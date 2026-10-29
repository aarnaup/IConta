'use strict';
// ============================================================ utilidades
// La API se sirve aparte: su dirección llega en /config.js (vacía = mismo origen). Todas las llamadas pasan por apiFetch.
const API = String(window.ICONTA_CONFIG?.api || '').replace(/\/+$/, '');
const apiFetch = (url, opt) => fetch(API + url, opt);
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const fmt = n => new Intl.NumberFormat('es-ES', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(n || 0);
const num = s => parseFloat(String(s ?? '').replace(/\s/g, '').replace(',', '.')) || 0;
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fechaES = d => new Date(d + 'T00:00:00').toLocaleDateString('es-ES');
const hoy = () => new Date().toISOString().slice(0, 10);
const ORDEN = { NINGUNO: 0, LECTURA: 1, ESCRITURA: 2 };
const MODULOS = { CONTABILIDAD: 'Contabilidad', FACTURACION: 'Facturación', TESORERIA: 'Tesorería', INMOVILIZADO: 'Inmovilizado',
                  ANALITICA: 'Analítica', PRESUPUESTOS: 'Presupuestos', BALANCES: 'Balances', FISCAL: 'Fiscal' };
const msg = (el, texto, cls) => { el.textContent = texto; el.className = cls || ''; };

// ============================================================ estado y API
const S = {
  token: sessionStorage.getItem('token'),
  user: JSON.parse(sessionStorage.getItem('usuario') || 'null'),
  empresa: null, ejercicio: null, acceso: null,
  cuentas: [], canales: [], diarios: [], lineas: [], tok: 0
};
const urlE = p => `/api/empresas/${S.empresa.id}${p}`;
const urlX = p => `/api/empresas/${S.empresa.id}/ejercicios/${S.ejercicio.id}${p}`;
const baseCanales = () => S.canales.filter(c => c.tipo === 'BASE');

async function api(url, opt = {}) {
  const headers = { ...(opt.headers || {}) };
  if (opt.body) headers['Content-Type'] = 'application/json';
  if (S.token) headers.Authorization = 'Bearer ' + S.token;
  const r = await apiFetch(url, { ...opt, headers });
  if (r.status === 401 && S.token) { salir('La sesión ha caducado. Vuelve a entrar.'); throw new Error('Sesión caducada'); }
  if (!r.ok) {
    let m = r.statusText;
    try { const j = await r.json(); m = (j.error || m) + (j.detalle ? ' (' + j.detalle + ')' : ''); } catch { /* sin cuerpo */ }
    throw new Error(m);
  }
  return r.status === 204 ? null : r.json();
}
const post = (url, body) => api(url, { method: 'POST', body: JSON.stringify(body ?? {}) });
const put = (url, body) => api(url, { method: 'PUT', body: JSON.stringify(body ?? {}) });

function guardaSesion(r) {
  S.token = r.token; S.user = r.usuario;
  sessionStorage.setItem('token', r.token);
  sessionStorage.setItem('usuario', JSON.stringify(r.usuario));
}

function salir(mensaje) {
  sessionStorage.clear();
  Object.assign(S, { token: null, user: null, empresa: null, ejercicio: null, acceso: null });
  vista('login');
  msg($('#loginMsg'), mensaje || '', mensaje ? 'ayuda err-texto' : 'ayuda');
}

// ============================================================ vistas
const VISTAS = ['login', 'cambio', 'empresas', 'admin', 'app'];
function vista(id) {
  VISTAS.forEach(v => $('#vista-' + v).hidden = v !== id);
  $('#cabecera').hidden = id === 'login';
  $('#usuarioNombre').textContent = S.user ? S.user.nombre : '';
  $('#btnCambiar').hidden = !(id === 'app' || id === 'admin');
  $('#btnCambiar').textContent = id === 'admin' ? 'Volver a las empresas' : 'Cambiar empresa';
  $('#contexto').textContent = id === 'app' ? S.empresa.nombre : id === 'admin' ? 'Administración' : '';
  // Ejercicio de trabajo en la cabecera: todo lo que se consulta o se graba va referido a él
  $('#ejCabL').hidden = id !== 'app';
  if (id === 'app')
    $('#ejCab').innerHTML = (S.listaEjercicios || []).map(x => `<option value="${x.id}" ${x.id === S.ejercicio.id ? 'selected' : ''}>${esc(x.codigo)}` +
      `${x.estado === 'ABIERTO' ? '' : ' (' + esc(x.estado.toLowerCase()) + ')'}</option>`).join('');
}

$('#ejCab').addEventListener('change', async e => {
  const nuevo = (S.listaEjercicios || []).find(x => String(x.id) === e.target.value);
  if (!nuevo || nuevo.id === S.ejercicio.id) return;
  const tab = ($$('#navApp button').find(b => b.getAttribute('aria-pressed') === 'true') || {}).dataset?.tab;
  try { await entrarEmpresa(S.empresa, nuevo, tab); }
  catch (err) { e.target.value = S.ejercicio.id; alert(err.message); }
});

$('#btnSalir').onclick = () => salir('');
$('#btnCambiar').onclick = () => irSelector().catch(e => msg($('#selMsg'), e.message, 'ayuda err-texto'));

// ============================================================ login y cambio de contraseña
$('#formLogin').addEventListener('submit', async e => {
  e.preventDefault();
  msg($('#loginMsg'), '', 'ayuda');
  try {
    const r = await post('/api/auth/login', { email: $('#loginEmail').value, password: $('#loginPassword').value });
    $('#loginPassword').value = '';
    guardaSesion(r);
    await despuesDeLogin();
  } catch (err) { msg($('#loginMsg'), err.message, 'ayuda err-texto'); }
});

$('#formCambio').addEventListener('submit', async e => {
  e.preventDefault();
  try {
    const r = await post('/api/auth/cambiar-password', { actual: $('#cpActual').value, nueva: $('#cpNueva').value });
    $('#cpActual').value = ''; $('#cpNueva').value = '';
    guardaSesion(r);
    await despuesDeLogin();
  } catch (err) { msg($('#cambioMsg'), err.message, 'ayuda err-texto'); }
});

async function despuesDeLogin() {
  if (S.user.debeCambiarPassword) { vista('cambio'); return; }
  await irSelector();
}

// ============================================================ selector de empresa y ejercicio
async function irSelector() {
  S.empresa = null; S.ejercicio = null; S.acceso = null;
  msg($('#selMsg'), '', 'ayuda');
  const empresas = await api('/api/mis-empresas');
  $('#paso-empresa').hidden = false; $('#paso-ejercicio').hidden = true;
  $('#btnAdmin').hidden = !(S.user && S.user.esSuperadmin);
  $('#listaEmpresas').innerHTML = empresas.length
    ? empresas.map(e => `<button type="button" data-id="${e.id}"><span>${esc(e.nombre)}<br><small>${esc(e.nif)} · ${esc(e.impuestoIndirecto)}</small></span><small>${esc(e.rolEnEmpresa)}</small></button>`).join('')
    : '<p class="vacio">No tienes empresas asignadas. Pide acceso a tu gestor.</p>';
  S.listaEmpresas = empresas;
  vista('empresas');
}

$('#listaEmpresas').addEventListener('click', async e => {
  const b = e.target.closest('button[data-id]'); if (!b) return;
  const empresa = S.listaEmpresas.find(x => String(x.id) === b.dataset.id);
  try {
    const ejercicios = await api(`/api/empresas/${empresa.id}/ejercicios`);
    S.empresaElegida = empresa; S.listaEjercicios = ejercicios;
    let ultimo = null;
    try { ultimo = localStorage.getItem('iconta.ejercicio.' + empresa.id); } catch { /* sin almacenamiento */ }
    $('#tituloEjercicio').textContent = `${empresa.nombre}: ¿qué ejercicio?`;
    $('#listaEjercicios').innerHTML = ejercicios.length
      ? ejercicios.map(x => `<button type="button" data-id="${x.id}"><span>Ejercicio ${esc(x.codigo)}${x.esPartido ? ' (partido)' : ''}${String(x.id) === ultimo ? ' · <small>último usado</small>' : ''}<br><small>${fechaES(x.fechaInicio)} – ${fechaES(x.fechaFin)}</small></span>
          <small class="${x.estado === 'ABIERTO' ? '' : 'estado-cerrado'}">${esc(x.estado.toLowerCase())}</small></button>`).join('')
      : '<p class="vacio">Esta empresa todavía no tiene ejercicios. Pide a tu gestor que cree uno.</p>';
    $('#paso-empresa').hidden = true; $('#paso-ejercicio').hidden = false;
    msg($('#selMsg'), '', 'ayuda');
  } catch (err) { msg($('#selMsg'), err.message, 'ayuda err-texto'); }
});

$('#btnVolverEmpresas').onclick = () => { $('#paso-empresa').hidden = false; $('#paso-ejercicio').hidden = true; };
$('#btnAdmin').onclick = () => entrarAdmin().catch(e => msg($('#selMsg'), e.message, 'ayuda err-texto'));

$('#listaEjercicios').addEventListener('click', async e => {
  const b = e.target.closest('button[data-id]'); if (!b) return;
  const ejercicio = S.listaEjercicios.find(x => String(x.id) === b.dataset.id);
  try { await entrarEmpresa(S.empresaElegida, ejercicio); }
  catch (err) { msg($('#selMsg'), err.message, 'ayuda err-texto'); }
});

// ============================================================ portal de empresa
function permitido(req) {
  if (!S.acceso) return false;
  if (req === 'TESORERIA_DIRECTO') return S.acceso.puedeContabilizarDirecto && ORDEN[S.acceso.permisos.TESORERIA] >= ORDEN.ESCRITURA;
  if (req === 'FISCAL_DIRECTO') return S.acceso.puedeContabilizarDirecto && ORDEN[S.acceso.permisos.FISCAL] >= ORDEN.ESCRITURA;
  if (req === 'INMOVILIZADO_DIRECTO') return S.acceso.puedeContabilizarDirecto && ORDEN[S.acceso.permisos.INMOVILIZADO] >= ORDEN.ESCRITURA;
  if (req === 'BALANCES') return ['BALANCES', 'CONTABILIDAD'].some(m => ORDEN[S.acceso.permisos[m]] >= ORDEN.LECTURA);
  if (req === 'ANALITICA') return ['ANALITICA', 'CONTABILIDAD'].some(m => ORDEN[S.acceso.permisos[m]] >= ORDEN.LECTURA);
  if (req === 'PRESUPUESTOS') return ['PRESUPUESTOS', 'CONTABILIDAD'].some(m => ORDEN[S.acceso.permisos[m]] >= ORDEN.LECTURA);
  if (req === 'CONTABILIDAD_DIRECTO') return S.acceso.puedeContabilizarDirecto && ORDEN[S.acceso.permisos.CONTABILIDAD] >= ORDEN.ESCRITURA;
  if (req === 'REVISOR')
    return S.acceso.puedeContabilizarDirecto && ['CONTABILIDAD', 'FACTURACION'].some(m => ORDEN[S.acceso.permisos[m]] >= ORDEN.ESCRITURA);
  const [m, n] = req.split(':');
  return ORDEN[S.acceso.permisos[m]] >= ORDEN[n];
}
const aplicaPermisos = () => $$('#vista-app [data-req]').forEach(el => el.hidden = !permitido(el.dataset.req));

async function entrarEmpresa(empresa, ejercicio, tab) {
  S.empresa = empresa; S.ejercicio = ejercicio;
  try { localStorage.setItem('iconta.ejercicio.' + empresa.id, String(ejercicio.id)); } catch { /* sin almacenamiento */ }
  S.acceso = await api(urlE('/mi-acceso'));
  aplicaPermisos();

  aplicaEstadoEjercicio();
  $('#avisoBorrador').hidden = S.acceso.puedeContabilizarDirecto;

  // Las fechas de trabajo quedan acotadas al ejercicio elegido
  const dentro = d => d < ejercicio.fechaInicio ? ejercicio.fechaInicio : d > ejercicio.fechaFin ? ejercicio.fechaFin : d;
  for (const id of ['#fecha', '#fFecha']) {
    $(id).min = ejercicio.fechaInicio; $(id).max = ejercicio.fechaFin; $(id).value = dentro(hoy());
  }
  // Años y fechas por defecto de cada pestaña: los del ejercicio (se rellenan al abrirla)
  for (const id of ['#noFecha', '#anDesde', '#anHasta', '#lrAnio', '#siiAnio', '#moAnio', '#ccAnio', '#prAnio', '#prRegAnio']) $(id).value = '';
  $('#mfAnio').value = dentro(hoy()).slice(0, 4);

  const pide = [];
  if (permitido('CONTABILIDAD:LECTURA') || permitido('FACTURACION:LECTURA'))
    pide.push(api(urlE('/cuentas')), api(urlE('/canales')), api(urlE('/diarios')));
  [S.cuentas = [], S.canales = [], S.diarios = []] = await Promise.all(pide);

  $('#dl-cuentas').innerHTML = S.cuentas.map(c => `<option value="${esc(c.codigo)}">${esc(c.nombre)}</option>`).join('');
  $('#diario').innerHTML = S.diarios.map(d => `<option value="${d.id}">${esc(d.codigo)} · ${esc(d.nombre)}</option>`).join('');
  $('#mayorCanal').innerHTML = '<option value="">Todos los canales</option>' + baseCanales().map(c => `<option value="${c.id}">Canal ${esc(c.codigo)}</option>`).join('');
  await cargaDimensiones().catch(() => { ANA.dims = []; });
  S.lineas = [nuevaLinea(), nuevaLinea()]; S.editando = null;
  $('#asEditando').hidden = true; $('#diario').disabled = false; $('#guardar').textContent = 'Contabilizar asiento';
  fst.lineas = [];
  PR.actual = null; $('#ppPanel').hidden = true; ANA.informe = null; $('#anBody').innerHTML = ''; $('#anDesde').value = ''; $('#anCanal').innerHTML = '';
  MF.modelos = []; MF.actual = null; MF.presentando = null; MF.liq = null;
  $('#mfResultado').hidden = true; $('#mfPresentarPanel').hidden = true; $('#mfLiqPanel').hidden = true;
  TES.liq = null; $('#liqPanel').hidden = true;
  IN.ficha = null; $('#inFicha').hidden = true; $('#inNuevo').hidden = true;
  RE.terceros = []; RE.detalle = null; $('#reDetalle').hidden = true; $('#reCandidatos').hidden = true;
  pintaLineas(); recalcula();

  vista('app');
  const primera = $$('#navApp button').find(b => !b.hidden && (!tab || b.dataset.tab === tab)) || $$('#navApp button').find(b => !b.hidden);
  if (primera) abreTab(primera.dataset.tab);
  else $$('#vista-app section').forEach(s => s.hidden = true);
}

// Un ejercicio cerrado solo se consulta: se desactivan los botones que graban
function aplicaEstadoEjercicio() {
  const cerrado = S.ejercicio.estado !== 'ABIERTO';
  $('#avisoEjercicio').hidden = !cerrado;
  $$('#vista-app [data-guarda]').forEach(b => b.disabled = cerrado);
}

function abreTab(nombre) {
  $$('#navApp button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.tab === nombre)));
  ['asiento', 'factura', 'diario', 'mayor', 'tesoreria', 'nominas', 'fiscal', 'pendientes', 'inmovilizado', 'balances', 'analitica', 'presupuestos', 'cierre'].forEach(t => $('#tab-' + t).hidden = t !== nombre);
  const carga = { factura: cargaFacturas, diario: cargaDiario, tesoreria: cargaTesoreria, nominas: cargaNominas, fiscal: cargaFiscal, pendientes: cargaPendientes,
                  inmovilizado: cargaInmovilizado, balances: cargaBalances, analitica: cargaAnalitica, presupuestos: cargaPresupuestos,
                  cierre: cargaCierre }[nombre];
  if (carga) carga().catch(e => alert(e.message));
}
$('#navApp').addEventListener('click', e => { const b = e.target.closest('button[data-tab]'); if (b) abreTab(b.dataset.tab); });

// ------------------------------------------------------------ nuevo asiento
const nuevaLinea = () => ({ cuenta: '', cuentaId: null, nombre: '', concepto: '', canalId: null, debe: '', haber: '', analitica: '' });
const canalOpts = sel => S.canales.map(c => `<option value="${c.id}" ${String(sel) === String(c.id) ? 'selected' : ''}>${esc(c.codigo)}</option>`).join('');

// Rejilla de apuntes como una hoja de cálculo: Enter pasa a la celda siguiente (y al final de la fila, a una fila nueva),
// las flechas arriba y abajo cambian de fila, Ctrl+Supr quita la fila y el importe que falta para cuadrar se propone solo.
function pintaLineas(foco) {
  const def = S.canales.find(c => c.codigo === 'A') || S.canales[0];
  S.lineas.forEach(l => { if (!l.canalId && def) l.canalId = def.id; });
  const ana = !$('#thAnalitica').hidden;
  $('#lineas').innerHTML = S.lineas.map((l, i) => `
    <tr data-i="${i}">
      <td class="fila">${i + 1}</td>
      <td><input data-f="cuenta" list="dl-cuentas" value="${esc(l.cuenta)}" autocomplete="off" class="${l.cuenta && !l.cuentaId ? 'mal' : ''}"
        placeholder="430.5 o nombre" aria-label="Cuenta apunte ${i + 1}"></td>
      <td class="nombre">${esc(l.nombre) || '<span style="opacity:.6">—</span>'}</td>
      <td><input data-f="concepto" value="${esc(l.concepto)}" placeholder="${esc($('#concepto').value || 'concepto del asiento')}" aria-label="Concepto apunte ${i + 1}"></td>
      <td><select data-f="canalId" aria-label="Canal apunte ${i + 1}">${canalOpts(l.canalId)}</select></td>
      ${ana ? `<td><input data-f="analitica" value="${esc(l.analitica)}" placeholder="por defecto" aria-label="Analítica apunte ${i + 1}"></td>` : ''}
      <td><input data-f="debe" class="num" inputmode="decimal" value="${esc(l.debe)}" aria-label="Debe apunte ${i + 1}"></td>
      <td><input data-f="haber" class="num" inputmode="decimal" value="${esc(l.haber)}" aria-label="Haber apunte ${i + 1}"></td>
      <td><button class="x" type="button" data-del="${i}" tabindex="-1" aria-label="Quitar apunte ${i + 1}">×</button></td>
    </tr>`).join('');
  if (foco) celda(foco.i, foco.f)?.focus();
}
const celda = (i, f) => $(`#lineas tr[data-i="${i}"] [data-f="${f}"]`);

// Dígitos de las subcuentas de la empresa (los del código más habitual)
function digitosCuenta() {
  const n = {}; S.cuentas.forEach(c => n[c.codigo.length] = (n[c.codigo.length] || 0) + 1);
  return +Object.entries(n).sort((a, b) => b[1] - a[1])[0]?.[0] || 8;
}
const sinTildes = s => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
// Cuenta a partir de lo tecleado: el código exacto, con punto (430.5 → 43000005), un principio de código o parte del nombre que
// solo encaje con una cuenta
function resuelveCuenta(texto) {
  const t = texto.trim(); if (!t) return null;
  const exacta = S.cuentas.find(c => c.codigo === t); if (exacta) return exacta;
  const m = t.match(/^(\d+)\.(\d*)$/);
  if (m) { const d = digitosCuenta(), relleno = d - m[1].length - m[2].length;
           return relleno >= 0 ? S.cuentas.find(c => c.codigo === m[1] + '0'.repeat(relleno) + m[2]) || null : null; }
  const cands = /^\d+$/.test(t) ? S.cuentas.filter(c => c.codigo.startsWith(t))
    : S.cuentas.filter(c => sinTildes(c.nombre).includes(sinTildes(t)));
  return cands.length === 1 ? cands[0] : null;
}
function fijaCuenta(tr, l, c) {
  l.cuentaId = c ? c.id : null; l.nombre = c ? c.nombre : ''; if (c) l.cuenta = c.codigo;
  const inp = tr.querySelector('[data-f=cuenta]');
  if (c) inp.value = c.codigo;
  inp.classList.toggle('mal', !!l.cuenta.trim() && !c);
  tr.querySelector('.nombre').textContent = l.nombre || (l.cuenta.trim() ? 'No existe' : '—');
}

$('#lineas').addEventListener('input', e => {
  const tr = e.target.closest('tr'); if (!tr) return;
  const l = S.lineas[+tr.dataset.i], f = e.target.dataset.f;
  l[f] = e.target.value;
  if (f === 'cuenta') { const c = S.cuentas.find(x => x.codigo === l.cuenta.trim()); fijaCuenta(tr, l, c); if (!c) e.target.classList.remove('mal'); }
  if (f === 'debe' && num(l.debe)) { l.haber = ''; tr.querySelector('[data-f=haber]').value = ''; }
  if (f === 'haber' && num(l.haber)) { l.debe = ''; tr.querySelector('[data-f=debe]').value = ''; }
  recalcula();
});
$('#lineas').addEventListener('change', e => {
  const tr = e.target.closest('tr'); if (!tr) return;
  const l = S.lineas[+tr.dataset.i];
  if (e.target.dataset.f === 'canalId') { l.canalId = +e.target.value; recalcula(); }
  if (e.target.dataset.f === 'cuenta') fijaCuenta(tr, l, resuelveCuenta(l.cuenta));
});
$('#lineas').addEventListener('click', e => {
  const d = e.target.dataset.del; if (d === undefined) return;
  quitaLinea(+d);
});
function quitaLinea(i) {
  S.lineas.splice(i, 1); if (!S.lineas.length) S.lineas.push(nuevaLinea());
  pintaLineas({ i: Math.min(i, S.lineas.length - 1), f: 'cuenta' }); recalcula();
}
function anadeLinea(foco = true) {
  S.lineas.push(nuevaLinea()); pintaLineas(foco ? { i: S.lineas.length - 1, f: 'cuenta' } : null); recalcula();
}

// Al entrar en el importe de una fila con cuenta y sin importe, se propone lo que falta para cuadrar
$('#lineas').addEventListener('focusin', e => {
  const f = e.target.dataset.f; if (f !== 'debe' && f !== 'haber') return;
  const tr = e.target.closest('tr'), i = +tr.dataset.i, l = S.lineas[i];
  if (!l.cuentaId || num(l.debe) || num(l.haber)) return;
  const dif = Math.round(S.lineas.reduce((s, x, j) => j === i ? s : s + num(x.debe) - num(x.haber), 0) * 100) / 100;
  if (!dif) return;
  const lado = dif > 0 ? 'haber' : 'debe', valor = Math.abs(dif).toFixed(2).replace('.', ',');
  l[lado] = valor; tr.querySelector(`[data-f=${lado}]`).value = valor; recalcula();
  // Se mueve el foco después de que termine el de esta celda (si no, el navegador lo devuelve aquí)
  setTimeout(() => { const c = celda(i, lado); if (lado !== f) c.focus(); c.select(); });
});

$('#lineas').addEventListener('keydown', e => {
  const f = e.target.dataset.f; if (!f) return;
  const tr = e.target.closest('tr'), i = +tr.dataset.i, l = S.lineas[i];
  if (e.key === 'Delete' && e.ctrlKey) { e.preventDefault(); return quitaLinea(i); }
  if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && f !== 'cuenta' && f !== 'canalId') {
    const j = i + (e.key === 'ArrowDown' ? 1 : -1);
    if (j >= 0 && j < S.lineas.length) { e.preventDefault(); celda(j, f).focus(); celda(j, f).select?.(); }
    return;
  }
  if (e.key !== 'Enter' || e.shiftKey) return;
  e.preventDefault();
  if (f === 'cuenta') fijaCuenta(tr, l, resuelveCuenta(e.target.value));
  // Desde el debe con importe se salta el haber; desde el final de la fila se pasa a la siguiente (o a una nueva)
  const celdas = [...tr.querySelectorAll('[data-f]')];
  let k = celdas.indexOf(e.target) + 1;
  if (f === 'debe' && num(l.debe)) k = celdas.length;
  if (k < celdas.length) { celdas[k].focus(); celdas[k].select?.(); return; }
  if (i + 1 < S.lineas.length) return celda(i + 1, 'cuenta').focus();
  const totD = S.lineas.reduce((s, x) => s + num(x.debe), 0), totH = S.lineas.reduce((s, x) => s + num(x.haber), 0);
  if (l.cuentaId && (num(l.debe) || num(l.haber)) && Math.round((totD - totH) * 100) !== 0) return anadeLinea();
  if (Math.round((totD - totH) * 100) === 0 && totD) return $('#guardar').focus();   // cuadrado: Enter en el botón contabiliza
  anadeLinea();
});
$('#concepto').addEventListener('input', () => $$('#lineas [data-f=concepto]').forEach(x => x.placeholder = $('#concepto').value || 'concepto del asiento'));
$('#addLinea').onclick = () => anadeLinea();
$('#fecha').onchange = recalcula;

let timer;
function recalcula() { clearTimeout(timer); timer = setTimeout(calcula, 200); }

async function calcula() {
  if (!S.empresa) return;
  const tok = ++S.tok, fecha = $('#fecha').value;
  $('#totDebe').textContent = fmt(S.lineas.reduce((s, l) => s + num(l.debe), 0));
  $('#totHaber').textContent = fmt(S.lineas.reduce((s, l) => s + num(l.haber), 0));
  const tot = Object.fromEntries(baseCanales().map(c => [c.id, { debe: 0, haber: 0 }]));
  let error = '';
  await Promise.all(S.lineas.filter(l => l.canalId && (num(l.debe) || num(l.haber))).map(l =>
    post(urlE('/canales/simular'), { canalId: l.canalId, fecha, debe: num(l.debe), haber: num(l.haber) })
      .then(rs => rs.forEach(r => { const t = tot[r.canalBaseId]; if (t) { t.debe += r.debe; t.haber += r.haber; } }))
      .catch(e => { error = e.message; })));
  if (tok !== S.tok) return;     // llegó una petición más reciente
  $('#reparto').innerHTML = baseCanales().map((c, i) => {
    const t = tot[c.id], dif = Math.round((t.debe - t.haber) * 100) / 100, ok = dif === 0;
    return `<tr class="canal-row" data-i="${i}"><td>Canal ${esc(c.codigo)}</td><td class="num">${fmt(t.debe)}</td><td class="num">${fmt(t.haber)}</td>
      <td class="${ok ? 'estado-ok' : 'estado-mal'}">${ok ? 'Cuadra' : 'Difiere ' + fmt(Math.abs(dif))}</td></tr>`;
  }).join('');
  if (error) msg($('#msg'), error, 'err'); else if ($('#msg').classList.contains('err')) msg($('#msg'), '', '');
}

async function guardarAsiento(borrador) {
  const lineas = S.lineas.filter(l => l.cuentaId && (num(l.debe) || num(l.haber)));
  if (S.lineas.some(l => (l.cuenta.trim() || num(l.debe) || num(l.haber)) && !l.cuentaId))
    return msg($('#msg'), 'Hay una cuenta que no existe en el plan de la empresa. Elige una de la lista.', 'err');
  let cmd;
  try {
    cmd = { fecha: $('#fecha').value, diarioId: +$('#diario').value, concepto: $('#concepto').value, borrador,
      apuntes: lineas.map(l => ({ cuentaId: l.cuentaId, canalId: l.canalId, debe: num(l.debe), haber: num(l.haber), concepto: l.concepto.trim() || $('#concepto').value.trim() || null,
                                  terceroId: l.terceroId ?? null, actividadId: l.actividadId ?? null, documento: l.documento || null,
                                  analitica: parseAnalitica(l.analitica) })) };
  } catch (e) { return msg($('#msg'), e.message, 'err'); }
  try {
    if (S.editando) {
      const r = await put(urlX('/asientos/' + S.editando.id), cmd);
      cierraEdicion();
      msg($('#msg'), `Asiento ${r.numero} modificado${r.estado === 'BORRADOR' ? ' (queda en borrador)' : ''}.`, 'ok');
      return;
    }
    const r = await post(urlX('/asientos'), cmd);
    msg($('#msg'), r.estado === 'CONTABILIZADO' ? `Asiento ${r.numero} contabilizado.`
      : `Asiento ${r.numero} guardado como borrador${r.forzadoBorrador ? ': queda pendiente de revisión' : ''}.`, 'ok');
    S.lineas = [nuevaLinea(), nuevaLinea()]; $('#concepto').value = '';
    pintaLineas(); calcula(); $('#concepto').focus();
  } catch (e) { msg($('#msg'), e.message, 'err'); }
}
$('#guardar').onclick = () => guardarAsiento(false);
$('#borrador').onclick = () => guardarAsiento(true);

// ------------------------------------------------------------ diario y mayor
async function cargaDiario() {
  const filas = await api(urlX('/asientos?limite=40'));
  if (!filas.length) { $('#diarioBody').innerHTML = '<tr><td colspan="5" class="vacio">Todavía no hay asientos en este ejercicio.</td></tr>'; return; }
  let ult = null, h = '';
  for (const f of filas) {
    if (f.asientoId !== ult) {
      ult = f.asientoId;
      const acc = f.bloqueo || !permitido('CONTABILIDAD:ESCRITURA') || S.ejercicio.estado !== 'ABIERTO'
        ? (f.bloqueo && f.estado !== 'ANULADO' ? `<span class="bloq" title="${esc(f.bloqueo)}">${esc(origenTexto(f.origen))}</span>` : '')
        : `<span class="acc"><button class="btn" type="button" data-modificar="${f.asientoId}">Modificar</button>
             <button class="btn" type="button" data-borrar-as="${f.asientoId}" data-num="${f.numero}">Borrar</button></span>`;
      h += `<tr class="asiento-cab"><td colspan="5">${esc(f.diario)} · Nº ${f.numero} · ${fechaES(f.fecha)} · ${esc(f.concepto)}${f.estado !== 'CONTABILIZADO' ? ' (' + esc(f.estado.toLowerCase()) + ')' : ''}${acc}</td></tr>`;
    }
    h += `<tr><td>${esc(f.cuenta)}</td><td class="nombre">${esc(f.cuentaNombre)}</td><td>${esc(f.canal)}</td><td class="num">${f.debe ? fmt(f.debe) : ''}</td><td class="num">${f.haber ? fmt(f.haber) : ''}</td></tr>`;
  }
  $('#diarioBody').innerHTML = h;
}

const origenTexto = o => ({ FACTURA: 'De una factura', TESORERIA: 'De un cobro o pago', AMORTIZACION: 'De la amortización', IVA: 'De la liquidación del IVA',
  APERTURA: 'Apertura', REGULARIZACION: 'Regularización', CIERRE: 'Cierre' }[o] || 'No modificable');

$('#diarioBody').addEventListener('click', async e => {
  const m = e.target.closest('button[data-modificar]'), b = e.target.closest('button[data-borrar-as]');
  if (m) {
    try { abreEdicion(await api(urlX('/asientos/' + m.dataset.modificar))); } catch (err) { msg($('#diarioMsg'), err.message, 'err-texto'); }
  } else if (b) {
    if (!confirm(`¿Borrar el asiento ${b.dataset.num}? No se puede deshacer.`)) return;
    try { await api(urlX('/asientos/' + b.dataset.borrarAs), { method: 'DELETE' }); msg($('#diarioMsg'), `Asiento ${b.dataset.num} borrado.`, 'ayuda'); await cargaDiario(); }
    catch (err) { msg($('#diarioMsg'), err.message, 'err-texto'); }
  }
});

// Carga un asiento en la rejilla para modificarlo
function abreEdicion(a) {
  const importe = n => n ? Number(n).toFixed(2).replace('.', ',') : '';
  const anaTexto = imp => imp.map(x => { const el = elemsAna().find(e => e.id === x.elementoId);
                                         return el ? `${el.dim}/${el.codigo}:${Number(x.porcentaje)}` : ''; }).filter(Boolean).join(' ');
  S.editando = { id: a.id, numero: a.numero };
  S.lineas = a.apuntes.map(p => ({ cuenta: p.cuenta, cuentaId: p.cuentaId, nombre: p.cuentaNombre, concepto: p.concepto === a.concepto ? '' : (p.concepto || ''),
    canalId: p.canalId, debe: importe(p.debe), haber: importe(p.haber), analitica: anaTexto(p.analitica),
    terceroId: p.terceroId, actividadId: p.actividadId, documento: p.documento }));
  $('#fecha').value = a.fecha; $('#diario').value = a.diarioId; $('#diario').disabled = true; $('#concepto').value = a.concepto;
  $('#asEditandoTxt').textContent = `Modificando el asiento nº ${a.numero}${a.estado === 'BORRADOR' ? ' (borrador)' : ''}`;
  $('#asEditando').hidden = false; $('#guardar').textContent = 'Guardar cambios';
  abreTab('asiento'); pintaLineas({ i: 0, f: 'cuenta' }); recalcula();
}
function cierraEdicion() {
  S.editando = null; $('#asEditando').hidden = true; $('#diario').disabled = false; $('#guardar').textContent = 'Contabilizar asiento';
  S.lineas = [nuevaLinea(), nuevaLinea()]; $('#concepto').value = ''; $('#fecha').value = hoy() >= S.ejercicio.fechaInicio && hoy() <= S.ejercicio.fechaFin ? hoy() : S.ejercicio.fechaInicio;
  pintaLineas(); recalcula();
}
$('#asCancelarEd').onclick = () => { cierraEdicion(); abreTab('diario'); };

$('#mayorBtn').onclick = async () => {
  const cuenta = $('#mayorCuenta').value.trim(); if (!cuenta) return;
  try {
    const filas = await api(urlX(`/mayor?cuenta=${encodeURIComponent(cuenta)}&canalBaseId=${$('#mayorCanal').value}`));
    $('#msgMayor').textContent = filas.length ? '' : 'Esa cuenta no tiene movimientos contabilizados en este ejercicio.';
    $('#mayorBody').innerHTML = filas.map(f => `<tr><td>${fechaES(f.fecha)}</td><td>${f.numero}</td><td>${esc(f.concepto)}</td><td>${esc(f.canal)}</td>
      <td class="num">${f.debe ? fmt(f.debe) : ''}</td><td class="num">${f.haber ? fmt(f.haber) : ''}</td><td class="num">${fmt(f.saldo)}</td></tr>`).join('');
  } catch (e) { $('#msgMayor').textContent = e.message; }
};

// ------------------------------------------------------------ facturas
const fst = { tipos: [], rets: [], terceros: [], formas: [], lineas: [] };
const nuevaFL = () => ({ tipoIvaId: null, base: '', regla: 'TOTAL', pct: '', op: '', inv: false });
// Operaciones por libro ('' = interior, o exenta si el tipo es exento). En el IGIC no hay intracomunitarias.
const OPS_FACTURA = {
  EMITIDAS: [['', 'Interior'], ['NO_SUJETA', 'No sujeta (arts. 7 y 14)'], ['NO_SUJETA_LOCAL', 'No sujeta por localización'],
             ['NO_SUJETA_OSS', 'No sujeta: ventanilla única (OSS)'], ['INTRACOM_ENTREGA', 'Entrega intracomunitaria (exenta)'],
             ['EXPORTACION', 'Exportación (exenta)'], ['INVERSION_SUJETO_PASIVO', 'Inversión del sujeto pasivo'],
             ['COMPENSACION_REAGP', 'Compensación REAGP (empresa en el régimen)']],
  RECIBIDAS: [['', 'Interior'], ['NO_SUJETA', 'No sujeta'], ['INTRACOM_ADQUISICION', 'Adquisición intracomunitaria'],
              ['INVERSION_SUJETO_PASIVO', 'Inversión del sujeto pasivo'], ['IMPORTACION', 'Importación (DUA)'],
              ['COMPENSACION_REAGP', 'Compensación REAGP (agricultor, ganadero o pescador)']]
};
const opsFactura = () => OPS_FACTURA[$('#fLibro').value].filter(([v]) => !(S.empresa && S.empresa.impuestoIndirecto === 'IGIC' && v.startsWith('INTRACOM')));
const esEmitida = () => $('#fLibro').value === 'EMITIDAS';
const setF = (t, c) => msg($('#fMsg'), t, c);

async function cargaFacturas() {
  if (permitido('FACTURACION:ESCRITURA')) {
    const f = $('#fFecha').value || hoy();
    const tipo = esEmitida() ? 'CLIENTE' : 'PROVEEDOR';
    [fst.tipos, fst.rets, fst.terceros, fst.formas] = await Promise.all([
      api(urlE('/tipos-iva?fecha=' + f)), api(urlE('/tipos-retencion?fecha=' + f)), api(urlE('/terceros?tipo=' + tipo)),
      api(urlE('/formas-pago'))]);
    const formaActual = $('#fForma').value;
    $('#fForma').innerHTML = '<option value="">La del tercero (o contado)</option>' + fst.formas.map(x => `<option value="${x.id}">${esc(x.nombre)}</option>`).join('');
    $('#fForma').value = formaActual;
    $('#fTercero').innerHTML = fst.terceros.length
      ? fst.terceros.map(t => `<option value="${t.id}">${esc(t.nombre)} (${esc(t.nif)})</option>`).join('')
      : '<option value="">No hay terceros: crea uno abajo</option>';
    $('#fCanal').innerHTML = S.canales.map(c => `<option value="${c.id}" ${c.codigo === 'A' ? 'selected' : ''}>${esc(c.codigo)} · ${esc(c.nombre)}</option>`).join('');
    $('#fRet').innerHTML = '<option value="">Sin retención</option>' + fst.rets.map(r => `<option value="${r.id}">${esc(r.nombre)} (${fmt(r.porcentaje)} %)</option>`).join('');
    if (!fst.lineas.length) fst.lineas = [nuevaFL()];
    fst.lineas.forEach(l => { if (!fst.tipos.some(t => t.id === l.tipoIvaId)) l.tipoIvaId = (fst.tipos.find(t => !t.esExento) || fst.tipos[0] || {}).id ?? null; });
    pintaFL();
  }
  await listaFacturas();
}

function pintaFL() {
  $('#thDed').style.display = esEmitida() ? 'none' : '';
  $('#fLineas').innerHTML = fst.lineas.map((l, i) => `
    <tr data-i="${i}">
      <td><select data-f="tipoIvaId" aria-label="Tipo de impuesto ${i + 1}">${fst.tipos.map(t =>
        `<option value="${t.id}" ${t.id === l.tipoIvaId ? 'selected' : ''}>${esc(t.nombre)} (${fmt(t.porcentaje)} %${t.recargo ? ' + rec. ' + fmt(t.recargo) : ''})</option>`).join('')}</select></td>
      <td><input data-f="base" class="num" inputmode="decimal" value="${esc(l.base)}" aria-label="Base ${i + 1}"></td>
      <td><select data-f="op" aria-label="Operación ${i + 1}">${opsFactura().map(([v, n]) => `<option value="${v}" ${v === l.op ? 'selected' : ''}>${esc(n)}</option>`).join('')}</select></td>
      <td ${esEmitida() ? 'style="display:none"' : ''}>
        <select data-f="regla" aria-label="Deducción ${i + 1}">
          <option value="TOTAL" ${l.regla === 'TOTAL' ? 'selected' : ''}>Deducible al 100 %</option>
          <option value="PRORRATA" ${l.regla === 'PRORRATA' ? 'selected' : ''}>Prorrata</option>
          <option value="PORCENTAJE_AFECTACION" ${l.regla === 'PORCENTAJE_AFECTACION' ? 'selected' : ''}>% de afectación</option>
          <option value="NINGUNA" ${l.regla === 'NINGUNA' ? 'selected' : ''}>No deducible</option>
        </select>
        ${l.regla === 'PORCENTAJE_AFECTACION' ? `<input data-f="pct" class="num" inputmode="decimal" value="${esc(l.pct)}" placeholder="% afectación" aria-label="% afectación ${i + 1}">` : ''}
        <label class="ayuda"><input type="checkbox" data-f="inv" ${l.inv ? 'checked' : ''}> Bien de inversión</label>
      </td>
      <td><button class="x" type="button" data-fdel="${i}" aria-label="Quitar línea ${i + 1}">×</button></td>
    </tr>`).join('');
  pinta349();
}

$('#fLineas').addEventListener('input', e => {
  const tr = e.target.closest('tr'); if (!tr || !e.target.dataset.f) return;
  const f = e.target.dataset.f;
  fst.lineas[+tr.dataset.i][f] = f === 'tipoIvaId' ? +e.target.value : f === 'inv' ? e.target.checked : e.target.value;
});
$('#fLineas').addEventListener('change', e => { if (e.target.dataset.f === 'regla') pintaFL(); if (e.target.dataset.f === 'op') pinta349(); });
$('#fLineas').addEventListener('click', e => {
  const d = e.target.dataset.fdel; if (d === undefined) return;
  fst.lineas.splice(+d, 1); if (!fst.lineas.length) fst.lineas.push(nuevaFL()); pintaFL();
});
$('#fAdd').onclick = () => { const l = nuevaFL(); l.tipoIvaId = (fst.tipos[0] || {}).id ?? null; fst.lineas.push(l); pintaFL(); };
$('#fLibro').onchange = () => { fst.lineas.forEach(l => { l.regla = 'TOTAL'; l.op = ''; l.inv = false; }); $('#fPreview').hidden = true; cargaFacturas().catch(e => setF(e.message, 'err')); };
$('#fFecha').onchange = () => cargaFacturas().catch(e => setF(e.message, 'err'));

const CLAVES_349 = { EMITIDAS: [['E', 'E · Entregas de bienes'], ['S', 'S · Servicios prestados'], ['T', 'T · Operación triangular']],
                     RECIBIDAS: [['A', 'A · Adquisiciones de bienes'], ['I', 'I · Servicios recibidos']] };
function pinta349() {
  const hay = fst.lineas.some(l => (l.op || '').startsWith('INTRACOM')), lib = $('#fLibro').value;
  $('#f349Label').hidden = !hay;
  if ($('#f349').dataset.libro !== lib) {
    $('#f349').innerHTML = CLAVES_349[lib].map(([v, n]) => `<option value="${v}">${esc(n)}</option>`).join(''); $('#f349').dataset.libro = lib;
  }
}
function cmdFactura(borrador) {
  return {
    libro: $('#fLibro').value, tipoFactura: 'ORDINARIA', numero: $('#fNumero').value.trim(),
    terceroId: +$('#fTercero').value || 0, formaPagoId: +$('#fForma').value || null, canalId: +$('#fCanal').value || 0,
    fechaExpedicion: $('#fFecha').value, tipoRetencionId: +$('#fRet').value || null, borrador,
    clave349: fst.lineas.some(l => (l.op || '').startsWith('INTRACOM')) ? $('#f349').value : null,
    inmuebleId: !$('#fInmL').hidden && $('#fInm').value ? +$('#fInm').value : null,
    lineas: fst.lineas.filter(l => l.tipoIvaId && num(l.base)).map(l => {
      const t = fst.tipos.find(x => x.id === l.tipoIvaId);
      return { tipoIvaId: l.tipoIvaId, base: num(l.base), tipoOperacion: l.op || (t && t.esExento ? 'EXENTA' : 'INTERIOR'),
               reglaDeduccion: esEmitida() ? 'TOTAL' : l.regla, porcentajeDeduccion: l.regla === 'PORCENTAJE_AFECTACION' ? num(l.pct) : null,
               esBienInversion: !esEmitida() && !!l.inv };
    })
  };
}

function pintaPreview(r) {
  const t = r.calculo.totales;
  const caja = (n, v) => `<div><span>${n}</span><strong>${fmt(v)}</strong></div>`;
  $('#fTotales').innerHTML = caja('Base', t.base) + caja('Cuota', t.cuota)
    + (esEmitida() ? '' : caja('Cuota deducible', t.cuotaDeducible) + caja('No deducible', t.cuotaNoDeducible))
    + (t.cuotaAutorrepercutida ? caja('Autorrepercutida', t.cuotaAutorrepercutida) : '')
    + (t.recargo ? caja('Recargo', t.recargo) : '') + (t.retencion ? caja('Retención', t.retencion) : '') + caja('Total', t.total);
  $('#fAsiento').innerHTML = r.asiento.map(a => `<tr><td>${esc(a.cuenta)}</td><td class="nombre">${esc(a.cuentaNombre)}</td>
    <td>${esc((S.canales.find(c => c.id === a.canalId) || {}).codigo || '')}</td>
    <td class="num">${a.debe ? fmt(a.debe) : ''}</td><td class="num">${a.haber ? fmt(a.haber) : ''}</td></tr>`).join('');
  $('#fPreview').hidden = false;
}

$('#fPrev').onclick = async () => {
  try { const r = await post(urlX('/facturas/previsualizar'), cmdFactura(false)); pintaPreview(r); setF(`Plantilla: ${r.plantillaNombre}`, ''); }
  catch (e) { $('#fPreview').hidden = true; setF(e.message, 'err'); }
};

async function guardarFactura(borrador) {
  try {
    const r = await post(urlX('/facturas'), cmdFactura(borrador));
    setF(r.estado === 'CONTABILIZADA' ? `Factura contabilizada en el asiento ${r.asientoNumero}.`
      : `Factura guardada como borrador${r.forzadoBorrador ? ': queda pendiente de revisión' : ''}.`, 'ok');
    fst.lineas = [nuevaFL()]; $('#fNumero').value = ''; $('#fPreview').hidden = true;
    await cargaFacturas();
  } catch (e) { setF(e.message, 'err'); }
}
$('#fGuardar').onclick = () => guardarFactura(false);
$('#fBorrador').onclick = () => guardarFactura(true);

async function listaFacturas() {
  const filas = await api(urlX('/facturas?limite=30'));
  $('#fLista').innerHTML = filas.length ? filas.map(f => `<tr><td>${f.libro === 'EMITIDAS' ? 'Emitida' : 'Recibida'}</td><td>${esc((f.serie || '') + f.numero)}</td>
    <td>${fechaES(f.fecha)}</td><td>${esc(f.tercero)}</td>
    <td class="num">${fmt(f.baseImponible)}</td><td class="num">${fmt(f.cuota)}</td><td class="num">${f.retencion ? fmt(f.retencion) : ''}</td>
    <td class="num">${fmt(f.total)}</td><td>${f.asientoNumero ? 'asiento ' + f.asientoNumero : esc(f.estado.toLowerCase())}</td>
    <td>${f.libro === 'EMITIDAS' && f.estado !== 'ANULADA' ? `<button type="button" class="btn" data-impreso="${f.id}">Imprimir</button>` : ''}</td></tr>`).join('')
    : '<tr><td colspan="10" class="vacio">Todavía no hay facturas en este ejercicio.</td></tr>';
  $('#fLista').querySelectorAll('[data-impreso]').forEach(b => b.onclick = () => imprimeFactura(b.dataset.impreso));
}

// Impreso de la factura: se abre en otra ventana y se lanza el diálogo de imprimir (desde ahí se guarda en PDF)
async function imprimeFactura(id) {
  const w = window.open('', '_blank');
  try {
    const r = await apiFetch(urlX(`/facturas/${id}/impreso`), { headers: { Authorization: 'Bearer ' + S.token } });
    if (!r.ok) { let m = r.statusText; try { m = (await r.json()).error || m; } catch { /* sin cuerpo */ } throw new Error(m); }
    const url = URL.createObjectURL(new Blob([await r.text()], { type: 'text/html' }));
    if (!w) { location.assign(url); return; }
    w.location = url;
    w.addEventListener('load', () => { w.focus(); w.print(); }, { once: true });
  } catch (e) { if (w) w.close(); alert(e.message); }
}

$('#tCrear').onclick = async () => {
  const tipo = $('#tTipo').value;
  try {
    await post(urlE('/terceros'), { nif: $('#tNif').value, nombre: $('#tNombre').value, esCliente: tipo === 'CLIENTE' || tipo === 'AMBOS',
      esProveedor: tipo === 'PROVEEDOR' || tipo === 'AMBOS', esEmpleado: tipo === 'EMPLEADO',
      codigoPostal: $('#tCp').value || null, pais: $('#tPais').value || 'ES', tipoDocumento: ($('#tPais').value || 'ES') === 'ES' ? 'NIF' : 'VIES',
      criterioCaja: (tipo === 'PROVEEDOR' || tipo === 'AMBOS') && $('#tCaja').checked });
    $('#tCaja').checked = false;
    msg($('#tMsg'), 'Tercero creado.', 'ok-texto'); $('#tNif').value = ''; $('#tNombre').value = ''; $('#tCp').value = '';
    await cargaFacturas();
  } catch (e) { msg($('#tMsg'), e.message, 'err-texto'); }
};


// ------------------------------------------------------------ nóminas
const NO = { nominas: [], empleados: [] };
const impNomina = () => {
  const bruto = num($('#noBruto').value), pct = $('#noPct').value.trim();
  const ret = $('#noRet').value.trim() ? num($('#noRet').value) : (pct ? Math.round(bruto * num(pct)) / 100 : 0);
  return { bruto, retencion: ret, ssTrabajador: num($('#noSsT').value), ssEmpresa: num($('#noSsE').value), anticipos: num($('#noAnt').value),
           especie: num($('#noEsp').value), ingresoACuenta: num($('#noIc').value), ingresoRepercutido: $('#noRep').checked };
};
function pintaLiquidoNomina() {
  const n = impNomina();
  const liq = n.bruto - n.retencion - n.ssTrabajador - n.anticipos - (n.ingresoRepercutido ? n.ingresoACuenta : 0);
  $('#noLiquido').textContent = n.bruto || n.especie ? `Retención ${fmt(n.retencion)} · líquido a percibir ${fmt(liq)} · coste para la empresa ${fmt(n.bruto + n.ssEmpresa + (n.ingresoRepercutido ? 0 : n.ingresoACuenta))}` : '';
}
['#noBruto', '#noPct', '#noRet', '#noSsT', '#noSsE', '#noAnt', '#noEsp', '#noIc'].forEach(i => $(i).addEventListener('input', pintaLiquidoNomina));
$('#noRep').addEventListener('change', pintaLiquidoNomina);
$('#noPct').addEventListener('input', () => { $('#noRet').value = ''; });

async function cargaNominas() {
  const [terceros, nominas, empleados] = await Promise.all([api(urlE('/terceros')), api(urlX('/nominas')), api(urlE('/empleados'))]);
  NO.nominas = nominas; NO.empleados = empleados;
  const sel = $('#noTercero').value;
  $('#noTercero').innerHTML = terceros.map(t => `<option value="${t.id}">${esc(t.nombre)} (${esc(t.nif || '')})</option>`).join('');
  if (sel) $('#noTercero').value = sel;
  $('#noCanal').innerHTML = canalOpts($('#noCanal').value || (S.canales.find(c => c.codigo === 'A') || S.canales[0] || {}).id);
  if (!$('#noFecha').value) $('#noFecha').value = S.ejercicio.fechaFin < new Date().toISOString().slice(0, 10) ? S.ejercicio.fechaFin : new Date().toISOString().slice(0, 10);
  const directo = permitido('CONTABILIDAD_DIRECTO');
  $('#noLista').innerHTML = nominas.map(n => `<tr class="${n.estado === 'ANULADA' ? 'estado-cerrado' : ''}"><td>${fechaES(n.fecha)}</td><td>${esc(n.empleado)}</td>
      <td>${esc(n.periodo || '')}</td><td class="num">${fmt(n.bruto)}</td><td class="num">${fmt(n.retencion)} <small>(${fmt(n.porcentajeRetencion)} %)</small></td>
      <td class="num">${fmt(n.ssTrabajador)}</td><td class="num">${fmt(n.ssEmpresa)}</td><td class="num">${n.especie ? fmt(n.especie) : ''}</td>
      <td class="num">${fmt(n.liquido)}</td><td>${n.estado === 'ANULADA' ? 'anulada' : (n.asientoNumero ? 'nº ' + n.asientoNumero : '')}</td>
      <td>${directo && n.estado !== 'ANULADA' ? `<button class="btn" type="button" data-anula="${n.id}">Anular</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="11" class="vacio">Todavía no hay nóminas en este ejercicio.</td></tr>';
  const escribe = permitido('CONTABILIDAD:ESCRITURA');
  const sit = v => [1, 2, 3].map(x => `<option ${x === v ? 'selected' : ''}>${x}</option>`).join('');
  $('#noEmpleados').innerHTML = empleados.map(e => `<tr data-id="${e.terceroId}"><td>${esc(e.nombre)}</td><td>${esc(e.nif)}</td>
      <td><input data-f="anioNacimiento" inputmode="numeric" style="width:6em" value="${e.anioNacimiento ?? ''}"></td>
      <td><select data-f="situacionFamiliar">${sit(e.situacionFamiliar)}</select></td>
      <td><input data-f="nifConyuge" maxlength="9" style="width:8em" value="${esc(e.nifConyuge || '')}"></td>
      <td><select data-f="discapacidad">${[0, 1, 2, 3].map(x => `<option ${x === e.discapacidad ? 'selected' : ''}>${x}</option>`).join('')}</select></td>
      <td><select data-f="contrato">${[1, 2, 3, 4].map(x => `<option ${x === e.contrato ? 'selected' : ''}>${x}</option>`).join('')}</select></td>
      <td><input type="checkbox" data-f="movilidadGeografica" ${e.movilidadGeografica ? 'checked' : ''}></td>
      <td>${escribe ? '<button class="btn" type="button" data-guarda-emp>Guardar</button>' : ''}</td></tr>`).join('')
    || '<tr><td colspan="9" class="vacio">Los trabajadores aparecen aquí con su primera nómina.</td></tr>';
  pintaLiquidoNomina();
}

$('#noGuardar').onclick = async () => {
  try {
    const n = await post(urlX('/nominas'), { terceroId: +$('#noTercero').value, canalId: +$('#noCanal').value, fecha: $('#noFecha').value,
      periodo: $('#noPeriodo').value || null, ...impNomina() });
    msg($('#noMsg'), `Nómina contabilizada: asiento nº ${n.asientoNumero}, líquido ${fmt(n.liquido)}.`, 'ok-texto');
    ['#noBruto', '#noPct', '#noRet', '#noSsT', '#noSsE', '#noPeriodo'].forEach(i => $(i).value = '');
    ['#noAnt', '#noEsp', '#noIc'].forEach(i => $(i).value = '0');
    await cargaNominas();
  } catch (e) { msg($('#noMsg'), e.message, 'err-texto'); }
};
$('#noLista').addEventListener('click', async e => {
  const b = e.target.closest('[data-anula]'); if (!b || !confirm('¿Anular la nómina y su asiento?')) return;
  try { await api(urlX(`/nominas/${b.dataset.anula}`), { method: 'DELETE' }); await cargaNominas(); msg($('#noListaMsg'), 'Nómina anulada.', 'ayuda ok-texto'); }
  catch (err) { msg($('#noListaMsg'), err.message, 'ayuda err-texto'); }
});
$('#noEmpleados').addEventListener('click', async e => {
  const b = e.target.closest('[data-guarda-emp]'); if (!b) return;
  const tr = b.closest('tr'), v = f => tr.querySelector(`[data-f=${f}]`);
  try {
    await put(urlE(`/empleados/${tr.dataset.id}`), { anioNacimiento: v('anioNacimiento').value ? +v('anioNacimiento').value : null,
      situacionFamiliar: +v('situacionFamiliar').value, nifConyuge: v('nifConyuge').value || null, discapacidad: +v('discapacidad').value,
      contrato: +v('contrato').value, movilidadGeografica: v('movilidadGeografica').checked });
    msg($('#noEmpMsg'), 'Datos del trabajador guardados.', 'ayuda ok-texto');
  } catch (err) { msg($('#noEmpMsg'), err.message, 'ayuda err-texto'); }
});

// ------------------------------------------------------------ tesorería
const TES = { venc: [], bancos: [], liq: null };
const estadoTxt = { PENDIENTE: 'pendiente', PARCIAL: 'parcial', LIQUIDADO: 'liquidado', ANULADO: 'anulado' };

async function cargaTesoreria() {
  await Promise.all([cargaPrevision(), cargaVencimientos(), cargaBancos()]);
  await Promise.all([cargaConciliacion(), cargaRemesas()]);
}

// ------------------------------------------------------------ remesas SEPA
const RE = { candidatos: [], sel: new Set(), detalle: null, terceros: [] };
const estadoRemesa = { GENERADA: 'Generada', CONTABILIZADA: 'Contabilizada', ANULADA: 'Anulada' };
const masDias = (d, n) => { const f = new Date(d + 'T00:00:00'); f.setDate(f.getDate() + n); return f.toISOString().slice(0, 10); };

async function cargaRemesas() {
  const propias = TES.bancos.filter(b => b.activa && b.cuentaContable);
  $('#reBanco').innerHTML = propias.map(b => `<option value="${b.id}">${esc(b.alias || b.iban)}</option>`).join('');
  if (!$('#reHasta').value) $('#reHasta').value = masDias(hoy(), 30);
  if (!$('#reFecha').value) $('#reFecha').value = masDias(hoy(), 3);
  if (!RE.terceros.length) {
    RE.terceros = await api(urlE('/terceros'));
    $('#mtTercero').innerHTML = RE.terceros.map(t => `<option value="${t.id}">${esc(t.nombre)} · ${esc(t.nif)}</option>`).join('');
  }
  await Promise.all([listaRemesas(), cargaCuentasTercero()]);
}

async function listaRemesas() {
  const filas = await api(urlE('/tesoreria/remesas'));
  const escribe = permitido('TESORERIA:ESCRITURA');
  $('#reLista').innerHTML = filas.map(r => `<tr><td>${esc(r.referencia)}</td><td>${r.tipo === 'COBRO' ? 'Cobros' : 'Pagos'}</td><td>${fechaES(r.fechaCargo)}</td>
      <td class="num">${r.numOperaciones}${r.devueltos ? ` <small>(${r.devueltos} devuelto${r.devueltos > 1 ? 's' : ''})</small>` : ''}</td><td class="num">${fmt(r.importe)}</td>
      <td>${estadoRemesa[r.estado] || esc(r.estado)}${r.asientoNumero ? ' · asiento nº ' + r.asientoNumero : ''}</td>
      <td class="fila-acciones"><button class="btn" data-ver="${r.id}">Ver</button>${r.estado !== 'ANULADA' ? `<button class="btn" data-xml="${r.id}">Fichero</button>` : ''}
        ${r.estado === 'GENERADA' && escribe ? `<button class="btn" data-anular="${r.id}">Anular</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="7" class="vacio">Todavía no hay remesas.</td></tr>';
}

$('#reBuscar').onclick = async () => {
  try {
    RE.candidatos = await api(urlE(`/tesoreria/remesas/candidatos?tipo=${$('#reTipo').value}&hasta=${$('#reHasta').value}`));
    RE.sel = new Set(RE.candidatos.filter(c => !c.problema).map(c => c.vencimientoId));
    $('#reCandBody').innerHTML = RE.candidatos.map(c => `<tr>
        <td><input type="checkbox" data-sel="${c.vencimientoId}" ${c.problema ? 'disabled' : 'checked'} aria-label="Incluir"></td>
        <td>${fechaES(c.fechaVencimiento)}</td><td>${esc(c.tercero)}</td><td>${esc(c.factura || '')}</td><td class="num">${fmt(c.importePendiente)}</td>
        <td>${c.iban ? esc(c.iban) : `<span class="err-texto">${esc(c.problema)}</span>`}</td><td>${esc(c.mandato || '')}${c.esquema && c.esquema !== 'CORE' ? ' (' + c.esquema + ')' : ''}</td></tr>`).join('')
      || '<tr><td colspan="7" class="vacio">No hay vencimientos pendientes hasta esa fecha.</td></tr>';
    $('#reCandidatos').hidden = false; pintaSelRemesa(); msg($('#reMsg'), '', 'ayuda');
  } catch (e) { msg($('#reMsg'), e.message, 'ayuda err-texto'); }
};
function pintaSelRemesa() {
  const elegidos = RE.candidatos.filter(c => RE.sel.has(c.vencimientoId));
  $('#reSeleccion').textContent = elegidos.length ? `${elegidos.length} vencimiento(s) · ${fmt(elegidos.reduce((a, c) => a + c.importePendiente, 0))}` : '';
  $('#reGenerar').disabled = !elegidos.length;
}
$('#reCandBody').addEventListener('change', e => {
  const id = e.target.dataset.sel; if (!id) return;
  e.target.checked ? RE.sel.add(+id) : RE.sel.delete(+id); pintaSelRemesa();
});
$('#reGenerar').onclick = async () => {
  try {
    const d = await post(urlE('/tesoreria/remesas'), { tipo: $('#reTipo').value, cuentaBancariaId: +$('#reBanco').value, fechaCargo: $('#reFecha').value, vencimientoIds: [...RE.sel] });
    $('#reCandidatos').hidden = true;
    await Promise.all([listaRemesas(), cargaVencimientos(), cargaPrevision(), cargaCuentasTercero()]);
    pintaDetalleRemesa(d); await descargaRemesa(d.remesa.id);
    msg($('#reMsg'), `Remesa ${d.remesa.referencia} generada: ${d.remesa.numOperaciones} operación(es) por ${fmt(d.remesa.importe)}. Sube el fichero a tu banca electrónica.`, 'ayuda ok-texto');
  } catch (e) { msg($('#reMsg'), e.message, 'ayuda err-texto'); }
};

async function descargaRemesa(id) {
  const r = await apiFetch(urlE(`/tesoreria/remesas/${id}/fichero`), { headers: { Authorization: 'Bearer ' + S.token } });
  if (!r.ok) { let m = r.statusText; try { m = (await r.json()).error || m; } catch { /* sin cuerpo */ } throw new Error(m); }
  const nombre = ((/filename=([^;]+)/.exec(r.headers.get('Content-Disposition') || '') || [])[1] || 'remesa.xml').replace(/"/g, '');
  const url = URL.createObjectURL(await r.blob());
  const a = Object.assign(document.createElement('a'), { href: url, download: nombre });
  document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
}

function pintaDetalleRemesa(d) {
  RE.detalle = d; const r = d.remesa;
  const devolver = r.tipo === 'COBRO' && r.estado === 'CONTABILIZADA' && permitido('TESORERIA_DIRECTO');
  $('#reDetTitulo').textContent = `${r.referencia} · ${r.tipo === 'COBRO' ? 'cobros' : 'pagos'} · ${fechaES(r.fechaCargo)} · ${fmt(r.importe)} · ${estadoRemesa[r.estado]}`;
  $('#reDetBody').innerHTML = d.lineas.map(l => `<tr><td>${esc(l.tercero)}</td><td>${esc(l.factura || '')}</td><td class="num">${fmt(l.importe)}</td>
      <td>${esc(l.iban)}</td><td>${esc(l.secuencia || '')}</td>
      <td>${l.estado === 'DEVUELTO' ? 'Devuelto el ' + fechaES(l.fechaDevolucion) + (l.motivoDevolucion ? ' · ' + esc(l.motivoDevolucion) : '') : 'Incluido'}</td>
      <td class="fila-acciones">${devolver && l.estado !== 'DEVUELTO' ? `<button class="btn" data-devolver="${l.id}" ${S.ejercicio.estado === 'ABIERTO' ? '' : 'disabled'}>Devolución</button>` : ''}</td></tr>`).join('');
  $('#reContabForm').hidden = r.estado !== 'GENERADA' || !permitido('TESORERIA_DIRECTO');
  const f = r.fechaCargo < S.ejercicio.fechaInicio ? S.ejercicio.fechaInicio : r.fechaCargo > S.ejercicio.fechaFin ? S.ejercicio.fechaFin : r.fechaCargo;
  $('#reContabFecha').value = f;
  $('#reDetalle').hidden = false;
}

$('#reLista').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  try {
    if (b.dataset.ver) { pintaDetalleRemesa(await api(urlE('/tesoreria/remesas/' + b.dataset.ver))); $('#reDetalle').scrollIntoView({ behavior: 'smooth', block: 'nearest' }); }
    if (b.dataset.xml) await descargaRemesa(b.dataset.xml);
    if (b.dataset.anular && confirm('¿Anular la remesa? Los vencimientos vuelven a quedar pendientes. Si ya enviaste el fichero al banco, anúlalo también allí.')) {
      await post(urlE(`/tesoreria/remesas/${b.dataset.anular}/anular`)); $('#reDetalle').hidden = true;
      await Promise.all([listaRemesas(), cargaVencimientos(), cargaCuentasTercero()]); msg($('#reMsg'), 'Remesa anulada.', 'ayuda');
    }
  } catch (err) { msg($('#reMsg'), err.message, 'ayuda err-texto'); }
});
$('#reContabilizar').onclick = async () => {
  if (!RE.detalle) return;
  try {
    const a = await post(urlX(`/tesoreria/remesas/${RE.detalle.remesa.id}/contabilizar`), { fecha: $('#reContabFecha').value });
    pintaDetalleRemesa(await api(urlE('/tesoreria/remesas/' + RE.detalle.remesa.id)));
    await Promise.all([listaRemesas(), cargaVencimientos(), cargaPrevision(), cargaBancos()]);
    msg($('#reMsg'), `Remesa contabilizada (asiento nº ${a.asientoNumero}).`, 'ayuda ok-texto');
  } catch (e) { msg($('#reMsg'), e.message, 'ayuda err-texto'); }
};
$('#reDetBody').addEventListener('click', async e => {
  const b = e.target.closest('button[data-devolver]'); if (!b) return;
  const motivo = prompt('Motivo de la devolución (p. ej. MD06, AC04, AM04):', ''); if (motivo === null) return;
  const gastos = num(prompt('Gastos de devolución que cobra el banco (0 si no hay):', '0'));
  let cuentaGastosId = null;
  if (gastos > 0) {
    const cod = prompt('Cuenta de los gastos (código, p. ej. 62600000):', '');
    const c = S.cuentas.find(x => x.codigo === (cod || '').trim());
    if (!c) return msg($('#reMsg'), 'Esa cuenta no existe.', 'ayuda err-texto');
    cuentaGastosId = c.id;
  }
  try {
    const a = await post(urlX(`/tesoreria/remesas/lineas/${b.dataset.devolver}/devolver`), { fecha: dentroEj(hoy()), motivo, gastos, cuentaGastosId });
    pintaDetalleRemesa(await api(urlE('/tesoreria/remesas/' + RE.detalle.remesa.id)));
    await Promise.all([listaRemesas(), cargaVencimientos(), cargaPrevision()]);
    msg($('#reMsg'), `Devolución contabilizada (asiento nº ${a.asientoNumero}). El recibo vuelve a estar pendiente.`, 'ayuda ok-texto');
  } catch (err) { msg($('#reMsg'), err.message, 'ayuda err-texto'); }
});

// Cuentas bancarias y mandatos de terceros
async function cargaCuentasTercero() {
  const t = $('#mtTercero').value; if (!t) return;
  const [cuentas, mandatos] = await Promise.all([api(urlE(`/terceros/${t}/cuentas-bancarias`)), api(urlE(`/tesoreria/mandatos?terceroId=${t}`))]);
  $('#mtCuentas').innerHTML = cuentas.map(c => `<tr><td>${esc(c.iban)}${c.activa ? '' : ' (inactiva)'}</td><td>${esc(c.bic || '')}</td>
      <td>${mandatos.filter(m => m.cuentaBancariaId === c.id).map(m => `${esc(m.referencia)} · ${m.esquema} · firmado ${fechaES(m.fechaFirma)}${m.usado ? ' · usado' : ''}${m.activo ? '' : ' · desactivado'}
        ${m.activo && permitido('TESORERIA:ESCRITURA') ? `<button class="btn" data-desactivar="${m.id}">Desactivar</button>` : ''}`).join('<br>')}</td></tr>`).join('')
    || '<tr><td colspan="3" class="vacio">Sin cuentas bancarias.</td></tr>';
  $('#mtCuenta').innerHTML = cuentas.filter(c => c.activa).map(c => `<option value="${c.id}">${esc(c.iban)}</option>`).join('');
  $('#mtMandatoForm').hidden = !cuentas.some(c => c.activa) || !permitido('TESORERIA:ESCRITURA');
  if (!$('#mtFirma').value) $('#mtFirma').value = hoy();
}
$('#mtTercero').onchange = () => cargaCuentasTercero().catch(e => msg($('#mtMsg'), e.message, 'ayuda err-texto'));
$('#mtAnadirCuenta').onclick = async () => {
  try {
    await post(urlE(`/terceros/${$('#mtTercero').value}/cuentas-bancarias`), { iban: $('#mtIban').value, bic: $('#mtBic').value || null });
    $('#mtIban').value = ''; $('#mtBic').value = ''; await cargaCuentasTercero(); msg($('#mtMsg'), 'Cuenta añadida.', 'ayuda ok-texto');
  } catch (e) { msg($('#mtMsg'), e.message, 'ayuda err-texto'); }
};
$('#mtCrear').onclick = async () => {
  try {
    const m = await post(urlE('/tesoreria/mandatos'), { terceroId: +$('#mtTercero').value, cuentaBancariaId: +$('#mtCuenta').value, fechaFirma: $('#mtFirma').value,
      esquema: $('#mtEsquema').value, referencia: $('#mtRef').value || null });
    $('#mtRef').value = ''; await cargaCuentasTercero(); msg($('#mtMsg'), `Mandato ${m.referencia} creado.`, 'ayuda ok-texto');
  } catch (e) { msg($('#mtMsg'), e.message, 'ayuda err-texto'); }
};
$('#mtCuentas').addEventListener('click', async e => {
  const b = e.target.closest('button[data-desactivar]'); if (!b || !confirm('¿Desactivar el mandato? No se podrán remesar más recibos con él.')) return;
  try { await post(urlE(`/tesoreria/mandatos/${b.dataset.desactivar}/desactivar`)); await cargaCuentasTercero(); msg($('#mtMsg'), 'Mandato desactivado.', 'ayuda'); }
  catch (err) { msg($('#mtMsg'), err.message, 'ayuda err-texto'); }
});

// ------------------------------------------------------------ conciliación bancaria
const CO = { estado: null, lineas: new Set(), apuntes: new Set(), accion: null };
const conceptoComun = { '01': 'Talones', '02': 'Abonarés', '03': 'Recibos', '04': 'Transferencias', '05': 'Amortizaciones', '06': 'Remesas',
  '07': 'Suscripciones', '08': 'Dividendos', '09': 'Operaciones de bolsa', '10': 'Cheques gasolina', '11': 'Cajero', '12': 'Tarjetas',
  '13': 'Operaciones exterior', '14': 'Devoluciones', '15': 'Nóminas', '16': 'Timbres', '17': 'Intereses', '98': 'Anulaciones', '99': 'Varios' };

async function cargaConciliacion() {
  const propias = TES.bancos.filter(b => b.activa && b.cuentaContable);
  $('#coPanel').hidden = !propias.length;
  if (!propias.length) return;
  const actual = $('#coBanco').value;
  $('#coBanco').innerHTML = propias.map(b => `<option value="${b.id}">${esc(b.alias || b.iban)}</option>`).join('');
  if (propias.some(b => String(b.id) === actual)) $('#coBanco').value = actual;
  if (!$('#coHasta').value) $('#coHasta').value = hoy();
  $('#coCanal').innerHTML = S.canales.map(c => `<option value="${c.id}">${esc(c.codigo)}</option>`).join('');
  await verConciliacion();
}

async function verConciliacion() {
  const banco = $('#coBanco').value; if (!banco) return;
  cargaReglasConciliacion().catch(e => msg($('#coMsg'), e.message, 'ayuda err-texto'));
  const [e, extractos] = await Promise.all([
    api(urlE(`/tesoreria/conciliacion?cuentaBancariaId=${banco}&hasta=${$('#coHasta').value}`)),
    api(urlE(`/tesoreria/extractos?cuentaBancariaId=${banco}`))]);
  CO.estado = e; CO.lineas.clear(); CO.apuntes.clear();
  const escribe = permitido('TESORERIA:ESCRITURA'), directo = permitido('TESORERIA_DIRECTO'), abierto = S.ejercicio.estado === 'ABIERTO';
  const caja = (n, x, cls) => `<div><span>${n}</span><strong class="${cls || ''}">${x == null ? '—' : fmt(x)}</strong></div>`;
  $('#coTotales').innerHTML = caja('Saldo banco', e.saldoBanco) + caja('Saldo contable (' + esc(e.cuentaContable) + ')', e.saldoContable)
    + caja('Pendiente en banco', e.lineasPendientes) + caja('Pendiente en contabilidad', e.apuntesPendientes)
    + caja('Diferencia', e.diferencia, e.diferencia ? 'estado-mal' : '');
  $('#coAvisos').innerHTML = e.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#coLineas').innerHTML = e.lineas.map(l => `<tr data-linea="${l.id}">
      <td>${escribe ? `<input type="checkbox" data-sel-linea="${l.id}" aria-label="Elegir">` : ''}</td><td>${fechaES(l.fecha)}</td>
      <td>${esc(l.concepto || '')}${l.conceptoComun && conceptoComun[l.conceptoComun] ? ` <small>(${conceptoComun[l.conceptoComun]})</small>` : ''}</td>
      <td class="num ${l.importe < 0 ? 'estado-mal' : ''}">${fmt(l.importe)}</td>
      <td class="fila-acciones">${directo ? `<button class="btn" data-asiento="${l.id}" ${abierto ? '' : 'disabled'}>Contabilizar</button>
        <button class="btn" data-liquidar="${l.id}" ${abierto ? '' : 'disabled'}>${l.importe > 0 ? 'Cobro' : 'Pago'}</button>` : ''}
        ${escribe ? `<button class="btn" data-ignorar="${l.id}" title="Línea duplicada o errónea">Ignorar</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="5" class="vacio">Nada pendiente.</td></tr>';
  $('#coApuntes').innerHTML = e.apuntes.map(a => `<tr data-apunte="${a.apunteId}">
      <td>${escribe ? `<input type="checkbox" data-sel-apunte="${a.apunteId}" aria-label="Elegir">` : ''}</td><td>${fechaES(a.fecha)}</td>
      <td>${esc(a.concepto || '')} <small>${esc(a.diario)} nº ${a.numero} · ${esc(a.ejercicio)}</small></td>
      <td class="num ${a.importe < 0 ? 'estado-mal' : ''}">${fmt(a.importe)}</td></tr>`).join('')
    || '<tr><td colspan="4" class="vacio">Nada pendiente.</td></tr>';
  $('#coRecientes').innerHTML = e.recientes.map(c => `<tr><td>${new Date(c.creadoEn).toLocaleString('es-ES')}</td>
      <td>${{ AUTOMATICA: 'Automática', MANUAL: 'Manual', ASIENTO: 'Contabilizada' }[c.tipo] || esc(c.tipo)}</td><td class="num">${fmt(c.importe)}</td>
      <td>${esc(c.lineas || '')}</td><td>${esc(c.apuntes || '')}</td>
      <td class="fila-acciones">${escribe ? `<button class="btn" data-deshacer="${c.id}">Deshacer</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="6" class="vacio">Ninguna.</td></tr>';
  $('#coExtractos').innerHTML = extractos.map(x => `<tr><td>${esc(x.fichero || x.formato)}</td><td>${fechaES(x.fechaDesde)} – ${fechaES(x.fechaHasta)}</td>
      <td class="num">${x.saldoInicial == null ? '' : fmt(x.saldoInicial)}</td><td class="num">${x.saldoFinal == null ? '' : fmt(x.saldoFinal)}</td>
      <td class="num">${x.lineas}</td><td class="num">${x.conciliadas}</td>
      <td class="fila-acciones">${escribe && !x.conciliadas ? `<button class="btn" data-borrar="${x.id}">Eliminar</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="7" class="vacio">Ninguno.</td></tr>';
  pintaSeleccion();
}
$('#coVer').onclick = () => verConciliacion().catch(e => msg($('#coMsg'), e.message, 'ayuda err-texto'));
$('#coBanco').onchange = $('#coVer').onclick;

function pintaSeleccion() {
  const e = CO.estado; if (!e) return;
  const sb = e.lineas.filter(l => CO.lineas.has(l.id)).reduce((a, l) => a + l.importe, 0);
  const sl = e.apuntes.filter(a => CO.apuntes.has(a.apunteId)).reduce((a, x) => a + x.importe, 0);
  const n = CO.lineas.size + CO.apuntes.size;
  $('#coSeleccion').textContent = n ? `Banco ${fmt(sb)} · contabilidad ${fmt(sl)}${Math.abs(sb - sl) < 0.005 ? ' · cuadra' : ' · diferencia ' + fmt(sb - sl)}` : '';
  $('#coConciliar').disabled = !CO.lineas.size || !CO.apuntes.size || Math.abs(sb - sl) >= 0.005;
  $$('#coLineas tr[data-linea]').forEach(tr => tr.classList.toggle('elegida', CO.lineas.has(+tr.dataset.linea)));
  $$('#coApuntes tr[data-apunte]').forEach(tr => tr.classList.toggle('elegida', CO.apuntes.has(+tr.dataset.apunte)));
}
$('#coApuntes').addEventListener('change', e => {
  const id = e.target.dataset.selApunte; if (!id) return;
  e.target.checked ? CO.apuntes.add(+id) : CO.apuntes.delete(+id); pintaSeleccion();
});
$('#coLineas').addEventListener('change', e => {
  const id = e.target.dataset.selLinea; if (!id) return;
  e.target.checked ? CO.lineas.add(+id) : CO.lineas.delete(+id); pintaSeleccion();
});

$('#coConciliar').onclick = async () => {
  try {
    await post(urlE('/tesoreria/conciliacion'), { cuentaBancariaId: +$('#coBanco').value, lineaIds: [...CO.lineas], apunteIds: [...CO.apuntes] });
    await verConciliacion(); msg($('#coMsg'), 'Conciliado.', 'ayuda ok-texto');
  } catch (e) { msg($('#coMsg'), e.message, 'ayuda err-texto'); }
};
$('#coAuto').onclick = async () => {
  try {
    const r = await post(urlE('/tesoreria/conciliacion/automatica'), { cuentaBancariaId: +$('#coBanco').value, diasMargen: 5 });
    await verConciliacion();
    msg($('#coMsg'), `${r.conciliadas} movimiento(s) conciliados automáticamente. Quedan ${r.lineasPendientes} en el banco y ${r.apuntesPendientes} en la contabilidad.`, 'ayuda ok-texto');
  } catch (e) { msg($('#coMsg'), e.message, 'ayuda err-texto'); }
};

// ---- Reglas de conciliación por concepto
const signoTxt = { CARGO: 'Cargos', ABONO: 'Abonos', AMBOS: 'Ambos' };
async function cargaReglasConciliacion() {
  const reglas = await api(urlE('/tesoreria/reglas-conciliacion')), escribe = permitido('TESORERIA:ESCRITURA');
  $('#rcLista').innerHTML = reglas.map(r => `<tr><td>${esc(r.texto)}</td><td>${signoTxt[r.signo]}</td><td>${esc(r.cuenta)} ${esc(r.cuentaNombre)}</td>
      <td>${esc(r.concepto || '')}</td><td class="num">${r.orden}</td>
      <td class="fila-acciones">${escribe ? `<button class="btn" data-borrar-regla="${r.id}">Quitar</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="6" class="vacio">Sin reglas.</td></tr>';
  if (!$('#rcCanal').options.length) $('#rcCanal').innerHTML = baseCanales().map(c => `<option value="${c.id}">Canal ${esc(c.codigo)}</option>`).join('');
}
$('#rcCrear').onclick = async () => {
  try {
    const cuenta = cuentaPorCodigo($('#rcCuenta').value); if (!cuenta) throw new Error('Elige una cuenta existente.');
    await post(urlE('/tesoreria/reglas-conciliacion'), { texto: $('#rcTexto').value, signo: $('#rcSigno').value, cuentaId: cuenta, canalId: +$('#rcCanal').value,
      concepto: $('#rcConcepto').value || null, orden: parseInt($('#rcOrden').value, 10) || 100 });
    ['#rcTexto', '#rcCuenta', '#rcConcepto'].forEach(i => $(i).value = '');
    await cargaReglasConciliacion(); msg($('#coMsg'), 'Regla añadida.', 'ayuda ok-texto');
  } catch (e) { msg($('#coMsg'), e.message, 'ayuda err-texto'); }
};
$('#rcLista').addEventListener('click', async e => {
  const b = e.target.closest('button[data-borrar-regla]'); if (!b) return;
  try { await api(urlE('/tesoreria/reglas-conciliacion/' + b.dataset.borrarRegla), { method: 'DELETE' }); await cargaReglasConciliacion(); }
  catch (err) { msg($('#coMsg'), err.message, 'ayuda err-texto'); }
});
$('#coReglas').onclick = async () => {
  try {
    const cuenta = +$('#coBanco').value;
    const s = await post(urlX('/tesoreria/conciliacion/reglas'), { cuentaBancariaId: cuenta, simular: true });
    if (!s.lineas.length) { msg($('#coMsg'), s.avisos.join(' ') || 'Ninguna línea coincide con las reglas.', 'ayuda'); return; }
    if (!confirm(`Se contabilizarán y conciliarán ${s.lineas.length} línea(s):\n` + s.lineas.map(l => `${fechaES(l.fecha)} ${l.concepto} ${fmt(l.importe)} → ${l.cuenta}`).join('\n'))) return;
    const a = await post(urlX('/tesoreria/conciliacion/reglas'), { cuentaBancariaId: cuenta });
    await verConciliacion(); msg($('#coMsg'), `${a.aplicadas} línea(s) contabilizadas y conciliadas con las reglas.`, 'ayuda ok-texto');
  } catch (e) { msg($('#coMsg'), e.message, 'ayuda err-texto'); }
};

// El fichero se envía en base64 dentro del JSON
$('#coImportar').onclick = async () => {
  const f = $('#coFichero').files[0];
  if (!f) return msg($('#coMsg'), 'Elige el fichero del extracto.', 'ayuda err-texto');
  try {
    const b64 = await new Promise((ok, mal) => { const r = new FileReader(); r.onload = () => ok(String(r.result).split(',')[1] || ''); r.onerror = mal; r.readAsDataURL(f); });
    const csv = /\.csv$/i.test(f.name);
    const r = await post(urlE('/tesoreria/extractos'), { nombre: f.name, contenido: b64, cuentaBancariaId: csv ? +$('#coBanco').value : null });
    $('#coFichero').value = '';
    await verConciliacion();
    msg($('#coMsg'), `Importado (${r.formato}): ` + r.extractos.map(x => `${x.cuenta}, ${x.nuevas} línea(s) nuevas${x.repetidas ? ', ' + x.repetidas + ' ya importadas' : ''}`).join('; ')
      + (r.avisos.length ? '. ' + r.avisos.join(' ') : '.'), 'ayuda ok-texto');
  } catch (e) { msg($('#coMsg'), e.message, 'ayuda err-texto'); }
};

$('#coLineas').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  const id = +(b.dataset.asiento || b.dataset.liquidar || b.dataset.ignorar);
  const l = CO.estado.lineas.find(x => x.id === id); if (!l) return;
  try {
    if (b.dataset.ignorar) { await post(urlE(`/tesoreria/extractos/lineas/${id}/ignorar`)); await verConciliacion(); msg($('#coMsg'), 'Línea ignorada.', 'ayuda'); return; }
    CO.accion = { tipo: b.dataset.asiento ? 'asiento' : 'liquidar', linea: l };
    $('#coAccionTitulo').textContent = `${fechaES(l.fecha)} · ${l.concepto || ''} · ${fmt(l.importe)}`;
    $('#coAsientoForm').hidden = CO.accion.tipo !== 'asiento'; $('#coLiquidarForm').hidden = CO.accion.tipo !== 'liquidar';
    if (CO.accion.tipo === 'asiento') { $('#coCuenta').value = ''; $('#coConcepto').value = l.concepto || ''; }
    else {
      const tipo = l.importe > 0 ? 'COBRO' : 'PAGO', imp = Math.abs(l.importe);
      const vs = (await api(urlE(`/tesoreria/vencimientos?tipo=${tipo}`))).filter(v => ['PENDIENTE', 'PARCIAL'].includes(v.estado) && v.importePendiente >= imp - 0.005)
        .sort((a, c) => Math.abs(a.importePendiente - imp) - Math.abs(c.importePendiente - imp));
      if (!vs.length) throw new Error(`No hay ${tipo === 'COBRO' ? 'cobros' : 'pagos'} pendientes de al menos ${fmt(imp)}.`);
      $('#coVenc').innerHTML = vs.map(v => `<option value="${v.id}">${esc(v.tercero)} · ${esc(v.factura || '')} · vence ${fechaES(v.fechaVencimiento)} · pendiente ${fmt(v.importePendiente)}</option>`).join('');
    }
    $('#coAccion').hidden = false; $('#coAccion').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  } catch (err) { msg($('#coMsg'), err.message, 'ayuda err-texto'); }
});
$('#coAccionNo').onclick = () => { CO.accion = null; $('#coAccion').hidden = true; };
$('#coAccionOk').onclick = async () => {
  const a = CO.accion; if (!a) return;
  try {
    let r;
    if (a.tipo === 'asiento') {
      const c = S.cuentas.find(x => x.codigo === $('#coCuenta').value.trim());
      if (!c) throw new Error('Elige la cuenta de contrapartida de la lista.');
      r = await post(urlX(`/tesoreria/extractos/lineas/${a.linea.id}/asiento`), { cuentaId: c.id, canalId: +$('#coCanal').value, concepto: $('#coConcepto').value || null });
    } else r = await post(urlX(`/tesoreria/extractos/lineas/${a.linea.id}/liquidar`), { vencimientoId: +$('#coVenc').value });
    CO.accion = null; $('#coAccion').hidden = true;
    await Promise.all([verConciliacion(), cargaVencimientos(), cargaPrevision()]);
    msg($('#coMsg'), `Contabilizado (asiento nº ${r.asientoNumero}) y conciliado.`, 'ayuda ok-texto');
  } catch (e) { msg($('#coMsg'), e.message, 'ayuda err-texto'); }
};

$('#coRecientes').addEventListener('click', async e => {
  const b = e.target.closest('button[data-deshacer]'); if (!b || !confirm('¿Deshacer esta conciliación? Los asientos no se tocan.')) return;
  try { await api(urlE('/tesoreria/conciliacion/' + b.dataset.deshacer), { method: 'DELETE' }); await verConciliacion(); msg($('#coMsg'), 'Conciliación deshecha.', 'ayuda'); }
  catch (err) { msg($('#coMsg'), err.message, 'ayuda err-texto'); }
});
$('#coExtractos').addEventListener('click', async e => {
  const b = e.target.closest('button[data-borrar]'); if (!b || !confirm('¿Eliminar este extracto y sus líneas?')) return;
  try { await api(urlE('/tesoreria/extractos/' + b.dataset.borrar), { method: 'DELETE' }); await verConciliacion(); msg($('#coMsg'), 'Extracto eliminado.', 'ayuda'); }
  catch (err) { msg($('#coMsg'), err.message, 'ayuda err-texto'); }
});

async function cargaPrevision() {
  const filas = await api(urlE('/tesoreria/prevision?meses=6'));
  $('#prevBody').innerHTML = filas.map(p => `<tr><td>${p.periodo === 'VENCIDO' ? '<strong>Vencido</strong>' : esc(p.periodo)}</td>
    <td class="num">${fmt(p.cobros)}</td><td class="num">${fmt(p.pagos)}</td>
    <td class="num ${p.neto < 0 ? 'estado-mal' : ''}">${fmt(p.neto)}</td></tr>`).join('');
}

async function cargaVencimientos() {
  const q = new URLSearchParams();
  if ($('#vTipo').value) q.set('tipo', $('#vTipo').value);
  if ($('#vEstado').value) q.set('estado', $('#vEstado').value);
  TES.venc = await api(urlE('/tesoreria/vencimientos?' + q));
  const directo = permitido('TESORERIA_DIRECTO');
  $('#vBody').innerHTML = TES.venc.length ? TES.venc.map(v => {
    const abierto = v.estado === 'PENDIENTE' || v.estado === 'PARCIAL';
    return `<tr><td class="${v.vencido ? 'estado-mal' : ''}">${fechaES(v.fechaVencimiento)}</td><td>${v.tipo === 'COBRO' ? 'Cobro' : 'Pago'}</td>
      <td>${esc(v.tercero)}</td><td>${esc(v.factura || '—')}</td><td>${v.numPlazo}/${v.totalPlazos}</td>
      <td class="num">${fmt(v.importe)}</td><td class="num">${fmt(v.importePendiente)}</td><td>${estadoTxt[v.estado] || esc(v.estado)}</td>
      <td class="fila-acciones">${abierto && directo ? `<button class="btn primario" data-liq="${v.id}" ${S.ejercicio.estado !== 'ABIERTO' ? 'disabled' : ''}>${v.tipo === 'COBRO' ? 'Cobrar' : 'Pagar'}</button>` : ''}
        ${v.estado === 'PENDIENTE' && v.importePendiente === v.importe && directo ? `<button class="btn" data-anul="${v.id}">Anular</button>` : ''}
        ${v.importePendiente < v.importe && v.estado !== 'ANULADO' ? `<button class="btn" data-cobros="${v.id}">${v.tipo === 'COBRO' ? 'Cobros' : 'Pagos'}</button>` : ''}</td></tr>`;
  }).join('') : '<tr><td colspan="9" class="vacio">No hay vencimientos con ese filtro.</td></tr>';
}
// Cobros o pagos de un vencimiento, con la opción de deshacerlos (extorno)
async function verCobros(v) {
  if (!v) return;
  TES.cobrosDe = v;
  const filas = await api(urlE(`/tesoreria/vencimientos/${v.id}/cobros`)), directo = permitido('TESORERIA_DIRECTO');
  $('#vCobrosTitulo').textContent = `${v.tipo === 'COBRO' ? 'Cobros' : 'Pagos'} de ${v.tercero}${v.factura ? ' · factura ' + v.factura : ''}`;
  $('#vCobrosBody').innerHTML = filas.map(c => `<tr><td>${fechaES(c.fecha)}</td><td class="num">${fmt(c.importe)}</td>
      <td>${c.asientoNumero ? 'nº ' + c.asientoNumero + ' (' + esc(c.ejercicio || '') + ')' : ''}</td>
      <td>${c.enRemesa ? 'en remesa' : c.conciliado ? 'conciliado' : ''}</td>
      <td class="fila-acciones">${directo && !c.enRemesa && !c.conciliado ? `<button class="btn" data-extornar="${c.id}">Deshacer</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="5" class="vacio">Sin cobros ni pagos registrados.</td></tr>';
  $('#vCobros').hidden = false;
}
$('#vCobrosBody').addEventListener('click', async e => {
  const b = e.target.closest('button[data-extornar]'); if (!b || !confirm('¿Deshacer este cobro o pago? Se anula su asiento y el importe vuelve a quedar pendiente.')) return;
  try {
    await post(urlX(`/tesoreria/cobros/${b.dataset.extornar}/extornar`));
    msg($('#vMsg'), 'Cobro o pago deshecho: el importe vuelve a estar pendiente.', 'ayuda ok-texto');
    await cargaVencimientos(); await verCobros(TES.venc.find(x => x.id === TES.cobrosDe.id) || TES.cobrosDe);
  } catch (err) { msg($('#vMsg'), err.message, 'ayuda err-texto'); }
});

$('#vTipo').onchange = $('#vEstado').onchange = () => cargaVencimientos().catch(e => msg($('#vMsg'), e.message, 'ayuda err-texto'));

$('#vBody').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  if (b.dataset.cobros) { verCobros(TES.venc.find(x => String(x.id) === b.dataset.cobros)).catch(err => msg($('#vMsg'), err.message, 'ayuda err-texto')); return; }
  if (b.dataset.anul) {
    if (!confirm('¿Anular este vencimiento? No se podrá cobrar ni pagar.')) return;
    try { await post(urlE(`/tesoreria/vencimientos/${b.dataset.anul}/anular`)); await cargaTesoreria(); }
    catch (err) { msg($('#vMsg'), err.message, 'ayuda err-texto'); }
    return;
  }
  const v = TES.venc.find(x => String(x.id) === b.dataset.liq); if (!v) return;
  if (!TES.bancos.some(x => x.activa && x.cuentaContableId)) {
    msg($('#vMsg'), 'Primero da de alta una cuenta bancaria con su cuenta contable (572…).', 'ayuda err-texto'); return;
  }
  TES.liq = v;
  $('#liqTitulo').textContent = `${v.tipo === 'COBRO' ? 'Registrar cobro' : 'Registrar pago'}: ${v.tercero}${v.factura ? ' · factura ' + v.factura : ''}`;
  const e0 = S.ejercicio, hoyISO = hoy();
  $('#liqFecha').min = e0.fechaInicio; $('#liqFecha').max = e0.fechaFin;
  $('#liqFecha').value = hoyISO < e0.fechaInicio ? e0.fechaInicio : hoyISO > e0.fechaFin ? e0.fechaFin : hoyISO;
  $('#liqImporte').value = String(v.importePendiente).replace('.', ',');
  $('#liqBanco').innerHTML = TES.bancos.filter(x => x.activa && x.cuentaContableId).map(x => `<option value="${x.id}">${esc(x.alias)} · ${esc(x.iban)}</option>`).join('');
  msg($('#liqMsg'), '', 'ayuda');
  $('#liqPanel').hidden = false; $('#liqPanel').scrollIntoView({ behavior: 'smooth' });
});

$('#liqCancelar').onclick = () => { TES.liq = null; $('#liqPanel').hidden = true; };
$('#liqGuardar').onclick = async () => {
  if (!TES.liq) return;
  try {
    const r = await post(urlX(`/tesoreria/vencimientos/${TES.liq.id}/liquidar`),
      { fecha: $('#liqFecha').value, importe: num($('#liqImporte').value), cuentaBancariaId: +$('#liqBanco').value });
    msg($('#vMsg'), `Contabilizado en el asiento ${r.asientoNumero}.` + (r.pendiente > 0 ? ` Quedan ${fmt(r.pendiente)} pendientes.` : ''), 'ayuda ok-texto');
    TES.liq = null; $('#liqPanel').hidden = true;
  IN.ficha = null; $('#inFicha').hidden = true; $('#inNuevo').hidden = true;
  RE.terceros = []; RE.detalle = null; $('#reDetalle').hidden = true; $('#reCandidatos').hidden = true;
    await cargaTesoreria();
  } catch (err) { msg($('#liqMsg'), err.message, 'ayuda err-texto'); }
};

async function cargaBancos() {
  TES.bancos = await api(urlE('/tesoreria/cuentas-bancarias'));
  $('#bBody').innerHTML = TES.bancos.length ? TES.bancos.map(b => `<tr><td>${esc(b.alias)}${b.activa ? '' : ' (inactiva)'}</td><td>${esc(b.iban)}</td>
    <td>${esc(b.banco || '')}</td><td>${esc(b.cuentaContable || '—')}</td><td class="num">${fmt(b.saldo)}</td></tr>`).join('')
    : '<tr><td colspan="5" class="vacio">Todavía no hay cuentas bancarias.</td></tr>';
}

$('#bCrear').onclick = async () => {
  const c = S.cuentas.find(x => x.codigo === $('#bCuenta').value.trim());
  if (!c) return msg($('#bMsg'), 'Elige una cuenta contable existente de la lista (se necesita permiso de lectura en Contabilidad o Facturación para verla).', 'ayuda err-texto');
  try {
    await post(urlE('/tesoreria/cuentas-bancarias'), { alias: $('#bAlias').value, iban: $('#bIban').value, cuentaContableId: c.id });
    msg($('#bMsg'), 'Cuenta bancaria creada.', 'ayuda ok-texto');
    ['#bAlias', '#bIban', '#bCuenta'].forEach(i => $(i).value = '');
    await cargaBancos();
  } catch (e) { msg($('#bMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ modelos fiscales
const MF = { modelos: [], actual: null, presentando: null, liq: null };
const estadoDecl = { CALCULADA: 'calculada', PRESENTADA: 'presentada', SIN_GUARDAR: 'sin guardar', BORRADOR: 'borrador' };
const periodoTxt = p => ({ '0A': 'Anual', '1T': '1.er trimestre', '2T': '2.º trimestre', '3T': '3.er trimestre', '4T': '4.º trimestre',
  '1P': '1.er pago (abril)', '2P': '2.º pago (octubre)', '3P': '3.er pago (diciembre)' }[p] || 'Mes ' + p);

async function cargaFiscal() {
  if (!$('#mfAnio').value) $('#mfAnio').value = new Date().getFullYear();
  if (!MF.modelos.length) {
    MF.modelos = await api(urlE('/fiscal/modelos'));
    $('#mfModelo').innerHTML = MF.modelos.length
      ? MF.modelos.map(m => `<option value="${esc(m.codigo)}">${esc(m.codigo)} · ${esc(m.nombre)}</option>`).join('')
      : '<option value="">Ningún modelo aplicable a esta empresa</option>';
    pintaPeriodos();
  }
  await Promise.all([cargaDeclaraciones(), listaProrratas(), cargaCriterioCaja(), cargaInmuebles()]);
  cargaSii();
  if (!$('#lrAnio').value) $('#lrAnio').value = $('#mfAnio').value;
  if (!$('#lrPeriodo').options.length)
    $('#lrPeriodo').innerHTML = ['1T', '2T', '3T', '4T', '0A', '01', '02', '03', '04', '05', '06', '07', '08', '09', '10', '11', '12']
      .map(p => `<option value="${p}">${esc(periodoTxt(p))}</option>`).join('');
}

// ------------------------------------------------------------ Impuesto sobre Sociedades
let IS = null;
async function verSociedades() {
  IS = await api(urlE('/fiscal/sociedades/' + S.ejercicio.id));
  $('#isTitulo').textContent = `Impuesto sobre Sociedades · periodo impositivo ${S.ejercicio.codigo} (${fechaES(S.ejercicio.fechaInicio)} – ${fechaES(S.ejercicio.fechaFin)})`;
  for (const k of ['reservaCapitalizacion', 'bonificaciones', 'deducciones', 'incn', 'binsAnteriores'])
    $('#is_' + k).value = IS[k] ? String(IS[k]).replace('.', ',') : '';
  pintaAjustesIs(); pintaBinsIs();
}
function pintaBinsIs() {
  IS.binsDetalle = IS.binsDetalle || [];
  $('#isBins').innerHTML = IS.binsDetalle.map((b, i) => `<tr><td><input data-b="${i}" data-k="anio" inputmode="numeric" value="${b.anio || ''}" aria-label="Año"></td>
    <td><input class="num" data-b="${i}" data-k="importe" inputmode="decimal" value="${String(b.importe || '').replace('.', ',')}" aria-label="Importe"></td>
    <td><button class="x" type="button" data-delb="${i}" aria-label="Quitar">×</button></td></tr>`).join('')
    || '<tr><td colspan="3" class="vacio">Sin detalle por año.</td></tr>';
  const tot = IS.binsDetalle.reduce((a, b) => a + (+b.importe || 0), 0);
  $('#is_binsAnteriores').readOnly = IS.binsDetalle.length > 0;
  if (IS.binsDetalle.length) $('#is_binsAnteriores').value = String(tot).replace('.', ',');
}
$('#isBins').addEventListener('change', e => {
  const t = e.target; if (t.dataset.b === undefined) return;
  IS.binsDetalle[+t.dataset.b][t.dataset.k] = t.dataset.k === 'anio' ? +t.value : num(t.value); pintaBinsIs();
});
$('#isBins').addEventListener('click', e => { if (e.target.dataset.delb !== undefined) { IS.binsDetalle.splice(+e.target.dataset.delb, 1); pintaBinsIs(); } });
$('#isBinAnadir').onclick = () => { IS.binsDetalle = IS.binsDetalle || []; IS.binsDetalle.push({ anio: new Date(S.ejercicio.fechaInicio).getFullYear() - 1, importe: 0 }); pintaBinsIs(); };
function pintaAjustesIs() {
  const clases = IS.clases || [];
  const opcClase = c => clases.map(x => `<option value="${x.codigo}" ${x.codigo === (c || 'OTRAS') ? 'selected' : ''}>${esc(x.texto)}</option>`).join('');
  $('#isAjustes').innerHTML = IS.ajustes.map((a, i) => `<tr><td><input data-i="${i}" data-k="concepto" value="${esc(a.concepto)}" aria-label="Concepto"></td>
    <td><select data-i="${i}" data-k="clase" aria-label="Casilla del modelo 200">${opcClase(a.clase)}</select></td>
    <td><input class="num" data-i="${i}" data-k="importe" inputmode="decimal" value="${String(a.importe).replace('.', ',')}" aria-label="Importe"></td>
    <td><button class="x" type="button" data-del="${i}" aria-label="Quitar">×</button></td></tr>`).join('')
    || '<tr><td colspan="4" class="vacio">Sin ajustes extracontables.</td></tr>';
  $('#isSugerencias').innerHTML = IS.sugerencias.map((a, i) => `<li>${esc(a.concepto)}: ${fmt(a.importe)}
    <button class="btn" type="button" data-sug="${i}">Añadir</button></li>`).join('');
}
$('#isAjustes').addEventListener('change', e => {
  const t = e.target; if (t.dataset.i === undefined) return;
  IS.ajustes[+t.dataset.i][t.dataset.k] = t.dataset.k === 'importe' ? num(t.value) : t.value;
});
$('#isAjustes').addEventListener('click', e => { if (e.target.dataset.del !== undefined) { IS.ajustes.splice(+e.target.dataset.del, 1); pintaAjustesIs(); } });
$('#isSugerencias').addEventListener('click', e => {
  const i = e.target.dataset.sug; if (i === undefined) return;
  IS.ajustes.push({ ...IS.sugerencias[+i] }); pintaAjustesIs();
});
$('#isAnadir').onclick = () => { IS.ajustes.push({ concepto: '', importe: 0, clase: 'OTRAS' }); pintaAjustesIs(); };
$('#isGuardar').onclick = async () => {
  try {
    const cuerpo = { ...IS };
    for (const k of ['reservaCapitalizacion', 'bonificaciones', 'deducciones', 'incn', 'binsAnteriores']) cuerpo[k] = num($('#is_' + k).value);
    IS = await put(urlE('/fiscal/sociedades/' + S.ejercicio.id), cuerpo); pintaAjustesIs(); pintaBinsIs();
    msg($('#isMsg'), 'Datos del Impuesto sobre Sociedades guardados. Calcula el 200 en la lista de modelos.', 'ayuda ok-texto');
  } catch (e) { msg($('#isMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ módulos
let MO = [];
const campoMo = (i, k, v, tipo) => tipo === 'bool'
  ? `<td><input type="checkbox" data-i="${i}" data-k="${k}" ${v ? 'checked' : ''} aria-label="${k}"></td>`
  : `<td><input data-i="${i}" data-k="${k}" ${tipo === 'n' ? 'class="num" inputmode="decimal"' : ''} value="${tipo === 'n' ? (v ? String(v).replace('.', ',') : '') : esc(v || '')}" aria-label="${k}"></td>`;
function pintaModulos() {
  $('#moLista').innerHTML = MO.map((m, i) => `<tr>${campoMo(i, 'descripcion', m.descripcion)}${campoMo(i, 'rendimientoNetoIrpf', m.rendimientoNetoIrpf, 'n')}
    ${campoMo(i, 'personasAsalariadas', m.personasAsalariadas, 'n')}${campoMo(i, 'agricola', m.agricola, 'bool')}
    ${campoMo(i, 'cuotaIvaDevengada', m.cuotaIvaDevengada, 'n')}${campoMo(i, 'cuotaIvaSoportada', m.cuotaIvaSoportada, 'n')}
    ${campoMo(i, 'porcentajeIngresoIva', m.porcentajeIngresoIva, 'n')}${campoMo(i, 'porcentajeCuotaMinima', m.porcentajeCuotaMinima, 'n')}
    <td><button class="x" type="button" data-del="${i}" aria-label="Quitar">×</button></td></tr>`).join('')
    || '<tr><td colspan="9" class="vacio">Sin actividades para este año.</td></tr>';
}
async function verModulos() { MO = await api(urlE('/fiscal/modulos/' + (+$('#moAnio').value))); pintaModulos(); }
$('#moVer').onclick = () => verModulos().catch(e => msg($('#moMsg'), e.message, 'ayuda err-texto'));
$('#moAnadir').onclick = () => { MO.push({ descripcion: '', agricola: false }); pintaModulos(); };
$('#moLista').addEventListener('change', e => {
  const t = e.target; if (t.dataset.i === undefined) return;
  MO[+t.dataset.i][t.dataset.k] = t.type === 'checkbox' ? t.checked : t.dataset.k === 'descripcion' ? t.value : num(t.value);
});
$('#moLista').addEventListener('click', e => { if (e.target.dataset.del !== undefined) { MO.splice(+e.target.dataset.del, 1); pintaModulos(); } });
$('#moGuardar').onclick = async () => {
  try { MO = await put(urlE('/fiscal/modulos/' + (+$('#moAnio').value)), MO); pintaModulos(); msg($('#moMsg'), 'Módulos guardados.', 'ayuda ok-texto'); }
  catch (e) { msg($('#moMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ SII
const SII = { facturas: [] };
const estadoSii = { PENDIENTE: 'Pendiente', ACEPTADA: 'Aceptada', ACEPTADA_CON_ERRORES: 'Aceptada con errores', RECHAZADA: 'Rechazada' };
function cargaSii() {
  const igic = S.empresa && (S.empresa.impuestoIndirecto === 'IGIC');
  $('#siiPanel').hidden = !!igic;
  if (!$('#siiAnio').value) $('#siiAnio').value = $('#mfAnio').value;
  if (!$('#siiMes').options.length)
    $('#siiMes').innerHTML = '<option value="">Todo el año</option>' + Array.from({ length: 12 }, (_, i) =>
      `<option value="${i + 1}" ${i === new Date().getMonth() ? 'selected' : ''}>${new Date(2000, i, 1).toLocaleDateString('es-ES', { month: 'long' })}</option>`).join('');
}
const siiCmd = (ids) => ({ libro: $('#siiLibro').value, anio: +$('#siiAnio').value, mes: $('#siiMes').value ? +$('#siiMes').value : null,
  facturaIds: ids && ids.length ? ids : null });
const siiElegidas = () => $$('#siiLista input[data-f]:checked').map(c => +c.dataset.f);
async function verSii() {
  try {
    const q = new URLSearchParams({ libro: $('#siiLibro').value, anio: $('#siiAnio').value });
    if ($('#siiMes').value) q.set('mes', $('#siiMes').value);
    const r = await api(urlE('/fiscal/sii/facturas?' + q));
    SII.facturas = r.facturas;
    $('#siiEnviar').title = r.envioConfigurado ? '' : 'El servidor no tiene certificado para el SII: descarga el XML';
    $('#siiEnviar').disabled = !r.envioConfigurado;
    $('#siiLista').innerHTML = r.facturas.map(f => `<tr><td><input type="checkbox" data-f="${f.id}" aria-label="Elegir ${esc(f.numSerie)}"></td>
      <td>${esc(f.numSerie)}</td><td>${fechaES(f.fechaExpedicion)}</td><td>${esc(f.nombre)} <small>${esc(f.nif)}</small></td>
      <td class="num">${fmt(f.total)}</td><td class="${f.estado === 'RECHAZADA' ? 'estado-mal' : f.estado === 'ACEPTADA' ? 'estado-ok' : ''}">${esc(estadoSii[f.estado] || f.estado)}</td>
      <td><small>${esc(f.error || f.csv || '')}</small></td></tr>`).join('') || '<tr><td colspan="7" class="vacio">No hay facturas contabilizadas en el periodo.</td></tr>';
    const pend = r.facturas.filter(f => ['PENDIENTE', 'RECHAZADA'].includes(f.estado)).length;
    msg($('#siiMsg'), `${r.facturas.length} factura(s); ${pend} pendiente(s) de enviar. Sin marcar ninguna se envían las pendientes y rechazadas del periodo.`, 'ayuda');
  } catch (e) { msg($('#siiMsg'), e.message, 'ayuda err-texto'); }
}
$('#siiVer').onclick = verSii;
$('#siiTodas').onchange = e => $$('#siiLista input[data-f]').forEach(c => c.checked = e.target.checked);
$('#siiXml').onclick = async () => {
  try { const n = await descargaPost(urlE('/fiscal/sii/xml'), siiCmd(siiElegidas()), 'sii.xml'); msg($('#siiMsg'), `Descargado ${n}.`, 'ayuda ok-texto'); }
  catch (e) { msg($('#siiMsg'), e.message, 'ayuda err-texto'); }
};
const pintaEnvioSii = envios => {
  $('#siiResultado').innerHTML = envios.map(e => `<li>${esc(e.tipoComunicacion)} ${esc(e.entorno.toLowerCase())}: ${esc(e.estadoEnvio || '')}
    ${e.csv ? ' · CSV ' + esc(e.csv) : ''}${e.fallo ? ' · ' + esc(e.fallo) : ''} · ${e.lineas.filter(l => l.estado === 'Correcto').length} correctas,
    ${e.lineas.filter(l => l.estado === 'AceptadoConErrores').length} con errores, ${e.lineas.filter(l => l.estado === 'Incorrecto').length} rechazadas</li>`).join('');
};
$('#siiEnviar').onclick = async () => {
  if (!confirm('¿Enviar las facturas al SII de la AEAT?')) return;
  try { pintaEnvioSii(await post(urlE('/fiscal/sii/enviar'), siiCmd(siiElegidas()))); await verSii(); }
  catch (e) { msg($('#siiMsg'), e.message, 'ayuda err-texto'); }
};
$('#siiRegistrar').onclick = async () => {
  try {
    pintaEnvioSii([await post(urlE('/fiscal/sii/respuesta'), { libro: $('#siiLibro').value, xml: $('#siiRespuesta').value })]);
    $('#siiRespuesta').value = ''; await verSii();
  } catch (e) { msg($('#siiMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ régimen de IVA y criterio de caja
async function cargaCriterioCaja() {
  const c = await api(urlE('/fiscal/configuracion'));
  $('#ccRegimen').value = c.regimenIva;
  $('#ccIrpf').value = c.regimenIrpf || '';
  $('#ccSii').value = String(!!c.sii);
  $('#ccIs').value = String(!!c.tributaIs); $('#ccIsTipo').value = c.isTipo || 'GENERAL';
  $('#ccIsPct').value = c.isPorcentaje == null ? '' : String(c.isPorcentaje).replace('.', ','); $('#ccIs202').value = c.isModalidad202 || 'ART_40_2';
  $('#isPanel').hidden = !c.tributaIs;
  if (c.tributaIs) await verSociedades();
  [...$('#ccRegimen').options].forEach(o => o.hidden = c.impuestoIndirecto === 'IGIC' && ['SIMPLIFICADO', 'RECARGO_EQUIVALENCIA'].includes(o.value));
  if (!$('#moAnio').value) $('#moAnio').value = $('#mfAnio').value;
  $('#moPanel').hidden = !(c.regimenIva === 'SIMPLIFICADO' || c.regimenIrpf === 'OBJETIVA');
  if (!$('#moPanel').hidden) await verModulos();
  if (!$('#ccAnio').value) $('#ccAnio').value = $('#mfAnio').value;
  if (!$('#ccPeriodo').options.length)
    $('#ccPeriodo').innerHTML = ['1T', '2T', '3T', '4T', '01', '02', '03', '04', '05', '06', '07', '08', '09', '10', '11', '12']
      .map(p => `<option value="${p}">${esc(periodoTxt(p))}</option>`).join('');
}
async function verCriterioCaja() {
  const d = await api(urlE(`/fiscal/criterio-caja?anio=${+$('#ccAnio').value}&periodo=${$('#ccPeriodo').value}`));
  $('#ccAvisos').innerHTML = d.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#ccLista').innerHTML = (d.lineas.map(l => `<tr><td>${l.libro === 'EMITIDAS' ? 'Emitida' : 'Recibida'}</td><td>${esc(l.factura)}</td><td>${esc(l.tercero)}</td>
      <td class="num">${fmt(l.total)}</td><td class="num">${fmt(l.base)}</td><td class="num">${fmt(l.cuota)}</td>
      <td>${l.traspasado ? 'asiento nº ' + l.asientoNumero : 'pendiente'}</td></tr>`).join('')
    + (d.lineas.length ? `<tr class="total"><td colspan="5">Repercutido ${fmt(d.repercutido)} · deducible ${fmt(d.deducible)}</td><td class="num">${fmt(d.pendiente)}</td><td>sin traspasar</td></tr>` : ''))
    || '<tr><td colspan="7" class="vacio">Nada devengado por criterio de caja en este periodo.</td></tr>';
  const asientos = new Map(d.lineas.filter(l => l.traspasado).map(l => [l.asientoId, l.asientoNumero]));
  $('#ccAsientos').innerHTML = permitido('FISCAL_DIRECTO') && S.ejercicio.estado === 'ABIERTO'
    ? [...asientos].map(([id, n]) => `<button class="btn" data-anular-cc="${id}">Anular el traspaso nº ${n}</button>`).join(' ') : '';
}
$('#ccVer').onclick = () => verCriterioCaja().catch(e => msg($('#ccMsg'), e.message, 'ayuda err-texto'));
$('#ccGuardar').onclick = async () => {
  try {
    const c = await api(urlE('/fiscal/configuracion'), { method: 'PUT', body: JSON.stringify({ regimenIva: $('#ccRegimen').value,
      regimenIrpf: $('#ccIrpf').value, sii: $('#ccSii').value === 'true', tributaIs: $('#ccIs').value === 'true', isTipo: $('#ccIsTipo').value,
      isPorcentaje: $('#ccIsPct').value ? num($('#ccIsPct').value) : null, isModalidad202: $('#ccIs202').value }) });
    msg($('#ccMsg'), {
      CRITERIO_CAJA: 'Empresa en criterio de caja: las facturas que se contabilicen desde ahora lo aplican.',
      SIMPLIFICADO: 'Empresa en el régimen simplificado: indica los módulos del año para calcular el 303 y el 131.',
      RECARGO_EQUIVALENCIA: 'Empresa en recargo de equivalencia: las compras llevan recargo y su IVA va al coste; el IVA de las ventas va a ingresos. No presenta 303.',
    }[c.regimenIva] || 'Empresa en régimen general. Las facturas ya contabilizadas en criterio de caja lo siguen aplicando.', 'ayuda ok-texto');
    MF.modelos = []; await cargaFiscal();
  } catch (e) { msg($('#ccMsg'), e.message, 'ayuda err-texto'); }
};
$('#ccTraspasar').onclick = async () => {
  try {
    const t = await post(urlX('/fiscal/criterio-caja/traspasar'), { anio: +$('#ccAnio').value, periodo: $('#ccPeriodo').value });
    await verCriterioCaja();
    msg($('#ccMsg'), `Traspaso contabilizado (asiento nº ${t.asientoNumero}): repercutido ${fmt(t.repercutido)}, deducible ${fmt(t.deducible)}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#ccMsg'), e.message, 'ayuda err-texto'); }
};
$('#ccAsientos').addEventListener('click', async e => {
  const b = e.target.closest('button[data-anular-cc]'); if (!b || !confirm('¿Anular el traspaso?')) return;
  try { await api(urlX('/fiscal/criterio-caja/' + b.dataset.anularCc), { method: 'DELETE' }); await verCriterioCaja(); msg($('#ccMsg'), 'Traspaso anulado.', 'ayuda ok-texto'); }
  catch (err) { msg($('#ccMsg'), err.message, 'ayuda err-texto'); }
});

// ------------------------------------------------------------ prorrata
async function listaProrratas() {
  const filas = await api(urlE('/fiscal/prorratas'));
  const pc = v => v == null ? '' : v + ' %';
  $('#prLista').innerHTML = filas.map(p => `<tr><td>${p.anio}</td><td>${p.sectorDiferenciado ?? ''}</td><td class="num">${pc(p.porcentajeProvisional)}</td>
      <td class="num">${pc(p.porcentajeDefinitivo)}</td><td class="num">${p.operacionesConDerecho != null ? fmt(p.operacionesConDerecho) : ''}</td>
      <td class="num">${p.operacionesTotales != null ? fmt(p.operacionesTotales) : ''}</td></tr>`).join('')
    || '<tr><td colspan="6" class="vacio">Sin prorrata: se deduce todo el impuesto soportado.</td></tr>';
  if (!$('#prAnio').value) $('#prAnio').value = $('#mfAnio').value;
  if (!$('#prRegAnio').value) $('#prRegAnio').value = $('#mfAnio').value;
}
let PR_ASIENTO = null;
async function verRegProrrata() {
  const r = await api(urlE(`/fiscal/prorratas/${+$('#prRegAnio').value}/regularizacion`));
  PR_ASIENTO = r.asientoId;
  $('#prRegAvisos').innerHTML = r.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#prRegLista').innerHTML = (r.lineas.map(l => `<tr><td>${esc(l.factura)}${l.bienInversion ? ' (bien de inversión)' : ''}</td><td>${esc(l.tercero)}</td>
      <td>${fechaES(l.fechaContable)}</td><td class="num">${fmt(l.cuota)}</td><td class="num">${fmt(l.porcentajeAplicado)} %</td>
      <td class="num">${l.porcentajeDefinitivo} %</td><td class="num">${fmt(l.importe)}</td></tr>`).join('')
    + (r.lineas.length ? `<tr class="total"><td colspan="6">Total${r.asientoNumero ? ' · contabilizado en el asiento nº ' + r.asientoNumero : ' · sin contabilizar'}</td>
      <td class="num">${fmt(r.importe)}</td></tr>` : '')) || '<tr><td colspan="7" class="vacio">Sin líneas con prorrata en el año.</td></tr>';
  $('#prRegAnular').hidden = !r.asientoId || !permitido('FISCAL_DIRECTO');
}
$('#prRegVer').onclick = () => verRegProrrata().catch(e => msg($('#prRegMsg'), e.message, 'ayuda err-texto'));
$('#prRegContabilizar').onclick = async () => {
  try {
    const r = await post(urlX('/fiscal/prorratas/regularizar'), { anio: +$('#prRegAnio').value });
    await verRegProrrata(); msg($('#prRegMsg'), `Regularización contabilizada (asiento nº ${r.asientoNumero}) por ${fmt(r.importe)}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#prRegMsg'), e.message, 'ayuda err-texto'); }
};
$('#prRegAnular').onclick = async () => {
  if (!PR_ASIENTO || !confirm('¿Anular el asiento de regularización por prorrata?')) return;
  try { await api(urlX('/fiscal/prorratas/regularizacion/' + PR_ASIENTO), { method: 'DELETE' }); await verRegProrrata(); msg($('#prRegMsg'), 'Regularización anulada.', 'ayuda ok-texto'); }
  catch (e) { msg($('#prRegMsg'), e.message, 'ayuda err-texto'); }
};
$('#prGuardar').onclick = async () => {
  try {
    const ent = id => $(id).value.trim() ? parseInt($(id).value, 10) : null, dec = id => $(id).value.trim() ? num($(id).value) : null;
    const p = await api(urlE('/fiscal/prorratas'), { method: 'PUT', body: JSON.stringify({ anio: +$('#prAnio').value, sectorDiferenciado: ent('#prSector'),
      porcentajeProvisional: ent('#prProv'), porcentajeDefinitivo: ent('#prDef'), operacionesConDerecho: dec('#prCon'), operacionesTotales: dec('#prTot') }) });
    ['#prProv', '#prDef', '#prCon', '#prTot', '#prSector'].forEach(i => $(i).value = '');
    await listaProrratas();
    msg($('#prMsg'), `Prorrata de ${p.anio} guardada${p.porcentajeDefinitivo != null ? ': definitivo ' + p.porcentajeDefinitivo + ' %' : ''}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#prMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ inmuebles arrendados (180)
const IM = { lista: [], sel: null };
const sitTxt = { 1: 'España salvo País Vasco y Navarra', 2: 'País Vasco', 3: 'Navarra', 4: 'sin referencia catastral' };
async function cargaInmuebles() {
  const [lista, terceros] = await Promise.all([api(urlE('/inmuebles')), api(urlE('/terceros'))]);
  IM.lista = lista;
  $('#imTercero').innerHTML = terceros.filter(t => t.esProveedor).map(t => `<option value="${t.id}">${esc(t.nombre)}</option>`).join('');
  $('#imLista').innerHTML = lista.map((i, n) => `<tr><td>${esc(i.arrendador)}</td><td>${i.situacion} · ${sitTxt[i.situacion]}</td><td>${esc(i.referenciaCatastral || '')}</td>
      <td>${esc([i.tipoVia, i.nombreVia, i.numero, i.complemento].filter(Boolean).join(' '))}</td><td>${esc(i.municipio || '')} ${esc(i.codigoPostal || '')}</td>
      <td class="fila-acciones" data-req="FISCAL:ESCRITURA"><button class="btn" data-imed="${n}">Modificar</button><button class="btn" data-imdel="${i.id}">Borrar</button></td></tr>`).join('')
    || '<tr><td colspan="6" class="vacio">No hay inmuebles arrendados.</td></tr>';
  aplicaPermisos();
}
const IM_CAMPOS = { imTercero: 'terceroId', imSit: 'situacion', imRc: 'referenciaCatastral', imTv: 'tipoVia', imVia: 'nombreVia', imNum: 'numero',
                    imComp: 'complemento', imMun: 'municipio', imMuni: 'codigoMunicipio', imCp: 'codigoPostal' };
function formInmueble(i) {
  IM.sel = i;
  Object.entries(IM_CAMPOS).forEach(([id, k]) => { $('#' + id).value = i ? (i[k] ?? '') : (id === 'imSit' ? '1' : id === 'imTercero' ? $('#imTercero').value : ''); });
}
$('#imNuevo').onclick = () => formInmueble(null);
$('#imLista').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  if (b.dataset.imed !== undefined) { formInmueble(IM.lista[+b.dataset.imed]); return; }
  if (!confirm('¿Borrar el inmueble?')) return;
  try { await api(urlE(`/inmuebles/${b.dataset.imdel}`), { method: 'DELETE' }); await cargaInmuebles(); msg($('#imMsg'), 'Inmueble borrado.', 'ok-texto'); }
  catch (err) { msg($('#imMsg'), err.message, 'err-texto'); }
});
$('#imGuardar').onclick = async () => {
  const o = { id: IM.sel?.id || 0 };
  Object.entries(IM_CAMPOS).forEach(([id, k]) => { const v = $('#' + id).value.trim(); o[k] = v === '' ? null : v; });
  o.terceroId = +o.terceroId; o.situacion = +o.situacion; o.nombreVia = o.nombreVia || '';
  try { await put(urlE('/inmuebles'), o); formInmueble(null); await cargaInmuebles(); msg($('#imMsg'), 'Inmueble guardado.', 'ok-texto'); }
  catch (err) { msg($('#imMsg'), err.message, 'err-texto'); }
};
// En la factura recibida: el inmueble del arrendador, si tiene alguno
async function pintaInmueblesFactura() {
  const t = +$('#fTercero').value;
  const lista = !esEmitida() && t ? await api(urlE(`/inmuebles?terceroId=${t}`)).catch(() => []) : [];
  $('#fInmL').hidden = lista.length === 0;
  $('#fInm').innerHTML = lista.map(i => `<option value="${i.id}">${esc([i.tipoVia, i.nombreVia, i.numero].filter(Boolean).join(' '))}${i.referenciaCatastral ? ' · ' + esc(i.referenciaCatastral) : ''}</option>`).join('');
}
$('#fTercero').addEventListener('change', pintaInmueblesFactura);
$('#fLibro').addEventListener('change', pintaInmueblesFactura);

// ------------------------------------------------------------ libros registro de IVA/IGIC
const consultaLibro = () => `/fiscal/libros/${$('#lrLibro').value}?anio=${+$('#lrAnio').value}&periodo=${$('#lrPeriodo').value}`;
$('#lrVer').onclick = async () => {
  try {
    const d = await api(urlE(consultaLibro()));
    const rec = d.libro === 'RECIBIDAS', imp = d.impuesto, txt = { INTERIOR: 'Interior', EXENTA: 'Exenta', NO_SUJETA: 'No sujeta', NO_SUJETA_LOCAL: 'No sujeta (localización)', NO_SUJETA_OSS: 'No sujeta (OSS)', COMPENSACION_REAGP: 'Compensación REAGP', INTRACOM_ENTREGA: 'Entrega intracom.', INTRACOM_ADQUISICION: 'Adquisición intracom.', EXPORTACION: 'Exportación', IMPORTACION: 'Importación', INVERSION_SUJETO_PASIVO: 'Inv. sujeto pasivo' };
    $('#lrResumen').textContent = `${rec ? 'Facturas recibidas' : 'Facturas expedidas'} · ${imp} · del ${fechaES(d.desde)} al ${fechaES(d.hasta)} · ${d.facturas} factura(s). `
      + `Base ${fmt(d.totalBase)}, cuota ${fmt(d.totalCuota)}${d.totalRecargo ? ', recargo ' + fmt(d.totalRecargo) : ''}${rec ? ', deducible ' + fmt(d.totalCuotaDeducible) : ''}, total ${fmt(d.totalFacturas)}.`;
    $('#lrAvisos').innerHTML = d.avisos.map(a => `<li>${esc(a)}</li>`).join('');
    $('#lrDedCab').hidden = !rec;
    $('#lrTotales').innerHTML = d.totales.map(t => `<tr><td>${esc(txt[t.tipoOperacion] || t.tipoOperacion)}</td><td class="num">${fmt(t.porcentaje)} %</td>
        <td class="num">${t.porcentajeRecargo ? fmt(t.porcentajeRecargo) + ' %' : ''}</td><td class="num">${t.lineas}</td><td class="num">${fmt(t.base)}</td>
        <td class="num">${fmt(t.cuota)}</td><td class="num">${fmt(t.cuotaRecargo)}</td>${rec ? `<td class="num">${fmt(t.cuotaDeducible)}</td>` : ''}</tr>`).join('')
      || `<tr><td colspan="${rec ? 8 : 7}" class="vacio">Sin facturas en el periodo.</td></tr>`;
    $('#lrHead').innerHTML = `<tr>${rec ? '<th>Nº reg.</th><th>Registro</th>' : ''}<th>Expedición</th><th>Factura</th><th>NIF</th><th>Nombre</th><th>Operación</th>
      <th class="num">Base</th><th class="num">Tipo</th><th class="num">Cuota</th><th class="num">Recargo</th>${rec ? '<th class="num">Deducible</th>' : ''}
      <th class="num">Retención</th><th class="num">Total</th><th>Asiento</th></tr>`;
    $('#lrBody').innerHTML = d.lineas.map(l => `<tr>${rec ? `<td>${l.numeroRegistro ?? ''}</td><td>${fechaES(l.fechaContable)}</td>` : ''}
      <td>${fechaES(l.fechaExpedicion)}</td><td>${esc((l.serie || '') + l.numero)}${l.tipoFactura === 'RECTIFICATIVA' ? ' <small>(rectificativa)</small>' : ''}</td>
      <td>${esc(l.nif)}</td><td>${esc(l.nombre)}</td><td>${esc(txt[l.tipoOperacion] || l.tipoOperacion)}</td><td class="num">${fmt(l.base)}</td>
      <td class="num">${fmt(l.porcentaje)} %</td><td class="num">${fmt(l.cuota)}</td><td class="num">${l.cuotaRecargo ? fmt(l.cuotaRecargo) : ''}</td>
      ${rec ? `<td class="num">${fmt(l.cuotaDeducible)}</td>` : ''}<td class="num">${l.retencion ? fmt(l.retencion) : ''}</td>
      <td class="num">${l.totalFactura == null ? '' : fmt(l.totalFactura)}</td><td>${l.asientoNumero ? 'nº ' + l.asientoNumero : ''}</td></tr>`).join('')
      || `<tr><td colspan="15" class="vacio">Sin facturas en el periodo.</td></tr>`;
    $('#lrResultado').hidden = false; msg($('#lrMsg'), '', 'ayuda');
  } catch (e) { msg($('#lrMsg'), e.message, 'ayuda err-texto'); }
};
// La descarga necesita el token: se pide con fetch y se guarda como fichero
async function descargaLibro(q, porDefecto) {
  try {
    const r = await apiFetch(urlE(q), { headers: { Authorization: 'Bearer ' + S.token } });
    if (!r.ok) { let m = r.statusText; try { m = (await r.json()).error || m; } catch { /* sin cuerpo */ } throw new Error(m); }
    const nombre = ((/filename=([^;]+)/.exec(r.headers.get('Content-Disposition') || '') || [])[1] || porDefecto).replace(/"/g, '');
    const url = URL.createObjectURL(await r.blob());
    const a = Object.assign(document.createElement('a'), { href: url, download: nombre });
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
    msg($('#lrMsg'), `Descargado ${nombre}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#lrMsg'), e.message, 'ayuda err-texto'); }
}
$('#lrCsv').onclick = () => descargaLibro(consultaLibro().replace('?', '/csv?'), 'libro.csv');
// Los dos libros (expedidas y recibidas) en un solo fichero, con la disposición del modelo de la AEAT
$('#lrAeat').onclick = () => descargaLibro(`/fiscal/libros-aeat?anio=${+$('#lrAnio').value}&periodo=${$('#lrPeriodo').value}`, 'librosiva.xlsx');

function pintaPeriodos() {
  const m = MF.modelos.find(x => x.codigo === $('#mfModelo').value);
  $('#mfPeriodo').innerHTML = m ? m.periodos.map(p => `<option value="${esc(p)}">${esc(periodoTxt(p))}</option>`).join('') : '';
  $('#mfDevolucionLabel').hidden = !m || !['IVA', 'IGIC'].includes(m.impuesto) || m.periodos[0] === '0A';
}
$('#mfModelo').onchange = pintaPeriodos;

const peticionModelo = () => ({ modelo: $('#mfModelo').value, anio: +$('#mfAnio').value, periodo: $('#mfPeriodo').value, solicitaDevolucion: $('#mfDevolucion').value === '1',
  tipoDeclaracion: $('#mfTipo').value });

function muestraDeclaracion(v) {
  MF.actual = v;
  const caja = (n, x, cls) => `<div><span>${n}</span><strong class="${cls || ''}">${fmt(x)}</strong></div>`;
  $('#mfTitulo').textContent = `Modelo ${v.modelo} · ${v.anio} · ${periodoTxt(v.periodo)}` + (v.tipoDeclaracion && v.tipoDeclaracion !== 'ORDINARIA' ? ` · ${v.tipoDeclaracion.toLowerCase()}` : '');
  $('#mfSub').textContent = `${v.nombre}. Estado: ${estadoDecl[v.estado] || v.estado}. Plazo orientativo de presentación: ${fechaES(v.plazoDesde)} – ${fechaES(v.plazoHasta)}.`;
  $('#mfTotales').innerHTML = caja('Resultado', v.resultado, v.resultado < 0 ? 'estado-mal' : '')
    + (v.aIngresar ? caja('A ingresar', v.aIngresar) : '') + (v.aDevolver ? caja('A devolver', v.aDevolver) : '') + (v.aCompensar ? caja('A compensar', v.aCompensar) : '');
  $('#mfAvisos').innerHTML = v.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#mfCasillas').innerHTML = v.casillas.map(c => `<tr><td>${esc(c.clave)}</td><td>${esc(c.descripcion)}</td><td class="num">${fmt(c.importe)}</td></tr>`).join('')
    || '<tr><td colspan="3" class="vacio">No hay operaciones en este periodo.</td></tr>';
  const det = v.detalle, th = cols => `<tr>${cols.map(c => c.startsWith('#') ? `<th class="num">${esc(c.slice(1))}</th>` : `<th>${esc(c)}</th>`).join('')}</tr>`;
  let titulo = 'Terceros declarados', cab = '', cuerpo = '', rect = null;
  if (['111', '115'].includes(v.modelo) && Array.isArray(det)) {
    titulo = 'Perceptores'; cab = th(['NIF', 'Nombre', '#Base', '#Retención']);
    cuerpo = det.map(p => `<tr><td>${esc(p.nif)}</td><td>${esc(p.nombre)}</td><td class="num">${fmt(p.base)}</td><td class="num">${fmt(p.retencion)}</td></tr>`).join('')
      || '<tr><td colspan="4" class="vacio">Sin retenciones en el periodo.</td></tr>';
  } else if (['190', '180'].includes(v.modelo) && Array.isArray(det)) {
    titulo = 'Perceptores'; cab = th(['NIF', 'Nombre', 'Provincia', 'Clave', '#%', '#Base', '#Retención']);
    cuerpo = det.map(p => `<tr><td>${esc(p.nif)}</td><td>${esc(p.nombre)}</td><td>${esc(p.provincia || '—')}</td>
        <td>${esc(p.clave ? p.clave + (p.subclave || '') : '')}</td><td class="num">${fmt(p.porcentaje)}</td><td class="num">${fmt(p.base)}</td>
        <td class="num">${fmt(p.retencion)}</td></tr>`).join('') || '<tr><td colspan="7" class="vacio">Sin retenciones en el año.</td></tr>';
  } else if (v.modelo === '349' && det) {
    titulo = 'Operadores intracomunitarios'; cab = th(['NIF-IVA', 'Nombre', 'Clave', '#Base imponible']);
    cuerpo = det.operaciones.map(o => `<tr><td>${esc(o.nifComunitario)}</td><td>${esc(o.nombre)}</td><td>${esc(o.clave)}</td><td class="num">${fmt(o.base)}</td></tr>`).join('')
      || '<tr><td colspan="4" class="vacio">Sin operaciones en el periodo.</td></tr>';
    rect = det.rectificaciones;
  } else if (Array.isArray(det)) {
    cab = th(['NIF', 'Nombre', 'Clave', '#Total', '#1T', '#2T', '#3T', '#4T']);
    cuerpo = det.map(f => `<tr><td>${esc(f.nif)}</td><td>${esc(f.nombre)}</td><td>${esc(f.clave)}</td><td class="num">${fmt(f.total)}</td>
      <td class="num">${fmt(f.t1)}</td><td class="num">${fmt(f.t2)}</td><td class="num">${fmt(f.t3)}</td><td class="num">${fmt(f.t4)}</td></tr>`).join('')
      || '<tr><td colspan="8" class="vacio">Ningún tercero supera el umbral.</td></tr>';
  }
  $('#mfDetalle').hidden = !cab;
  $('#mfDetalleTitulo').textContent = titulo; $('#mfDetalleHead').innerHTML = cab; $('#mfDetalleBody').innerHTML = cuerpo;
  $('#mfRectificaciones').hidden = !(rect && rect.length);
  $('#mfRectBody').innerHTML = (rect || []).map(r => `<tr><td>${esc(r.nifComunitario)}</td><td>${esc(r.nombre)}</td><td>${esc(r.clave)}</td>
      <td>${esc(periodoTxt(r.periodo))} ${r.anio}</td><td class="num">${fmt(r.baseAnterior)}</td><td class="num">${fmt(r.baseRectificada)}</td></tr>`).join('');
  // Fichero AEAT: solo de declaraciones guardadas de los modelos con diseño de registro
  // 420/425: XML para el programa de ayuda de la Agencia Tributaria Canaria
  const atc = ['420', '425'].includes(v.modelo);
  const conFichero = !!v.id && (atc || ['303', '390', '111', '115', '130', '131', '309', '347', '349', '190', '180', '200', '202'].includes(v.modelo));
  $('#mfFichero').hidden = !conFichero;
  $('#mfFichero').querySelector('summary').textContent = atc ? 'Fichero XML para la Agencia Tributaria Canaria' : 'Fichero para la Agencia Tributaria';
  if (conFichero) {
    const autoliq = ['303', '111', '115', '130', '131', '309'].includes(v.modelo);
    const is = ['200', '202'].includes(v.modelo);
    $('#mfFDomL').hidden = !(autoliq || is) || v.resultado <= 0;
    $('#mfFIbanL').hidden = !autoliq && !is && v.modelo !== '420';
    $('#mfFTelL').hidden = autoliq || v.modelo === '202'; $('#mfFConL').hidden = autoliq || atc || is;
    $('#mfFCnaeL').hidden = !is;
    $('#mfFSvpL').hidden = $('#mfFMunL').hidden = !atc;
    $('#mfFPagoL').hidden = v.modelo !== '420' || v.resultado <= 0;
    $('#mfFExoL').hidden = !(v.modelo === '303' && ['4T', '12'].includes(v.periodo));
    msg($('#mfFMsg'), '', 'ayuda');
  }
  $('#mfResultado').hidden = false;
}

async function descargaPost(url, cuerpo, porDefecto) {
  const r = await apiFetch(url, { method: 'POST', headers: { Authorization: 'Bearer ' + S.token, 'Content-Type': 'application/json' },
    body: JSON.stringify(cuerpo) });
  if (!r.ok) {
    let m = r.statusText;
    try { const j = await r.json(); m = (j.error || m) + (j.detalle ? ' (' + j.detalle + ')' : ''); } catch { /* sin cuerpo */ }
    throw new Error(m);
  }
  const nombre = ((/filename=([^;]+)/.exec(r.headers.get('Content-Disposition') || '') || [])[1] || porDefecto).replace(/"/g, '');
  const enlace = URL.createObjectURL(await r.blob());
  const a = Object.assign(document.createElement('a'), { href: enlace, download: nombre });
  document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(enlace);
  return nombre;
}

$('#mfFDescargar').onclick = async () => {
  const v = MF.actual; if (!v || !v.id) return;
  try {
    const nombre = await descargaPost(urlE(`/fiscal/declaraciones/${v.id}/fichero`), {
      domiciliar: $('#mfFDom').value === 'true' && !$('#mfFDomL').hidden, iban: $('#mfFIban').value || null,
      telefono: $('#mfFTel').value || null, contacto: $('#mfFCon').value || null,
      exonerado390: $('#mfFExo').value === 'true' && !$('#mfFExoL').hidden,
      siglasVia: $('#mfFSvp').value || null, codigoMunicipio: $('#mfFMun').value || null, formaPagoAtc: $('#mfFPago').value,
      cnae: $('#mfFCnaeL').hidden ? null : $('#mfFCnae').value || null }, 'modelo.txt');
    msg($('#mfFMsg'), `Descargado ${nombre}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#mfFMsg'), e.message, 'ayuda err-texto'); }
};

$('#mfCalcular').onclick = async () => {
  try { muestraDeclaracion(await post(urlE('/fiscal/calcular'), peticionModelo())); msg($('#mfMsg'), '', 'ayuda'); $('#mfResultado').scrollIntoView({ behavior: 'smooth' }); }
  catch (e) { msg($('#mfMsg'), e.message, 'ayuda err-texto'); }
};

$('#mfGuardar').onclick = async () => {
  try {
    muestraDeclaracion(await post(urlE('/fiscal/declaraciones'), peticionModelo()));
    msg($('#mfMsg'), 'Declaración guardada.', 'ayuda ok-texto');
    await cargaDeclaraciones();
  } catch (e) { msg($('#mfMsg'), e.message, 'ayuda err-texto'); }
};

async function cargaDeclaraciones() {
  const filas = await api(urlE('/fiscal/declaraciones?anio=' + ($('#mfAnio').value || new Date().getFullYear())));
  const directo = permitido('FISCAL_DIRECTO'), abierto = S.ejercicio.estado === 'ABIERTO';
  $('#mfLista').innerHTML = filas.length ? filas.map(d => `<tr><td>${esc(d.modelo)} <small>${esc(d.nombre)}</small></td><td>${d.anio}</td><td>${esc(periodoTxt(d.periodo))}${d.numeroPresentacion > 1 ? ` <small>nº ${d.numeroPresentacion} · ${esc((d.tipoDeclaracion || '').toLowerCase())}</small>` : ''}</td>
    <td>${estadoDecl[d.estado] || esc(d.estado)}</td><td class="num">${d.resultado == null ? '' : fmt(d.resultado)}</td>
    <td>${d.fechaPresentacion ? fechaES(d.fechaPresentacion) + (d.justificante ? ' · ' + esc(d.justificante) : '') : ''}</td>
    <td>${d.asientoNumero ? 'nº ' + d.asientoNumero : ''}</td>
    <td class="fila-acciones"><button class="btn" data-ver="${d.id}">Ver</button>
      ${d.estado === 'CALCULADA' && directo ? `<button class="btn primario" data-pres="${d.id}">Presentar</button><button class="btn" data-desc="${d.id}">Descartar</button>` : ''}
      ${liquidable(d) && directo && !d.asientoNumero ? `<button class="btn primario" data-liq="${d.id}" ${abierto ? '' : 'disabled'}>Contabilizar liquidación</button>` : ''}
      ${d.asientoNumero && directo ? `<button class="btn" data-anliq="${d.id}" ${abierto ? '' : 'disabled'}>Anular asiento</button>` : ''}</td></tr>`).join('')
    : '<tr><td colspan="8" class="vacio">No hay declaraciones guardadas en este año.</td></tr>';
}
// 303/420: liquidación del IVA/IGIC; 202: pago fraccionado; 200: impuesto corriente y diferido del ejercicio
const liquidable = d => d.estado === 'PRESENTADA' && (['303', '420', '202'].includes(d.modelo) && d.periodo !== '0A' || d.modelo === '200');
$('#mfAnio').onchange = () => cargaDeclaraciones().catch(e => msg($('#mfMsg'), e.message, 'ayuda err-texto'));

$('#mfLista').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  try {
    if (b.dataset.ver) { muestraDeclaracion(await api(urlE('/fiscal/declaraciones/' + b.dataset.ver))); $('#mfResultado').scrollIntoView({ behavior: 'smooth' }); }
    if (b.dataset.liq) await previsualizaLiquidacion(b.dataset.liq, null);
    if (b.dataset.anliq && confirm('¿Anular el asiento de liquidación de esta declaración?')) {
      await api(urlX(`/fiscal/declaraciones/${b.dataset.anliq}/asiento`), { method: 'DELETE' });
      msg($('#mfMsg'), 'Asiento de liquidación anulado.', 'ayuda ok-texto'); await cargaDeclaraciones();
    }
    if (b.dataset.desc && confirm('¿Descartar esta declaración calculada?')) { await post(urlE(`/fiscal/declaraciones/${b.dataset.desc}/descartar`)); await cargaDeclaraciones(); }
    if (b.dataset.pres) {
      const v = await api(urlE('/fiscal/declaraciones/' + b.dataset.pres));
      MF.presentando = v;
      $('#mfPresentarTitulo').textContent = `Marcar como presentada: modelo ${v.modelo} ${v.anio} ${periodoTxt(v.periodo)}`;
      $('#mfFecha').value = hoy(); $('#mfJustificante').value = ''; msg($('#mfPresentarMsg'), '', 'ayuda');
      $('#mfPresentarPanel').hidden = false; $('#mfPresentarPanel').scrollIntoView({ behavior: 'smooth' });
    }
  } catch (err) { msg($('#mfMsg'), err.message, 'ayuda err-texto'); }
});

$('#mfPresentarCancelar').onclick = () => { MF.presentando = null; $('#mfPresentarPanel').hidden = true; };
$('#mfPresentar').onclick = async () => {
  if (!MF.presentando) return;
  try {
    const v = await post(urlE(`/fiscal/declaraciones/${MF.presentando.id}/presentar`), { fechaPresentacion: $('#mfFecha').value, justificante: $('#mfJustificante').value || null });
    MF.presentando = null; $('#mfPresentarPanel').hidden = true;
    muestraDeclaracion(v); msg($('#mfMsg'), 'Declaración marcada como presentada.', 'ayuda ok-texto');
    await cargaDeclaraciones();
  } catch (e) { msg($('#mfPresentarMsg'), e.message, 'ayuda err-texto'); }
};

// Asiento de liquidación del IVA/IGIC: primero se previsualiza; se contabiliza al confirmar
async function previsualizaLiquidacion(id, fecha) {
  const v = await post(urlX(`/fiscal/declaraciones/${id}/asiento/previsualizar`), fecha ? { fecha } : {});
  MF.liq = v;
  const canal = cid => (S.canales.find(c => c.id === cid) || {}).codigo || cid;
  $('#mfLiqTitulo').textContent = `Asiento de liquidación: modelo ${v.modelo} ${v.anio} ${periodoTxt(v.periodo)}`;
  $('#mfLiqFecha').value = v.fecha;
  $('#mfLiqAvisos').innerHTML = v.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#mfLiqCanales').innerHTML = v.canales.map(c => `<tr><td>${esc(c.canal)}</td><td class="num">${fmt(c.repercutido)}</td><td class="num">${fmt(c.soportado)}</td>
    <td class="num">${fmt(c.compensado)}</td><td class="num">${fmt(c.aIngresar)}</td><td class="num">${fmt(c.aFavor)}</td></tr>`).join('');
  $('#mfLiqApuntes').innerHTML = v.asiento.map(a => `<tr><td>${esc(a.cuenta)}</td><td>${esc(a.cuentaNombre)}</td><td>${esc(canal(a.canalId))}</td>
    <td class="num">${a.debe ? fmt(a.debe) : ''}</td><td class="num">${a.haber ? fmt(a.haber) : ''}</td></tr>`).join('');
  msg($('#mfLiqMsg'), '', 'ayuda');
  $('#mfLiqPanel').hidden = false; $('#mfLiqPanel').scrollIntoView({ behavior: 'smooth' });
}
$('#mfLiqFecha').onchange = () => MF.liq && previsualizaLiquidacion(MF.liq.declaracionId, $('#mfLiqFecha').value)
  .catch(e => msg($('#mfLiqMsg'), e.message, 'ayuda err-texto'));
$('#mfLiqCancelar').onclick = () => { MF.liq = null; $('#mfLiqPanel').hidden = true; };
$('#mfLiqGuardar').onclick = async () => {
  if (!MF.liq) return;
  try {
    const r = await post(urlX(`/fiscal/declaraciones/${MF.liq.declaracionId}/asiento`), { fecha: $('#mfLiqFecha').value });
    MF.liq = null; $('#mfLiqPanel').hidden = true;
    msg($('#mfMsg'), `Liquidación contabilizada: asiento nº ${r.asientoNumero}.`, 'ayuda ok-texto');
    await cargaDeclaraciones();
  } catch (e) { msg($('#mfLiqMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ inmovilizado
const IN = { categorias: [], ficha: null };
const estadoInm = { ALTA: 'En alta', BAJA: 'Baja', VENDIDO: 'Vendido' };
const cuentaPorCodigo = cod => { const c = S.cuentas.find(x => x.codigo === (cod || '').trim()); return c ? c.id : null; };
const dentroEj = d => d < S.ejercicio.fechaInicio ? S.ejercicio.fechaInicio : d > S.ejercicio.fechaFin ? S.ejercicio.fechaFin : d;

async function cargaInmovilizado() {
  IN.categorias = await api(urlE('/inmovilizado/categorias'));
  $('#inCat').innerHTML = IN.categorias.map(c => `<option value="${c.id}">${esc(c.nombre)}</option>`).join('');
  $('#inCanal').innerHTML = S.canales.map(c => `<option value="${c.id}">${esc(c.codigo)} · ${esc(c.nombre)}</option>`).join('');
  $('#caLista').innerHTML = IN.categorias.map(c => `<tr><td>${esc(c.nombre)}</td><td>${esc(c.tipo.toLowerCase())}</td><td>${esc(c.cuentaInmovilizado || '')}</td>
      <td>${esc(c.cuentaAmortAcumulada || '')}</td><td>${esc(c.cuentaGastoAmort || '')}</td><td>${c.metodoDefecto === 'LINEAL' ? 'Lineal' : 'No se amortiza'}</td>
      <td class="num">${c.vidaUtilMesesDefecto ? c.vidaUtilMesesDefecto + ' meses' : ''}</td></tr>`).join('')
    || '<tr><td colspan="7" class="vacio">No hay categorías.</td></tr>';
  pintaMesesCategoria();
  if (!$('#amHasta').value || $('#amHasta').value < S.ejercicio.fechaInicio || $('#amHasta').value > S.ejercicio.fechaFin) $('#amHasta').value = S.ejercicio.fechaFin;
  const anioEj = +S.ejercicio.fechaFin.slice(0, 4);
  if (!$('#biAnio').value || $('#biAnio').dataset.ej !== String(S.ejercicio.id)) { $('#biAnio').value = anioEj; $('#biAnio').dataset.ej = S.ejercicio.id; }
  if (!$('#biPeriodo').options.length)
    $('#biPeriodo').innerHTML = ['4T', '3T', '2T', '1T', '12', '11', '10', '09', '08', '07', '06', '05', '04', '03', '02', '01']
      .map(p => `<option value="${p}">${esc(periodoTxt(p))}</option>`).join('');
  await Promise.all([listaInmovilizado(), listaAmortizaciones(), listaBienesInversion()]);
}
function pintaMesesCategoria() {
  const c = IN.categorias.find(x => String(x.id) === $('#inCat').value);
  $('#inMeses').placeholder = c && c.vidaUtilMesesDefecto ? `${c.vidaUtilMesesDefecto} (de la categoría)` : '';
  $('#inMeses').disabled = c && c.metodoDefecto === 'NO_AMORTIZA';
}
$('#inCat').onchange = pintaMesesCategoria;

async function listaInmovilizado() {
  const filas = await api(urlE('/inmovilizado' + ($('#inEstado').value ? '?estado=' + $('#inEstado').value : '')));
  $('#inLista').innerHTML = (filas.map(e => `<tr><td>${esc(e.codigo)}</td><td>${esc(e.descripcion)}</td><td>${esc(e.categoria)}</td><td>${esc(e.canal)}</td>
      <td class="num">${fmt(e.valorAdquisicion)}</td><td class="num">${fmt(e.amortizado)}</td><td class="num">${fmt(e.valorNeto)}</td>
      <td>${estadoInm[e.estado] || esc(e.estado)}${e.fechaBaja ? ' · ' + fechaES(e.fechaBaja) : ''}</td>
      <td class="fila-acciones"><button class="btn" data-ver="${e.id}">Ver</button></td></tr>`).join('')
    + (filas.length ? `<tr class="total"><td colspan="4">Total</td><td class="num">${fmt(filas.reduce((a, e) => a + e.valorAdquisicion, 0))}</td>
      <td class="num">${fmt(filas.reduce((a, e) => a + e.amortizado, 0))}</td><td class="num">${fmt(filas.reduce((a, e) => a + e.valorNeto, 0))}</td><td colspan="2"></td></tr>` : ''))
    || '<tr><td colspan="9" class="vacio">No hay elementos.</td></tr>';
}
$('#inEstado').onchange = () => listaInmovilizado().catch(e => msg($('#inMsg'), e.message, 'ayuda err-texto'));
$('#inLista').addEventListener('click', e => { const b = e.target.closest('button[data-ver]'); if (b) verFicha(b.dataset.ver).catch(err => msg($('#inMsg'), err.message, 'ayuda err-texto')); });

$('#inNuevoBtn').onclick = () => {
  ['#inDesc', '#inCodigo', '#inValor', '#inMeses', '#inContra', '#inFIni', '#inCoef'].forEach(i => $(i).value = ''); $('#inMetodo').value = '';
  $('#inResidual').value = '0'; $('#inPrevia').value = '0'; $('#inFAdq').value = dentroEj(hoy());
  msg($('#inNuevoMsg'), '', 'ayuda'); $('#inNuevo').hidden = false; $('#inNuevo').scrollIntoView({ behavior: 'smooth' });
};
$('#inCancelar').onclick = () => $('#inNuevo').hidden = true;
$('#inGuardar').onclick = async () => {
  try {
    const contra = $('#inContra').value.trim();
    if (contra && !cuentaPorCodigo(contra)) throw new Error(`La cuenta ${contra} no existe.`);
    const f = await post(urlX('/inmovilizado'), {
      descripcion: $('#inDesc').value, categoriaId: +$('#inCat').value, canalId: +$('#inCanal').value, codigo: $('#inCodigo').value || null,
      fechaAdquisicion: $('#inFAdq').value, fechaInicioAmortizacion: $('#inFIni').value || null, valorAdquisicion: num($('#inValor').value),
      valorResidual: num($('#inResidual').value), vidaUtilMeses: $('#inMeses').value ? parseInt($('#inMeses').value, 10) : null,
      metodo: $('#inMetodo').value || null, coeficiente: $('#inCoef').value.trim() ? num($('#inCoef').value) : null,
      amortizacionPrevia: num($('#inPrevia').value), cuentaContrapartidaId: contra ? cuentaPorCodigo(contra) : null
    });
    $('#inNuevo').hidden = true;
    msg($('#inMsg'), `Alta de ${f.elemento.codigo} ${f.elemento.descripcion}.`, 'ayuda ok-texto');
    await listaInmovilizado(); pintaFicha(f);
  } catch (e) { msg($('#inNuevoMsg'), e.message, 'ayuda err-texto'); }
};

async function verFicha(id) { pintaFicha(await api(urlE('/inmovilizado/' + id))); }
function pintaFicha(f) {
  IN.ficha = f; const e = f.elemento;
  $('#inFichaTitulo').textContent = `${e.codigo} · ${e.descripcion}`;
  $('#inFichaSub').textContent = `${e.categoria} · canal ${e.canal} · adquirido el ${fechaES(e.fechaAdquisicion)} por ${fmt(e.valorAdquisicion)}`
    + (e.metodo === 'NO_AMORTIZA' || !f.finAmortizacion ? ' · no se amortiza'
       : ` · amortización ${metodoTxt[e.metodo] || e.metodo}${e.vidaUtilMeses ? ' en ' + e.vidaUtilMeses + ' meses' : ''}${e.coeficiente ? ' al ' + fmt(e.coeficiente) + ' %' : ''} desde el ${fechaES(e.fechaInicioAmortizacion)} hasta el ${fechaES(f.finAmortizacion)}`)
    + (e.valorResidual ? ` · valor residual ${fmt(e.valorResidual)}` : '') + (e.amortizacionPrevia ? ` · amortización previa ${fmt(e.amortizacionPrevia)}` : '')
    + ` · ${estadoInm[e.estado]}${e.fechaBaja ? ' el ' + fechaES(e.fechaBaja) : ''}.`;
  $('#inCuadro').innerHTML = f.cuadro.map(t => `<tr><td>${fechaES(t.desde)}</td><td>${fechaES(t.hasta)}</td><td class="num">${fmt(t.cuota)}</td>
      <td class="num">${fmt(t.acumulada)}</td><td class="num">${fmt(t.valorNeto)}</td></tr>`).join('') || '<tr><td colspan="5" class="vacio">No se amortiza.</td></tr>';
  $('#inMovs').innerHTML = f.movimientos.map(m => `<tr><td>${m.desde ? fechaES(m.desde) : ''} – ${m.hasta ? fechaES(m.hasta) : ''}</td><td class="num">${fmt(m.importe)}</td>
      <td class="num">${fmt(m.acumulada)}</td><td>${m.asientoNumero ? 'nº ' + m.asientoNumero + ' (' + esc(m.ejercicio) + ')' : ''}</td></tr>`).join('')
    || '<tr><td colspan="4" class="vacio">Todavía no se ha contabilizado amortización.</td></tr>';
  pintaBienInversion(e).catch(err => msg($('#inFichaMsg'), err.message, 'ayuda err-texto'));
  $('#inFiscalResumen').textContent = e.metodoFiscal
    ? `Fiscal: ${metodoTxt[e.metodoFiscal] || e.metodoFiscal}${e.vidaUtilMesesFiscal ? ' en ' + e.vidaUtilMesesFiscal + ' meses' : ''}${e.coeficienteFiscal ? ' al ' + fmt(e.coeficienteFiscal) + ' %' : ''}. La diferencia con la contable es un ajuste en el Impuesto sobre Sociedades.`
    : 'La amortización fiscal es igual que la contable.';
  $('#inFMetodo').value = e.metodoFiscal || ''; $('#inFMeses').value = e.vidaUtilMesesFiscal || ''; $('#inFCoef').value = e.coeficienteFiscal ? String(e.coeficienteFiscal).replace('.', ',') : '';
  $('#inBFecha').value = dentroEj(hoy()); $('#inBTipo').value = 'BAJA'; $('#inBImporte').value = '0'; $('#inBCobro').value = '';
  $('#inBaja').hidden = e.estado !== 'ALTA'; $('#inAnularBaja').hidden = e.estado === 'ALTA';
  $$('#inBajaForm .cab2').forEach(x => x.hidden = e.estado !== 'ALTA');
  msg($('#inFichaMsg'), '', 'ayuda');
  $('#inFicha').hidden = false; $('#inFicha').scrollIntoView({ behavior: 'smooth' });
}
$('#inBaja').onclick = async () => {
  if (!IN.ficha) return;
  try {
    const venta = $('#inBTipo').value === 'VENTA', cobro = $('#inBCobro').value.trim();
    if (venta && !cuentaPorCodigo(cobro)) throw new Error('Indica la cuenta en la que se cobra la venta.');
    if (!confirm('Se contabilizará la amortización hasta esa fecha y la baja del elemento. ¿Continuar?')) return;
    const r = await post(urlX(`/inmovilizado/${IN.ficha.elemento.id}/baja`), {
      fecha: $('#inBFecha').value, tipo: $('#inBTipo').value, importeVenta: venta ? num($('#inBImporte').value) : 0, cuentaCobroId: venta ? cuentaPorCodigo(cobro) : null });
    const res = r.resultado.resultado;
    await verFicha(IN.ficha.elemento.id); await listaInmovilizado();
    msg($('#inFichaMsg'), `Baja contabilizada (asiento nº ${r.asientoNumero}). ${res > 0 ? 'Beneficio' : res < 0 ? 'Pérdida' : 'Resultado'}: ${fmt(Math.abs(res))}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#inFichaMsg'), e.message, 'ayuda err-texto'); }
};
$('#inAnularBaja').onclick = async () => {
  if (!IN.ficha || !confirm('¿Anular la baja? El elemento vuelve a estar en alta.')) return;
  try { await api(urlX(`/inmovilizado/${IN.ficha.elemento.id}/baja`), { method: 'DELETE' }); await verFicha(IN.ficha.elemento.id); await listaInmovilizado(); msg($('#inFichaMsg'), 'Baja anulada.', 'ayuda ok-texto'); }
  catch (e) { msg($('#inFichaMsg'), e.message, 'ayuda err-texto'); }
};

const metodoTxt = { LINEAL: 'lineal', DEGRESIVO: 'degresiva', DIGITOS_DECRECIENTE: 'por suma de dígitos decreciente', DIGITOS_CRECIENTE: 'por suma de dígitos creciente', COEFICIENTE_TABLA: 'por coeficiente de tablas', LIBRE: 'libre', NO_AMORTIZA: 'no se amortiza' };
$('#inFGuardar').onclick = async () => {
  if (!IN.ficha) return;
  try {
    const f = await api(urlE(`/inmovilizado/${IN.ficha.elemento.id}/amortizacion-fiscal`), { method: 'PUT', body: JSON.stringify({
      metodo: $('#inFMetodo').value || null, vidaUtilMeses: $('#inFMeses').value ? parseInt($('#inFMeses').value, 10) : null,
      coeficiente: $('#inFCoef').value.trim() ? num($('#inFCoef').value) : null }) });
    pintaFicha(f); msg($('#inFichaMsg'), 'Amortización fiscal guardada.', 'ayuda ok-texto');
  } catch (e) { msg($('#inFichaMsg'), e.message, 'ayuda err-texto'); }
};
$('#dfVer').onclick = async () => {
  try {
    const d = await api(urlX(`/inmovilizado/diferencias-fiscales?tipo=${num($('#dfTipo').value) || 25}`));
    $('#dfAvisos').innerHTML = d.avisos.map(a => `<li>${esc(a)}</li>`).join('');
    $('#dfLista').innerHTML = d.elementos.map(x => `<tr><td>${esc(x.codigo)}</td><td>${esc(x.descripcion)}</td>
        <td>${esc(metodoTxt[x.metodoContable] || x.metodoContable)} / ${esc(metodoTxt[x.metodoFiscal] || x.metodoFiscal)}</td>
        <td class="num">${fmt(x.contable)}</td><td class="num">${fmt(x.fiscal)}</td><td class="num">${fmt(x.ajuste)}</td><td class="num">${fmt(x.acumulada)}</td></tr>`).join('')
      || '<tr><td colspan="7" class="vacio">Sin diferencias.</td></tr>';
    const caja = (n, v) => `<div><span>${n}</span><strong>${fmt(v)}</strong></div>`;
    $('#dfTotales').innerHTML = caja('Aumentos', d.aumentos) + caja('Disminuciones', d.disminuciones) + caja('Activo diferido (4740)', d.activoDiferido)
      + caja('Pasivo diferido (479)', d.pasivoDiferido);
  } catch (e) { msg($('#inMsg'), e.message, 'ayuda err-texto'); }
};

// ---- bienes de inversión (regularización del IVA/IGIC, arts. 107-110 LIVA)
const tipoReg = { INICIO: 'inicio', ANUAL: 'anual', TRANSMISION: 'venta' };
const textoCuadro = b => b.cuadro.filter(c => c.tipo !== 'INICIO').map(c =>
  `${c.anio} ${tipoReg[c.tipo]}${c.prorrata != null ? ' ' + c.prorrata + ' %' : ''}: ${fmt(c.importe)}${c.contabilizada ? ' (asiento nº ' + c.asientoNumero + ')' : c.importe ? ' (pendiente)' : ''}`
).join(' · ');
async function libroBienes(anio) { return api(urlE('/inmovilizado/bienes-inversion?anio=' + anio)); }
async function listaBienesInversion() {
  const l = await libroBienes(+$('#biAnio').value || new Date().getFullYear());
  $('#biAvisos').innerHTML = l.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  const asientos = new Map();
  l.bienes.forEach(b => b.cuadro.filter(c => c.asientoId && c.anio === l.anio).forEach(c => asientos.set(c.asientoId, c.asientoNumero)));
  $('#biLista').innerHTML = (l.bienes.map(b => `<tr><td>${esc(b.codigo)}</td><td>${esc(b.descripcion)}</td><td>${b.anioInicio}</td><td class="num">${b.anios}</td>
      <td class="num">${fmt(b.cuotaSoportada)}</td><td class="num">${b.prorrataInicial} %</td><td class="num">${fmt(b.deduccionInicial)}</td>
      <td>${esc(textoCuadro(b)) || '<span class="ayuda">—</span>'}</td><td>${estadoInm[b.estado] || esc(b.estado)}</td></tr>`).join('')
    + (l.bienes.length ? `<tr class="total"><td colspan="7">Regularización de ${l.anio}</td><td colspan="2">${fmt(l.regularizacionAnio)} · contabilizado ${fmt(l.contabilizado)}</td></tr>` : ''))
    || '<tr><td colspan="9" class="vacio">No hay bienes en el registro. Se dan de alta desde la ficha del elemento.</td></tr>';
  $('#biAsientos').innerHTML = permitido('INMOVILIZADO_DIRECTO') && S.ejercicio.estado === 'ABIERTO'
    ? [...asientos].map(([id, n]) => `<button class="btn" data-anular-bi="${id}">Anular el asiento nº ${n}</button>`).join(' ') : '';
}
$('#biVer').onclick = () => listaBienesInversion().catch(e => msg($('#biMsg'), e.message, 'ayuda err-texto'));
$('#biContabilizar').onclick = async () => {
  try {
    const r = await post(urlX('/inmovilizado/bienes-inversion/regularizar'), { anio: +$('#biAnio').value, periodo: $('#biPeriodo').value });
    await listaBienesInversion();
    msg($('#biMsg'), `Regularización contabilizada: asiento nº ${r.asientoNumero} por ${fmt(r.importe)}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#biMsg'), e.message, 'ayuda err-texto'); }
};
$('#biAsientos').addEventListener('click', async e => {
  const b = e.target.closest('button[data-anular-bi]'); if (!b || !confirm('¿Anular la regularización contabilizada?')) return;
  try { await api(urlX('/inmovilizado/bienes-inversion/regularizacion/' + b.dataset.anularBi), { method: 'DELETE' }); await listaBienesInversion(); msg($('#biMsg'), 'Regularización anulada.', 'ayuda ok-texto'); }
  catch (err) { msg($('#biMsg'), err.message, 'ayuda err-texto'); }
});
async function pintaBienInversion(e) {
  const l = await libroBienes(+$('#biAnio').value || new Date().getFullYear());
  const b = l.bienes.find(x => x.inmovilizadoId === e.id);
  $('#inBIResumen').textContent = b
    ? `En el registro desde ${b.anioInicio} hasta ${b.ultimoAnio}${b.esInmueble ? ' (inmueble)' : ''}: cuota soportada ${fmt(b.cuotaSoportada)}, prorrata del año de inicio ${b.prorrataInicial} %, `
      + `deducción ${fmt(b.deduccionInicial)}.${b.sectorDiferenciado != null ? ' Sector diferenciado ' + b.sectorDiferenciado + '.' : ''}`
      + `${b.transmisionExenta ? ' Venta exenta o no sujeta.' : ''}`
      + `${b.porcentajes.length ? ' % fijados: ' + b.porcentajes.map(p => p.anio + ': ' + p.porcentaje + ' %').join(', ') + '.' : ''} ${textoCuadro(b)}`
    : e.valorAdquisicion > 3005.06 ? 'No está en el registro de bienes de inversión. Si su IVA/IGIC se dedujo con prorrata, regístralo para regularizarlo los años siguientes.'
      : 'Valor de 3.005,06 € o menos: no es bien de inversión a efectos del impuesto.';
  $('#inBIForm').hidden = !!b || e.valorAdquisicion <= 3005.06 || !permitido('INMOVILIZADO:ESCRITURA');
  $('#inBIQuitar').hidden = !b;
  $('#inBIAfecta').hidden = !b || !permitido('INMOVILIZADO:ESCRITURA');
  ['#inBICuota', '#inBIProrrata', '#inBIAnio', '#inBISector', '#inBIPAnio', '#inBIPPct'].forEach(i => $(i).value = ''); $('#inBIInmueble').checked = $('#inBIExenta').checked = false;
}
$('#inBIAlta').onclick = async () => {
  if (!IN.ficha) return;
  try {
    const n = v => v.trim() ? num(v) : null;
    await post(urlE(`/inmovilizado/${IN.ficha.elemento.id}/bien-inversion`), {
      cuotaSoportada: n($('#inBICuota').value), prorrataInicial: $('#inBIProrrata').value.trim() ? parseInt($('#inBIProrrata').value, 10) : null,
      anioInicio: $('#inBIAnio').value.trim() ? parseInt($('#inBIAnio').value, 10) : null,
      sectorDiferenciado: $('#inBISector').value.trim() ? parseInt($('#inBISector').value, 10) : null,
      esInmueble: $('#inBIInmueble').checked, transmisionExenta: $('#inBIExenta').checked });
    await Promise.all([pintaBienInversion(IN.ficha.elemento), listaBienesInversion()]);
    msg($('#inFichaMsg'), 'Registrado como bien de inversión.', 'ayuda ok-texto');
  } catch (e) { msg($('#inFichaMsg'), e.message, 'ayuda err-texto'); }
};
$('#inBIPGuardar').onclick = async () => {
  if (!IN.ficha) return;
  try {
    await put(urlE(`/inmovilizado/${IN.ficha.elemento.id}/bien-inversion/porcentajes`), { anio: parseInt($('#inBIPAnio').value, 10),
      porcentaje: $('#inBIPPct').value.trim() ? parseInt($('#inBIPPct').value, 10) : null });
    await Promise.all([pintaBienInversion(IN.ficha.elemento), listaBienesInversion()]);
    msg($('#inFichaMsg'), 'Porcentaje del año guardado: la regularización se recalcula.', 'ayuda ok-texto');
  } catch (e) { msg($('#inFichaMsg'), e.message, 'ayuda err-texto'); }
};
$('#inBIQuitar').onclick = async () => {
  if (!IN.ficha || !confirm('¿Quitar el elemento del registro de bienes de inversión?')) return;
  try {
    await api(urlE(`/inmovilizado/${IN.ficha.elemento.id}/bien-inversion`), { method: 'DELETE' });
    await Promise.all([pintaBienInversion(IN.ficha.elemento), listaBienesInversion()]);
    msg($('#inFichaMsg'), 'Quitado del registro.', 'ayuda ok-texto');
  } catch (e) { msg($('#inFichaMsg'), e.message, 'ayuda err-texto'); }
};

function pintaAmortizacion(v) {
  $('#amAvisos').innerHTML = v.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#amLineas').innerHTML = (v.lineas.map(l => `<tr><td>${esc(l.codigo)}</td><td>${esc(l.descripcion)}</td><td>${esc(l.canal)}</td><td class="num">${fmt(l.importe)}</td>
      <td class="num">${fmt(l.acumulada)}</td><td class="num">${fmt(l.valorNeto)}</td></tr>`).join('')
    + (v.lineas.length ? `<tr class="total"><td colspan="3">Total</td><td class="num">${fmt(v.total)}</td><td colspan="2"></td></tr>` : ''))
    || '<tr><td colspan="6" class="vacio">No hay amortización pendiente hasta esa fecha.</td></tr>';
}
async function listaAmortizaciones() {
  if (!permitido('INMOVILIZADO_DIRECTO')) return;
  const filas = await api(urlX('/inmovilizado/amortizacion'));
  $('#amHechas').innerHTML = filas.map(a => `<tr><td>nº ${a.numero}</td><td>${fechaES(a.fecha)}</td><td class="num">${a.elementos}</td><td class="num">${fmt(a.total)}</td>
      <td class="fila-acciones"><button class="btn" data-anular="${a.asientoId}" ${S.ejercicio.estado === 'ABIERTO' ? '' : 'disabled'}>Anular</button></td></tr>`).join('')
    || '<tr><td colspan="5" class="vacio">Ninguna.</td></tr>';
}
$('#amPrevia').onclick = async () => {
  try { pintaAmortizacion(await post(urlX('/inmovilizado/amortizacion/previsualizar'), { hasta: $('#amHasta').value })); msg($('#amMsg'), '', 'ayuda'); }
  catch (e) { msg($('#amMsg'), e.message, 'ayuda err-texto'); }
};
$('#amGuardar').onclick = async () => {
  try {
    const v = await post(urlX('/inmovilizado/amortizacion'), { hasta: $('#amHasta').value });
    pintaAmortizacion(v); await Promise.all([listaAmortizaciones(), listaInmovilizado()]);
    msg($('#amMsg'), `Amortización contabilizada: asiento nº ${v.asientoNumero} por ${fmt(v.total)}.`, 'ayuda ok-texto');
  } catch (e) { msg($('#amMsg'), e.message, 'ayuda err-texto'); }
};
$('#amHechas').addEventListener('click', async e => {
  const b = e.target.closest('button[data-anular]'); if (!b || !confirm('¿Anular esta amortización?')) return;
  try { await api(urlX('/inmovilizado/amortizacion/' + b.dataset.anular), { method: 'DELETE' }); await Promise.all([listaAmortizaciones(), listaInmovilizado()]); msg($('#amMsg'), 'Amortización anulada.', 'ayuda ok-texto'); }
  catch (err) { msg($('#amMsg'), err.message, 'ayuda err-texto'); }
});

$('#caGuardar').onclick = async () => {
  try {
    const id = cod => { if (!cod.trim()) return null; const c = cuentaPorCodigo(cod); if (!c) throw new Error(`La cuenta ${cod} no existe.`); return c; };
    await post(urlE('/inmovilizado/categorias'), {
      nombre: $('#caNombre').value, tipo: $('#caTipo').value, metodoDefecto: $('#caMetodo').value, cuentaInmovilizadoId: id($('#caInm').value),
      cuentaAmortAcumuladaId: id($('#caAmo').value), cuentaGastoAmortId: id($('#caGas').value),
      vidaUtilMesesDefecto: $('#caMeses').value ? parseInt($('#caMeses').value, 10) : null });
    ['#caNombre', '#caInm', '#caAmo', '#caGas', '#caMeses'].forEach(i => $(i).value = '');
    msg($('#caMsg'), 'Categoría creada.', 'ayuda ok-texto'); await cargaInmovilizado();
  } catch (e) { msg($('#caMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ balances
function cargaBalances() {
  if (!$('#baCanal').options.length || $('#baCanal').dataset.empresa !== String(S.empresa.id)) {
    $('#baCanal').innerHTML = baseCanales().map(c => `<option value="${c.id}">Canal ${esc(c.codigo)}</option>`).join('');
    $('#baCanal').dataset.empresa = String(S.empresa.id);
  }
  if ($('#baHasta').dataset.ejercicio !== String(S.ejercicio.id)) {
    for (const id of ['#baDesde', '#baHasta']) { $(id).min = S.ejercicio.fechaInicio; $(id).max = S.ejercicio.fechaFin; }
    $('#baDesde').value = S.ejercicio.fechaInicio; $('#baHasta').value = S.ejercicio.fechaFin;
    $('#baHasta').dataset.ejercicio = String(S.ejercicio.id);
  }
  pintaControlesBalance();
  return verBalance();
}

function pintaControlesBalance() {
  const inf = $('#baInforme').value, anual = ['patrimonio-neto', 'flujos-efectivo', 'memoria'].includes(inf);
  $('#baDesdeL').hidden = inf === 'situacion' || anual;
  $('#baHasta').parentElement.hidden = anual;
  $('#baNivelL').hidden = $('#baIncluirL').hidden = inf !== 'sumas-saldos';
  $('#baFormatoL').hidden = !['situacion', 'perdidas-ganancias'].includes(inf);
}
$('#baInforme').onchange = () => { pintaControlesBalance(); verBalance(); };

async function verBalance() {
  const inf = $('#baInforme').value;
  const anual = ['patrimonio-neto', 'flujos-efectivo', 'memoria'].includes(inf);
  const q = new URLSearchParams({ canalId: $('#baCanal').value });
  if (!anual) q.set('hasta', $('#baHasta').value);
  if (!anual && inf !== 'situacion') q.set('desde', $('#baDesde').value);
  if (inf === 'sumas-saldos') { q.set('nivel', $('#baNivel').value); q.set('incluir', $('#baIncluir').value); }
  if (['situacion', 'perdidas-ganancias'].includes(inf) && $('#baFormato').value) q.set('formato', $('#baFormato').value);
  try {
    const r = await api(urlX(`/balances/${inf}?${q}`));
    msg($('#baMsg'), '', 'ayuda');
    $('#baTabla').hidden = inf === 'memoria'; $('#baExtra').innerHTML = '';
    if (inf === 'sumas-saldos') pintaSumasSaldos(r);
    else if (inf === 'patrimonio-neto') pintaEcpn(r);
    else if (inf === 'flujos-efectivo') pintaEfe(r);
    else if (inf === 'memoria') pintaMemoria(r);
    else pintaEstado(r);
  } catch (e) { msg($('#baMsg'), e.message, 'ayuda err-texto'); }
}
$('#baVer').onclick = verBalance;
$('#baImprimir').onclick = () => window.print();

function cabeceraBalance(titulo, r) {
  $('#baTitulo').textContent = `${titulo} · ${S.empresa.nombre}`;
  $('#baSub').textContent = `Ejercicio ${S.ejercicio.codigo} · canal ${r.canal.codigo} · ` +
    (r.desde && titulo !== 'Balance de situación' ? `del ${fechaES(r.desde)} al ${fechaES(r.hasta)}` : `a ${fechaES(r.hasta)}`);
}

function pintaSumasSaldos(r) {
  cabeceraBalance('Balance de sumas y saldos', r);
  $('#baAvisos').innerHTML = r.cuadra ? '' : '<li>Las sumas del debe y del haber no coinciden.</li>';
  $('#baHead').innerHTML = '<tr><th>Cuenta</th><th>Nombre</th><th class="num">Debe</th><th class="num">Haber</th><th class="num">Saldo deudor</th><th class="num">Saldo acreedor</th></tr>';
  $('#baBody').innerHTML = (r.filas.map(f => `<tr><td>${esc(f.codigo)}</td><td>${esc(f.nombre)}</td><td class="num">${fmt(f.debe)}</td>
      <td class="num">${fmt(f.haber)}</td><td class="num">${f.saldoDeudor ? fmt(f.saldoDeudor) : ''}</td><td class="num">${f.saldoAcreedor ? fmt(f.saldoAcreedor) : ''}</td></tr>`).join('')
    || '<tr><td colspan="6" class="vacio">No hay movimientos en esas fechas.</td></tr>')
    + `<tr class="total"><td colspan="2">Totales</td><td class="num">${fmt(r.totalDebe)}</td><td class="num">${fmt(r.totalHaber)}</td>
       <td class="num">${fmt(r.totalSaldoDeudor)}</td><td class="num">${fmt(r.totalSaldoAcreedor)}</td></tr>`;
  $('#baPista').textContent = '';
}

function pintaEstado(r) {
  cabeceraBalance(r.titulo, r);
  const ant = r.ejercicioAnterior;
  $('#baAvisos').innerHTML = r.avisos.map(a => `<li>${esc(a)}</li>`).join('')
    + r.sinClasificar.map(s => `<li>Sin partida: ${esc(s.codigo)} ${esc(s.nombre)} (saldo ${fmt(s.saldo)})</li>`).join('');
  $('#baHead').innerHTML = `<tr><th>Partida</th><th class="num">${esc(r.ejercicio)}</th>${ant ? `<th class="num">${esc(ant)}</th>` : ''}</tr>`;
  const cols = ant ? 3 : 2;
  $('#baBody').innerHTML = r.partidas
    .filter(p => p.esTotal || p.importe || p.importeAnterior)
    .map(p => {
      const sangria = `style="padding-left:${8 + p.nivel * 18}px"`;
      const fila = `<tr class="partida ${p.esTotal && p.nivel === 0 ? 'total' : ''}" ${p.cuentas.length ? `data-abre="${esc(p.id)}"` : ''}>
        <td ${sangria}>${esc(p.texto)}</td><td class="num">${fmt(p.importe)}</td>${ant ? `<td class="num">${p.importeAnterior == null ? '' : fmt(p.importeAnterior)}</td>` : ''}</tr>`;
      const detalle = p.cuentas.map(c => `<tr class="detalle" data-de="${esc(p.id)}" hidden><td style="padding-left:${26 + p.nivel * 18}px">${esc(c.codigo)} ${esc(c.nombre)}</td>
        <td class="num">${fmt(c.importe)}</td>${cols === 3 ? '<td></td>' : ''}</tr>`).join('');
      return fila + detalle;
    }).join('');
  $('#baPista').textContent = 'Pulsa una partida para ver sus cuentas. Solo se muestran las partidas con importe.';
}
function cabeceraAnual(titulo, r) {
  $('#baTitulo').textContent = `${titulo} · ${S.empresa.nombre}`;
  $('#baSub').textContent = `Ejercicio ${r.ejercicio} (del ${fechaES(r.desde)} al ${fechaES(r.hasta)})` + (r.canal ? ` · canal ${r.canal.codigo}` : '');
  $('#baAvisos').innerHTML = r.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#baPista').textContent = '';
}
const filaLinea = l => `<tr class="partida ${l.esTotal && l.nivel === 0 ? 'total' : ''}"><td style="padding-left:${8 + l.nivel * 18}px">${esc(l.texto)}</td>
  <td class="num">${l.esTotal || l.importe ? fmt(l.importe) : ''}</td></tr>`;
function pintaEcpn(r) {
  cabeceraAnual('Estado de cambios en el patrimonio neto', r);
  $('#baHead').innerHTML = `<tr><th>A) Estado de ingresos y gastos reconocidos</th><th class="num">${esc(r.ejercicio)}</th></tr>`;
  $('#baBody').innerHTML = r.ingresosGastos.map(filaLinea).join('');
  const cols = r.columnas;
  $('#baExtra').innerHTML = `<h3>B) Estado total de cambios en el patrimonio neto</h3><div style="overflow-x:auto"><table class="balance">
    <thead><tr><th></th>${cols.map(c => `<th class="num">${esc(c.texto)}</th>`).join('')}</tr></thead>
    <tbody>${r.cambios.map(f => `<tr class="${f.esTotal ? 'total' : ''}"><td>${esc(f.texto)}</td>${cols.map(c => `<td class="num">${f.importes[c.id] ? fmt(f.importes[c.id]) : ''}</td>`).join('')}</tr>`).join('')}</tbody>
    </table></div><p class="ayuda">Patrimonio neto según el balance: ${fmt(r.patrimonioNetoBalance)}.</p>`;
}
function pintaEfe(r) {
  cabeceraAnual('Estado de flujos de efectivo', r);
  $('#baHead').innerHTML = `<tr><th>Concepto</th><th class="num">${esc(r.ejercicio)}</th></tr>`;
  $('#baBody').innerHTML = r.lineas.map(filaLinea).join('');
}
function pintaMemoria(r) {
  cabeceraAnual('Memoria (borrador)', { ...r, canal: null });
  $('#baExtra').innerHTML = `<div class="memoria"><p><strong>${esc(r.empresa)}</strong> · NIF ${esc(r.nif)} · modelo ${r.formato === 'NORMAL' ? 'normal' : 'PYMES/abreviado'}</p>`
    + r.secciones.map(sec => `<h3>${esc(sec.numero)}. ${esc(sec.titulo)}</h3>${sec.parrafos.map(p => `<p>${esc(p)}</p>`).join('')}`
      + sec.tablas.map(t => `<table class="balance"><thead><tr>${t.cabecera.map((c, i) => `<th class="${i ? 'num' : ''}">${esc(c)}</th>`).join('')}</tr></thead>
        <tbody>${t.filas.map(f => `<tr>${f.map((v, i) => `<td class="${i ? 'num' : ''}">${esc(v)}</td>`).join('')}</tr>`).join('')}</tbody></table>`).join('')).join('')
    + '</div>';
}

$('#baBody').addEventListener('click', e => {
  const tr = e.target.closest('tr[data-abre]'); if (!tr) return;
  const abierta = tr.classList.toggle('abierta');
  $$(`#baBody tr[data-de="${CSS.escape(tr.dataset.abre)}"]`).forEach(d => d.hidden = !abierta);
});

// ------------------------------------------------------------ analítica
const ANA = { dims: [], informe: null, sin: [] };
const elemsAna = () => ANA.dims.flatMap(d => d.elementos.map(e => ({ ...e, dim: d.codigo })));

async function cargaDimensiones() {
  ANA.dims = permitido('ANALITICA') || permitido('PRESUPUESTOS') ? await api(urlE('/analitica/dimensiones')) : [];
  const hay = elemsAna().length > 0;
  $('#thAnalitica').hidden = !hay; $('#ayudaAnalitica').hidden = !hay;
  $('#pieLineas').colSpan = hay ? 6 : 5;
}

// «T1», «T1:60 T2:40», «CC/T1:50»: sin porcentaje, lo que falte hasta 100 en su dimensión a partes iguales
function parseAnalitica(texto) {
  const t = String(texto || '').trim();
  if (!t) return null;
  const els = elemsAna().filter(e => e.activo), filas = [];
  for (const tok of t.split(/[\s,;]+/).filter(Boolean)) {
    const [ref, pct] = tok.split(':');
    const [a, b] = ref.toUpperCase().split('/');
    const c = b ? els.filter(e => e.dim === a && e.codigo === b) : els.filter(e => e.codigo === a);
    if (!c.length) throw new Error(`El elemento analítico «${ref}» no existe o está de baja.`);
    if (c.length > 1) throw new Error(`«${ref}» está en varias dimensiones: escribe DIMENSIÓN/${a}.`);
    filas.push({ elementoId: c[0].id, dim: c[0].dimensionId, porcentaje: pct === undefined ? null : num(pct) });
  }
  for (const dim of new Set(filas.map(f => f.dim))) {
    const g = filas.filter(f => f.dim === dim), sin = g.filter(f => f.porcentaje === null);
    if (!sin.length) continue;
    const resto = 100 - g.reduce((s, f) => s + (f.porcentaje || 0), 0);
    const cuota = Math.round(resto / sin.length * 10000) / 10000;
    sin.forEach((f, i) => f.porcentaje = i === sin.length - 1 ? Math.round((resto - cuota * (sin.length - 1)) * 10000) / 10000 : cuota);
  }
  return filas.map(f => ({ elementoId: f.elementoId, porcentaje: f.porcentaje }));
}
function textoAnalitica(imps) {
  const els = elemsAna();
  return (imps || []).map(i => {
    const repetido = els.filter(e => e.codigo === i.elemento).length > 1;
    return (repetido ? i.dimension + '/' : '') + i.elemento + (+i.porcentaje === 100 ? '' : ':' + String(+i.porcentaje).replace('.', ','));
  }).join(' ');
}

async function cargaAnalitica() {
  await cargaDimensiones();
  const dims = ANA.dims.map(d => `<option value="${d.id}">${esc(d.codigo)} · ${esc(d.nombre)}</option>`).join('');
  const sel = $('#anDim').value;
  $('#anDim').innerHTML = dims || '<option value="">(no hay dimensiones)</option>';
  if (sel && ANA.dims.some(d => String(d.id) === sel)) $('#anDim').value = sel;
  $('#anNeDim').innerHTML = dims;
  if (!$('#anCanal').options.length) $('#anCanal').innerHTML = baseCanales().map(c => `<option value="${c.id}">Canal ${esc(c.codigo)}</option>`).join('');
  if (!$('#anDesde').value) { $('#anDesde').value = S.ejercicio.fechaInicio; $('#anHasta').value = S.ejercicio.fechaFin; }
  pintaPadres(); pintaDimensiones();
  await cargaRepartos();
  if (ANA.dims.length) await verInforme();
}

function pintaPadres() {
  const d = ANA.dims.find(x => String(x.id) === $('#anNeDim').value);
  $('#anNePadre').innerHTML = '<option value="">(ninguno)</option>' +
    (d ? d.elementos.map(e => `<option value="${e.id}">${esc(e.codigo)} · ${esc(e.nombre)}</option>`).join('') : '');
}
$('#anNeDim').onchange = pintaPadres;

function pintaDimensiones() {
  const escribe = permitido('ANALITICA:ESCRITURA');
  if (!ANA.dims.length) { $('#anDims').innerHTML = '<tr><td colspan="4" class="vacio">Todavía no hay dimensiones.</td></tr>'; return; }
  const filas = [];
  for (const d of ANA.dims) {
    filas.push(`<tr class="asiento-cab"><td>${esc(d.codigo)}</td><td>${esc(d.nombre)}</td><td></td>
      <td>${escribe ? `<div class="fila-acciones"><button class="btn" data-dimdel="${d.id}" type="button">Borrar</button></div>` : ''}</td></tr>`);
    const hijos = p => d.elementos.filter(e => (e.padreId || null) === p);
    const recorre = (p, n) => hijos(p).forEach(e => {
      filas.push(`<tr><td style="padding-left:${12 + n * 18}px">${esc(e.codigo)}</td><td>${esc(e.nombre)}</td>
        <td>${e.activo ? 'Activo' : '<span class="estado-cerrado">De baja</span>'}</td>
        <td>${escribe ? `<div class="fila-acciones"><button class="btn" data-elact="${e.id}" type="button">${e.activo ? 'Dar de baja' : 'Reactivar'}</button>
          <button class="btn" data-eldel="${e.id}" type="button">Borrar</button></div>` : ''}</td></tr>`);
      recorre(e.id, n + 1);
    });
    recorre(null, 0);
  }
  $('#anDims').innerHTML = filas.join('');
}

$('#anDims').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  try {
    if (b.dataset.dimdel) {
      if (!confirm('¿Borrar la dimensión y sus elementos?')) return;
      await api(urlE('/analitica/dimensiones/' + b.dataset.dimdel), { method: 'DELETE' });
    } else if (b.dataset.eldel) {
      await api(urlE('/analitica/elementos/' + b.dataset.eldel), { method: 'DELETE' });
    } else if (b.dataset.elact) {
      const el = elemsAna().find(x => String(x.id) === b.dataset.elact);
      await put(urlE('/analitica/elementos/' + el.id), { codigo: el.codigo, nombre: el.nombre, padreId: el.padreId, activo: !el.activo });
    } else return;
    msg($('#anDimMsg'), 'Hecho.', 'ayuda ok-texto');
    await cargaAnalitica();
  } catch (err) { msg($('#anDimMsg'), err.message, 'ayuda err-texto'); }
});
$('#anNdCrear').onclick = async () => {
  try {
    await post(urlE('/analitica/dimensiones'), { codigo: $('#anNdCodigo').value, nombre: $('#anNdNombre').value });
    $('#anNdCodigo').value = $('#anNdNombre').value = '';
    msg($('#anDimMsg'), 'Dimensión creada.', 'ayuda ok-texto'); await cargaAnalitica();
  } catch (err) { msg($('#anDimMsg'), err.message, 'ayuda err-texto'); }
};
$('#anNeCrear').onclick = async () => {
  try {
    const dim = $('#anNeDim').value;
    await post(urlE(`/analitica/dimensiones/${dim}/elementos`),
      { codigo: $('#anNeCodigo').value, nombre: $('#anNeNombre').value, padreId: $('#anNePadre').value ? +$('#anNePadre').value : null });
    $('#anNeCodigo').value = $('#anNeNombre').value = '';
    msg($('#anDimMsg'), 'Elemento creado.', 'ayuda ok-texto'); await cargaAnalitica(); $('#anNeDim').value = dim; pintaPadres();
  } catch (err) { msg($('#anDimMsg'), err.message, 'ayuda err-texto'); }
};

async function cargaRepartos() {
  const rs = await api(urlE('/analitica/repartos'));
  const escribe = permitido('ANALITICA:ESCRITURA');
  $('#anRepartos').innerHTML = rs.length ? rs.map(r => `<tr><td>${esc(r.cuenta)}</td><td class="nombre">${esc(r.cuentaNombre)}</td>
    <td>${r.requiereAnalitica ? 'Sí' : ''}</td><td>${esc(textoAnalitica(r.reparto)) || '—'}</td>
    <td>${escribe ? `<div class="fila-acciones"><button class="btn" data-rpedit="${esc(r.cuenta)}" type="button">Editar</button>
      <button class="btn" data-rpdel="${esc(r.cuenta)}" type="button">Quitar</button></div>` : ''}</td></tr>`).join('')
    : '<tr><td colspan="5" class="vacio">Ninguna cuenta tiene reparto por defecto.</td></tr>';
  ANA.repartos = rs;
}
$('#anRepartos').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  if (b.dataset.rpedit) {
    const r = ANA.repartos.find(x => x.cuenta === b.dataset.rpedit);
    $('#anRpCuenta').value = r.cuenta; $('#anRpReparto').value = textoAnalitica(r.reparto); $('#anRpExige').value = String(r.requiereAnalitica);
    $('#anRpCuenta').focus();
  } else if (b.dataset.rpdel) {
    try { await put(urlE('/analitica/repartos'), { cuenta: b.dataset.rpdel, requiereAnalitica: false, reparto: [] }); await cargaRepartos(); }
    catch (err) { msg($('#anRpMsg'), err.message, 'ayuda err-texto'); }
  }
});
$('#anRpGuardar').onclick = async () => {
  try {
    await put(urlE('/analitica/repartos'), { cuenta: $('#anRpCuenta').value.trim(), requiereAnalitica: $('#anRpExige').value === 'true',
      reparto: parseAnalitica($('#anRpReparto').value) || [] });
    msg($('#anRpMsg'), 'Reparto guardado.', 'ayuda ok-texto');
    $('#anRpCuenta').value = $('#anRpReparto').value = ''; $('#anRpExige').value = 'false';
    await cargaRepartos();
  } catch (err) { msg($('#anRpMsg'), err.message, 'ayuda err-texto'); }
};

async function verInforme() {
  if (!$('#anDim').value) return;
  try {
    const q = new URLSearchParams({ dimensionId: $('#anDim').value, canalId: $('#anCanal').value, desde: $('#anDesde').value,
      hasta: $('#anHasta').value, detalle: $('#anDetalle').value });
    const r = ANA.informe = await api(urlX('/analitica/informe?' + q));
    $('#anSub').textContent = `${r.dimension} · canal ${r.canal} · del ${fechaES(r.desde)} al ${fechaES(r.hasta)}`;
    $('#anAvisos').innerHTML = r.avisos.map(a => `<li>${esc(a)}</li>`).join('');
    const fila = (f, cls) => `<tr class="${cls}"><td style="padding-left:${6 + f.nivel * 18}px">${esc(f.codigo)} ${esc(f.nombre)}</td>
      <td class="num">${fmt(f.ingresos)}</td><td class="num">${fmt(f.gastos)}</td><td class="num">${fmt(f.resultado)}</td><td class="num">${f.otros ? fmt(f.otros) : ''}</td></tr>`;
    $('#anBody').innerHTML = r.filas.map(f => fila(f, f.esCuenta ? 'detalle' : 'partida')).join('')
      + fila(r.sinAsignar, 'partida') + fila(r.total, 'total');
    msg($('#anMsg'), '', 'ayuda');
  } catch (err) { msg($('#anMsg'), err.message, 'ayuda err-texto'); }
}
$('#anVer').onclick = verInforme;
$('#anImprimir').onclick = () => window.print();

$('#anSinVer').onclick = async () => {
  msg($('#anSinMsg'), '', 'ayuda');
  await buscaSinImputar();
};
async function buscaSinImputar() {
  try {
    const q = new URLSearchParams({ dimensionId: $('#anDim').value, desde: $('#anDesde').value, hasta: $('#anHasta').value });
    ANA.sin = await api(urlX('/analitica/sin-imputar?' + q));
    const escribe = permitido('ANALITICA:ESCRITURA') || permitido('CONTABILIDAD:ESCRITURA');
    $('#anSinBody').innerHTML = ANA.sin.length ? ANA.sin.map(a => `<tr data-ap="${a.apunteId}"><td>${fechaES(a.fecha)}</td><td>${a.numero}</td>
      <td>${esc(a.cuenta)}</td><td class="nombre">${esc(a.concepto)}</td><td class="num">${fmt(a.debe || -a.haber)}</td>
      <td>${esc(textoAnalitica(a.imputaciones)) || '—'}</td>
      <td>${escribe ? `<input data-rep value="${esc(textoAnalitica(a.imputaciones))}" aria-label="Nuevo reparto">` : ''}</td>
      <td>${escribe ? '<button class="btn" data-imputa type="button">Guardar</button>' : ''}</td></tr>`).join('')
      : '<tr><td colspan="8" class="vacio">Todo está imputado en esta dimensión.</td></tr>';
    if (ANA.sin.length === 500) msg($('#anSinMsg'), 'Se muestran los 500 primeros.', 'ayuda');
  } catch (err) { msg($('#anSinMsg'), err.message, 'ayuda err-texto'); }
}
$('#anSinBody').addEventListener('click', async e => {
  if (!e.target.matches('[data-imputa]')) return;
  const tr = e.target.closest('tr');
  try {
    await put(urlE('/analitica/apuntes/' + tr.dataset.ap), parseAnalitica(tr.querySelector('[data-rep]').value) || []);
    msg($('#anSinMsg'), 'Analítica del apunte guardada.', 'ayuda ok-texto');
    await buscaSinImputar(); verInforme();
  } catch (err) { msg($('#anSinMsg'), err.message, 'ayuda err-texto'); }
});

// ------------------------------------------------------------ presupuestos
const PR = { lista: [], actual: null };

async function cargaPresupuestos() {
  await cargaDimensiones();
  $('#ppNCanal').innerHTML = baseCanales().map(c => `<option value="${c.id}">Canal ${esc(c.codigo)}</option>`).join('');
  $('#ppGEj').innerHTML = (S.listaEjercicios || []).filter(x => x.id !== S.ejercicio.id)
    .map(x => `<option value="${x.id}">${esc(x.codigo)}</option>`).join('');
  $('#ppGDim').innerHTML = '<option value="">(sin analítica)</option>' + ANA.dims.map(d => `<option value="${d.id}">${esc(d.codigo)}</option>`).join('');
  PR.lista = await api(urlX('/presupuestos'));
  $('#ppLista').innerHTML = PR.lista.length ? PR.lista.map(p => `<tr><td>${esc(p.codigo)}</td><td>${p.version}</td><td>${esc(p.nombre)}</td>
    <td>${esc(p.canal || '')}</td><td>${esc(p.estado)}</td><td class="num">${fmt(p.ingresos)}</td><td class="num">${fmt(p.gastos)}</td>
    <td class="num">${fmt(p.ingresos - p.gastos)}</td><td><button class="btn" data-pr="${p.id}" type="button">Abrir</button></td></tr>`).join('')
    : '<tr><td colspan="9" class="vacio">No hay presupuestos en este ejercicio.</td></tr>';
  if (PR.actual && !PR.lista.some(p => p.id === PR.actual.id)) { PR.actual = null; $('#ppPanel').hidden = true; }
}
$('#ppLista').addEventListener('click', e => { const b = e.target.closest('[data-pr]'); if (b) abrePresupuesto(+b.dataset.pr).catch(err => alert(err.message)); });

$('#ppNCrear').onclick = async () => {
  try {
    const r = await post(urlX('/presupuestos'), { codigo: $('#ppNCodigo').value, nombre: $('#ppNNombre').value, canalId: +$('#ppNCanal').value });
    $('#ppNCodigo').value = $('#ppNNombre').value = '';
    msg($('#ppMsg'), 'Presupuesto creado.', 'ayuda ok-texto');
    await cargaPresupuestos(); await abrePresupuesto(r.id);
  } catch (err) { msg($('#ppMsg'), err.message, 'ayuda err-texto'); }
};

async function abrePresupuesto(id) {
  pintaPresupuesto(await api(urlE('/presupuestos/' + id)));
  $('#ppComp').innerHTML = ''; $('#ppTotales').innerHTML = ''; $('#ppAvisos').innerHTML = '';
  msg($('#ppMsg2'), '', 'ayuda');
  await comparaPresupuesto();
}

function pintaPresupuesto(p) {
  PR.actual = p;
  const borrador = p.estado === 'BORRADOR', escribe = permitido('PRESUPUESTOS:ESCRITURA') && borrador;
  $('#ppPanel').hidden = false;
  $('#ppTitulo').textContent = `${p.codigo} v${p.version} · ${p.nombre} · canal ${p.canal || ''} · ${p.estado.toLowerCase()}`;
  $('#ppGuardar').hidden = $('#ppAnadir').hidden = $('#ppEliminar').hidden = $('#ppAprobar').hidden = !borrador;
  $('#ppABorrador').hidden = $('#ppCerrar').hidden = p.estado !== 'APROBADO';
  $('#ppHead').innerHTML = `<tr><th>Cuenta</th><th>Elemento</th>${p.periodos.map(x => `<th class="num">${esc(x)}</th>`).join('')}<th class="num">Total</th><th></th></tr>`;
  $('#ppHasta').innerHTML = p.periodos.map((x, i) => `<option value="${i + 1}" ${i === p.periodos.length - 1 ? 'selected' : ''}>${esc(x)}</option>`).join('');
  PR.filas = p.lineas.map(l => ({ cuenta: l.cuentaPatron, nombre: l.cuentaNombre, elemento: l.elemento ? l.elemento.split('/')[1] : '',
    elementoTxt: l.elemento || '', importes: l.importes.map(String) }));
  pintaFilasPresupuesto(escribe);
}

function pintaFilasPresupuesto(escribe = PR.actual.estado === 'BORRADOR' && permitido('PRESUPUESTOS:ESCRITURA')) {
  const n = PR.actual.periodos.length;
  const ro = escribe ? '' : 'readonly';
  $('#ppLineas').innerHTML = PR.filas.map((f, i) => `<tr data-i="${i}">
    <td class="cta"><input data-f="cuenta" value="${esc(f.cuenta)}" ${ro} aria-label="Cuenta" title="${esc(f.nombre || '')}"></td>
    <td class="cta"><input data-f="elemento" value="${esc(f.elementoTxt)}" ${ro} aria-label="Elemento"></td>
    ${f.importes.map((x, j) => `<td><input class="num" data-m="${j}" inputmode="decimal" value="${num(x) ? fmt(num(x)) : ''}" ${ro} aria-label="Mes ${j + 1}"></td>`).join('')}
    <td><input class="num" data-f="total" inputmode="decimal" value="${fmt(f.importes.reduce((s, x) => s + num(x), 0))}" ${ro} aria-label="Total"></td>
    <td>${escribe ? `<button class="x" type="button" data-del="${i}" aria-label="Quitar línea">×</button>` : ''}</td></tr>`).join('')
    || `<tr><td colspan="${n + 4}" class="vacio">Sin líneas.</td></tr>`;
  pintaPiePresupuesto();
}
function pintaPiePresupuesto() {
  const n = PR.actual.periodos.length, tot = Array(n).fill(0);
  const signo = c => c.startsWith('7') ? 1 : c.startsWith('6') ? -1 : 0;
  PR.filas.forEach(f => f.importes.forEach((x, j) => tot[j] += signo(f.cuenta) * num(x)));
  $('#ppPie').innerHTML = `<tr><td colspan="2">Resultado (7 − 6)</td>${tot.map(x => `<td class="num">${fmt(x)}</td>`).join('')}
    <td class="num">${fmt(tot.reduce((s, x) => s + x, 0))}</td><td></td></tr>`;
}
const repartoLineal = (total, n) => {
  const c = Math.round(total / n * 100) / 100, r = Array(n).fill(c);
  r[n - 1] = Math.round((total - c * (n - 1)) * 100) / 100; return r.map(String);
};
$('#ppLineas').addEventListener('change', e => {
  const tr = e.target.closest('tr'); if (!tr || !PR.filas[+tr.dataset.i]) return;
  const f = PR.filas[+tr.dataset.i];
  if (e.target.dataset.m !== undefined) { f.importes[+e.target.dataset.m] = String(num(e.target.value)); pintaFilasPresupuesto(); }
  else if (e.target.dataset.f === 'total') { f.importes = repartoLineal(num(e.target.value), PR.actual.periodos.length); pintaFilasPresupuesto(); }
  else if (e.target.dataset.f === 'cuenta') { f.cuenta = e.target.value.trim(); pintaPiePresupuesto(); }
  else if (e.target.dataset.f === 'elemento') f.elementoTxt = e.target.value.trim();
});
$('#ppLineas').addEventListener('click', e => {
  const d = e.target.dataset.del; if (d === undefined) return;
  PR.filas.splice(+d, 1); pintaFilasPresupuesto();
});
$('#ppAnadir').onclick = () => {
  PR.filas.push({ cuenta: '', elementoTxt: '', importes: Array(PR.actual.periodos.length).fill('0') });
  pintaFilasPresupuesto();
  $$('#ppLineas tr').at(-1).querySelector('input').focus();
};
$('#ppGuardar').onclick = async () => {
  try {
    const lineas = PR.filas.filter(f => f.cuenta).map(f => {
      const a = parseAnalitica(f.elementoTxt);
      if (a && a.length > 1) throw new Error(`La línea ${f.cuenta} lleva más de un elemento: crea una línea por elemento.`);
      return { cuentaPatron: f.cuenta, elementoId: a ? a[0].elementoId : null, importes: f.importes.map(num) };
    });
    pintaPresupuesto(await put(urlE(`/presupuestos/${PR.actual.id}/lineas`), { lineas }));
    msg($('#ppMsg2'), 'Líneas guardadas.', 'ayuda ok-texto');
    await cargaPresupuestos(); await comparaPresupuesto();
  } catch (err) { msg($('#ppMsg2'), err.message, 'ayuda err-texto'); }
};
$('#ppGenerar').onclick = async () => {
  if (PR.filas.length && !confirm('Se sustituyen las líneas actuales. ¿Seguir?')) return;
  try {
    pintaPresupuesto(await post(urlE(`/presupuestos/${PR.actual.id}/desde-real`), { ejercicioOrigenId: +$('#ppGEj').value,
      incrementoPorcentaje: num($('#ppGInc').value), digitos: +$('#ppGDig').value, dimensionId: $('#ppGDim').value ? +$('#ppGDim').value : null }));
    msg($('#ppMsg2'), 'Líneas generadas: revísalas y guarda si cambias algo.', 'ayuda ok-texto');
    await cargaPresupuestos(); await comparaPresupuesto();
  } catch (err) { msg($('#ppMsg2'), err.message, 'ayuda err-texto'); }
};
async function estadoPresupuesto(estado) {
  try {
    await post(urlE(`/presupuestos/${PR.actual.id}/estado`), { estado });
    await cargaPresupuestos(); await abrePresupuesto(PR.actual.id);
  } catch (err) { msg($('#ppMsg2'), err.message, 'ayuda err-texto'); }
}
$('#ppAprobar').onclick = () => estadoPresupuesto('APROBADO');
$('#ppABorrador').onclick = () => estadoPresupuesto('BORRADOR');
$('#ppCerrar').onclick = () => estadoPresupuesto('CERRADO');
$('#ppVersion').onclick = async () => {
  try { const r = await post(urlE(`/presupuestos/${PR.actual.id}/nueva-version`)); await cargaPresupuestos(); await abrePresupuesto(r.id); }
  catch (err) { msg($('#ppMsg2'), err.message, 'ayuda err-texto'); }
};
$('#ppEliminar').onclick = async () => {
  if (!confirm('¿Eliminar este presupuesto?')) return;
  try { await api(urlE('/presupuestos/' + PR.actual.id), { method: 'DELETE' }); PR.actual = null; $('#ppPanel').hidden = true; await cargaPresupuestos(); }
  catch (err) { msg($('#ppMsg2'), err.message, 'ayuda err-texto'); }
};

async function comparaPresupuesto() {
  if (!PR.actual) return;
  const c = await api(urlE(`/presupuestos/${PR.actual.id}/comparativa?hastaPeriodo=${$('#ppHasta').value || ''}`));
  const caja = (t, v) => `<div><span>${t}</span><strong>${fmt(v)}</strong></div>`;
  $('#ppTotales').innerHTML = caja('Ingresos presupuestados', c.ingresosPresupuesto) + caja('Ingresos reales', c.ingresosReal)
    + caja('Gastos presupuestados', c.gastosPresupuesto) + caja('Gastos reales', c.gastosReal)
    + caja('Resultado presupuestado', c.resultadoPresupuesto) + caja('Resultado real', c.resultadoReal);
  $('#ppAvisos').innerHTML = c.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#ppComp').innerHTML = c.filas.map(f => `<tr><td>${esc(f.cuentaPatron)}</td><td class="nombre">${esc(f.cuentaNombre || '')}</td><td>${esc(f.elemento || '')}</td>
    <td class="num">${fmt(f.presupuesto)}</td><td class="num">${fmt(f.real)}</td>
    <td class="num ${f.desviacion ? (f.favorable ? 'fav' : 'desf') : ''}">${fmt(f.desviacion)}</td>
    <td class="num">${f.ejecucion === null ? '—' : fmt(f.ejecucion) + ' %'}</td></tr>`).join('')
    || '<tr><td colspan="7" class="vacio">Sin líneas.</td></tr>';
}
$('#ppComparar').onclick = () => comparaPresupuesto().catch(err => msg($('#ppMsg2'), err.message, 'ayuda err-texto'));

// ------------------------------------------------------------ cierre del ejercicio
async function cargaCierre() {
  const c = await api(urlX('/cierre'));
  S.ejercicio.estado = c.estado; aplicaEstadoEjercicio();
  $('#ciTitulo').textContent = `Cierre del ejercicio ${c.codigo}`;
  $('#ciEstado').textContent = c.estado === 'ABIERTO'
    ? (c.puedeCerrar ? `Listo para cerrar. La apertura irá al ejercicio ${c.siguiente.codigo}.` : 'Todavía no se puede cerrar:')
    : `Ejercicio ${c.estado.toLowerCase()}.`;
  $('#ciBloqueos').innerHTML = c.estado === 'ABIERTO' ? c.bloqueos.map(b => `<li>${esc(b)}</li>`).join('') : '';
  $('#ciAvisos').innerHTML = c.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#ciResultados').innerHTML = c.resultados.length
    ? c.resultados.map(r => `<tr><td>${esc(r.canal)}</td><td class="num">${fmt(r.ingresos)}</td><td class="num">${fmt(r.gastos)}</td>
        <td class="num ${r.resultado < 0 ? 'estado-mal' : ''}">${fmt(r.resultado)}</td></tr>`).join('')
    : '<tr><td colspan="4" class="vacio">Sin ingresos ni gastos en el ejercicio.</td></tr>';
  $('#ciCerrar').hidden = c.estado !== 'ABIERTO'; $('#ciCerrar').disabled = !c.puedeCerrar;
  $('#ciDeshacer').hidden = c.estado !== 'CERRADO';
  await cargaAplicacion();
}
// ------------------------------------------------------------ aplicación del resultado
const AR = { propuesta: null, lineas: [] };
const DESTINOS = [
  ['RESERVA_LEGAL', 'Reserva legal (112)'], ['RESERVA_VOLUNTARIA', 'Reservas voluntarias (113)'], ['REMANENTE', 'Remanente (120)'],
  ['COMPENSAR_PERDIDAS', 'Compensar pérdidas anteriores (121)'], ['DIVIDENDOS', 'Dividendos (557/526)'],
  ['RESULTADOS_NEGATIVOS', 'Resultados negativos (121)'], ['CUENTA', 'Otra cuenta del grupo 1']];
const MODOS = [['IMPORTE', 'Importe'], ['PORCENTAJE', '% del resultado'], ['RESTO', 'El resto']];
const opciones = (lista, sel) => lista.map(([v, t]) => `<option value="${v}" ${v === sel ? 'selected' : ''}>${esc(t)}</option>`).join('');

async function cargaAplicacion() {
  const p = AR.propuesta = await api(urlX('/aplicacion-resultado'));
  $('#arTitulo').textContent = p.resultadoDe ? `Aplicación del resultado de ${p.resultadoDe}` : 'Aplicación del resultado';
  $('#arCanales').innerHTML = p.canales.map(c => `<tr><td>${esc(c.canal)}</td><td class="num ${c.resultado < 0 ? 'estado-mal' : ''}">${fmt(c.resultado)}</td>
      <td class="num">${fmt(c.capital)}</td><td class="num">${fmt(c.reservaLegal)}</td><td class="num">${fmt(c.reservaLegalMinima)}</td>
      <td class="num">${fmt(c.perdidasAnteriores)}</td></tr>`).join('');
  $('#arAvisos').innerHTML = p.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#arHechas').innerHTML = p.aplicaciones.map(a => `<tr><td>nº ${a.numero}</td><td>${fechaES(a.fecha)}</td><td>${esc(a.concepto)}</td>
      <td class="fila-acciones">${permitido('CONTABILIDAD_DIRECTO') ? `<button class="btn" data-anular="${a.asientoId}" ${S.ejercicio.estado === 'ABIERTO' ? '' : 'disabled'}>Anular</button>` : ''}</td></tr>`).join('')
    || '<tr><td colspan="4" class="vacio">Ninguna.</td></tr>';

  const pendientes = p.canales.filter(c => c.resultado !== 0);
  $('#arCanal').innerHTML = (pendientes.length > 1 ? '<option value="">Todos los canales con resultado</option>' : '')
    + pendientes.map(c => `<option value="${c.canalId}">Canal ${esc(c.canal)}</option>`).join('');
  $('#arForm').hidden = !permitido('CONTABILIDAD_DIRECTO') || !pendientes.length;
  if (!$('#arFecha').value || $('#arFecha').value < S.ejercicio.fechaInicio || $('#arFecha').value > S.ejercicio.fechaFin) {
    $('#arFecha').min = S.ejercicio.fechaInicio; $('#arFecha').max = S.ejercicio.fechaFin;
    const h = hoy(); $('#arFecha').value = h < S.ejercicio.fechaInicio ? S.ejercicio.fechaInicio : h > S.ejercicio.fechaFin ? S.ejercicio.fechaFin : h;
  }
  propuestaLineas();
}

// Propuesta por defecto: con beneficio, la reserva legal mínima y el resto a voluntarias; con pérdida, a resultados negativos
function propuestaLineas() {
  const canal = AR.propuesta.canales.find(c => String(c.canalId) === $('#arCanal').value) || AR.propuesta.canales.find(c => c.resultado !== 0);
  if (!canal) { AR.lineas = []; pintaLineasAR(); return; }
  AR.lineas = canal.resultado > 0
    ? [...(canal.reservaLegalMinima > 0 && $('#arCanal').value ? [{ destino: 'RESERVA_LEGAL', modo: 'IMPORTE', valor: canal.reservaLegalMinima }]
           : canal.reservaLegalMinima > 0 ? [{ destino: 'RESERVA_LEGAL', modo: 'PORCENTAJE', valor: 10 }] : []),
       { destino: 'RESERVA_VOLUNTARIA', modo: 'RESTO', valor: '' }]
    : [{ destino: 'RESULTADOS_NEGATIVOS', modo: 'RESTO', valor: '' }];
  $('#arPreviaPanel').hidden = true;
  pintaLineasAR();
}
$('#arCanal').onchange = propuestaLineas;

function pintaLineasAR() {
  $('#arLineas').innerHTML = AR.lineas.map((l, i) => `<tr data-i="${i}">
    <td><select data-k="destino" aria-label="Destino">${opciones(DESTINOS, l.destino)}</select></td>
    <td><select data-k="modo" aria-label="Cómo">${opciones(MODOS, l.modo)}</select></td>
    <td class="num"><input data-k="valor" inputmode="decimal" value="${l.modo === 'RESTO' ? '' : esc(l.valor)}" ${l.modo === 'RESTO' ? 'disabled' : ''} aria-label="Valor"></td>
    <td><input data-k="cuenta" list="dl-cuentas" value="${esc(l.cuenta || '')}" ${l.destino === 'CUENTA' ? '' : 'disabled'} aria-label="Cuenta"></td>
    <td><button class="btn" data-quitar="${i}" type="button" aria-label="Quitar">✕</button></td></tr>`).join('');
}
$('#arLineas').addEventListener('change', e => {
  const tr = e.target.closest('tr[data-i]'); if (!tr) return;
  const l = AR.lineas[+tr.dataset.i]; l[e.target.dataset.k] = e.target.value;
  if (['modo', 'destino'].includes(e.target.dataset.k)) pintaLineasAR();
});
$('#arLineas').addEventListener('click', e => {
  const b = e.target.closest('button[data-quitar]'); if (!b) return;
  AR.lineas.splice(+b.dataset.quitar, 1); pintaLineasAR();
});
$('#arAnadir').onclick = () => { AR.lineas.push({ destino: 'REMANENTE', modo: 'IMPORTE', valor: '' }); pintaLineasAR(); };

function cmdAplicacion() {
  return {
    fecha: $('#arFecha').value, canalId: $('#arCanal').value ? +$('#arCanal').value : null, acuerdo: $('#arAcuerdo').value || null,
    lineas: AR.lineas.map(l => {
      const cuenta = l.destino === 'CUENTA' ? S.cuentas.find(c => c.codigo === (l.cuenta || '').trim()) : null;
      if (l.destino === 'CUENTA' && !cuenta) throw new Error(`La cuenta «${l.cuenta || ''}» no existe.`);
      return { destino: l.destino, modo: l.modo, valor: l.modo === 'RESTO' ? 0 : num(l.valor), cuentaId: cuenta ? cuenta.id : null };
    })
  };
}

$('#arPrevia').onclick = async () => {
  try {
    const v = await post(urlX('/aplicacion-resultado/previsualizar'), cmdAplicacion());
    const canal = cid => (S.canales.find(c => c.id === cid) || {}).codigo || cid;
    $('#arPreviaAvisos').innerHTML = v.avisos.map(a => `<li>${esc(a)}</li>`).join('');
    $('#arPreviaApuntes').innerHTML = v.asiento.map(a => `<tr><td>${esc(a.cuenta)}</td><td>${esc(a.cuentaNombre)}</td><td>${esc(canal(a.canalId))}</td>
      <td class="num">${a.debe ? fmt(a.debe) : ''}</td><td class="num">${a.haber ? fmt(a.haber) : ''}</td></tr>`).join('');
    $('#arPreviaPanel').hidden = false; msg($('#arMsg'), '', 'ayuda');
  } catch (e) { $('#arPreviaPanel').hidden = true; msg($('#arMsg'), e.message, 'ayuda err-texto'); }
};
$('#arGuardar').onclick = async () => {
  try {
    const r = await post(urlX('/aplicacion-resultado'), cmdAplicacion());
    await cargaAplicacion();
    msg($('#arMsg'), `Resultado aplicado: asiento nº ${r.asientoNumero}.` + (r.avisos.length ? ' Revisa los avisos: ' + r.avisos.join(' ') : ''), 'ayuda ok-texto');
  } catch (e) { msg($('#arMsg'), e.message, 'ayuda err-texto'); }
};
$('#arHechas').addEventListener('click', async e => {
  const b = e.target.closest('button[data-anular]'); if (!b || !confirm('¿Anular esta aplicación del resultado?')) return;
  try { await api(urlX('/aplicacion-resultado/' + b.dataset.anular), { method: 'DELETE' }); await cargaAplicacion(); msg($('#arMsg'), 'Aplicación anulada.', 'ayuda ok-texto'); }
  catch (err) { msg($('#arMsg'), err.message, 'ayuda err-texto'); }
});

$('#ciCerrar').onclick = async () => {
  if (!confirm(`Se generarán la regularización, el cierre y la apertura, y el ejercicio ${S.ejercicio.codigo} quedará cerrado. ¿Continuar?`)) return;
  try {
    const r = await post(urlX('/cierre'));
    const tipos = { REGULARIZACION: 'regularización', CIERRE: 'cierre', APERTURA: 'apertura' };
    await cargaCierre();
    msg($('#ciMsg'), 'Ejercicio cerrado. ' + r.asientos.map(a => `Asiento de ${tipos[a.tipo]}: nº ${a.numero}`).join('; ') + '.', 'ayuda ok-texto');
  } catch (e) { msg($('#ciMsg'), e.message, 'ayuda err-texto'); }
};
$('#ciDeshacer').onclick = async () => {
  if (!confirm('Se anularán los asientos de regularización, cierre y apertura, y el ejercicio volverá a estar abierto. ¿Continuar?')) return;
  try { await post(urlX('/cierre/deshacer')); await cargaCierre(); msg($('#ciMsg'), 'Cierre deshecho: el ejercicio vuelve a estar abierto.', 'ayuda ok-texto'); }
  catch (e) { msg($('#ciMsg'), e.message, 'ayuda err-texto'); }
};

// ------------------------------------------------------------ pendientes de revisión
async function cargaPendientes() {
  const filas = await api(urlX('/pendientes'));
  $('#pendBody').innerHTML = filas.length ? filas.map(p => `<tr>
      <td>${p.tipo === 'ASIENTO' ? 'Asiento' : 'Factura'}</td><td>${esc(p.referencia)}</td><td>${fechaES(p.fecha)}</td><td>${esc(p.descripcion)}</td>
      <td class="num">${fmt(p.importe)}</td><td>${esc(p.creadoPor)}</td>
      <td class="fila-acciones"><button class="btn primario" data-ap="${p.tipo}:${p.id}" ${S.ejercicio.estado !== 'ABIERTO' ? 'disabled' : ''}>Aprobar</button>
          <button class="btn" data-re="${p.tipo}:${p.id}">Rechazar</button></td></tr>`).join('')
    : '<tr><td colspan="7" class="vacio">No hay borradores pendientes.</td></tr>';
}

$('#pendBody').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  const aprobar = b.dataset.ap !== undefined, [tipo, id] = (b.dataset.ap ?? b.dataset.re).split(':');
  const ruta = (tipo === 'ASIENTO' ? '/asientos/' : '/facturas/') + id + (aprobar ? '/aprobar' : '/rechazar');
  let motivo = null;
  if (!aprobar) { motivo = prompt('Motivo del rechazo (opcional):'); if (motivo === null) return; }
  try {
    await post(urlX(ruta), aprobar ? {} : { motivo });
    msg($('#pendMsg'), aprobar ? 'Borrador aprobado y contabilizado.' : 'Borrador rechazado.', 'ayuda ok-texto');
    await cargaPendientes();
  } catch (err) { msg($('#pendMsg'), err.message, 'ayuda err-texto'); }
});

// ============================================================ administración
async function entrarAdmin() {
  S.empresa = null; S.ejercicio = null;
  vista('admin');
  abreTabAdmin('usuarios');
}

function abreTabAdmin(t) {
  $$('#navAdmin button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.tab === t)));
  $('#adm-usuarios').hidden = t !== 'usuarios'; $('#adm-empresas').hidden = t !== 'empresas'; $('#adm-catalogos').hidden = t !== 'catalogos';
  ({ usuarios: cargaUsuarios, empresas: cargaEmpresasAdmin, catalogos: cargaCatalogos })[t]().catch(e => alert(e.message));
}
$('#navAdmin').addEventListener('click', e => { const b = e.target.closest('button[data-tab]'); if (b) abreTabAdmin(b.dataset.tab); });

// ---- catálogos comunes y formas de pago de cada empresa
// Cada catálogo: columnas de la tabla, campos del formulario (k, etiqueta, tipo; solo: no editable al modificar) y cómo se guarda.
const pctVigente = t => { const v = t.vigencias.find(x => !x.fechaHasta) || t.vigencias[t.vigencias.length - 1]; return v ? v : null; };
// fechaHasta es el día en que empieza la siguiente: se muestra el anterior
const diaAntes = d => { const x = new Date(d + 'T00:00:00'); x.setDate(x.getDate() - 1); return x.toLocaleDateString('es-ES'); };
const vigTxt = t => t.vigencias.map(v => `${v.porcentaje} %${v.recargo ? ' (RE ' + v.recargo + ' %)' : ''} desde ${fechaES(v.fechaDesde)}${v.fechaHasta ? ' hasta ' + diaAntes(v.fechaHasta) : ''}`).join('<br>');
const siNo = b => b ? 'sí' : 'no';
const CAT = {
  iva: {
    url: '/api/admin/catalogos/tipos-iva', ayuda: 'Los tipos no se borran: se dan de baja o se abre una vigencia nueva con el porcentaje que entra en vigor.',
    cols: ['Impuesto', 'Código', 'Nombre', 'Vigencias', 'Exento', 'Activo'],
    fila: t => [esc(t.impuesto), esc(t.codigo), esc(t.nombre), vigTxt(t), siNo(t.esExento), siNo(t.activo)],
    campos: [['impuesto', 'Impuesto', ['IVA', 'IGIC', 'IPSI'], 1], ['codigo', 'Código', 'text', 1], ['nombre', 'Nombre', 'text'],
      ['porcentaje', 'Porcentaje inicial', 'number', 1], ['recargo', 'Recargo de equivalencia', 'number', 1], ['fechaDesde', 'Se aplica desde', 'date', 1],
      ['esExento', 'Exento', 'check'], ['activo', 'Activo', 'check']],
    guarda: (o, id) => id ? put(`/api/admin/catalogos/tipos-iva/${id}`, o) : post('/api/admin/catalogos/tipos-iva', o), vigencias: 'tipos-iva', recargo: true,
  },
  ret: {
    url: '/api/admin/catalogos/tipos-retencion', ayuda: 'El modelo de ingreso (111, 115…) y la clave del resumen anual (190, 180) deciden dónde se declara cada retención.',
    cols: ['Código', 'Nombre', 'Naturaleza', 'Modelo', 'Clave', 'Vigencias', 'Activo'],
    fila: t => [esc(t.codigo), esc(t.nombre), esc(t.naturaleza), esc(t.modeloIngreso || ''), esc(t.claveResumen || ''), vigTxt(t), siNo(t.activo)],
    campos: [['codigo', 'Código', 'text', 1], ['nombre', 'Nombre', 'text'],
      ['naturaleza', 'Naturaleza', ['PROFESIONAL', 'ARRENDAMIENTO', 'CAPITAL', 'AGRICOLA', 'TRABAJO', 'OTRA']],
      ['modeloIngreso', 'Modelo de ingreso', ['', '111', '115', '123', '124', '126', '216']], ['claveResumen', 'Clave del resumen anual', 'text'],
      ['porcentaje', 'Porcentaje inicial', 'number', 1], ['fechaDesde', 'Se aplica desde', 'date', 1], ['activo', 'Activo', 'check']],
    guarda: (o, id) => id ? put(`/api/admin/catalogos/tipos-retencion/${id}`, o) : post('/api/admin/catalogos/tipos-retencion', o), vigencias: 'tipos-retencion',
  },
  mod: {
    url: '/api/admin/catalogos/modelos-fiscales', ayuda: 'Un modelo desactivado deja de ofrecerse en Modelos fiscales de todas las empresas.', sinForm: true,
    cols: ['Administración', 'Modelo', 'Nombre', 'Impuesto', 'Activo'],
    fila: m => [esc(m.administracion), esc(m.codigo), esc(m.nombre), esc(m.impuesto),
      `<button class="btn" type="button" data-act="${m.id}" data-v="${!m.activo}">${m.activo ? 'Desactivar' : 'Activar'}</button>`],
  },
  pais: {
    url: '/api/admin/catalogos/paises', ayuda: 'El código ISO de 2 letras identifica el país: guardar uno existente lo modifica.', clave: 'iso2',
    cols: ['ISO 2', 'ISO 3', 'Nombre', 'UE', 'Prefijo VIES'],
    fila: p => [esc(p.iso2), esc(p.iso3), esc(p.nombre), siNo(p.esUe), esc(p.prefijoVies || '')],
    campos: [['iso2', 'ISO 2', 'text', 1], ['iso3', 'ISO 3', 'text'], ['nombre', 'Nombre', 'text'], ['prefijoVies', 'Prefijo VIES', 'text'], ['esUe', 'Unión Europea', 'check']],
    guarda: o => put('/api/admin/catalogos/paises', o),
  },
  banco: {
    url: '/api/admin/catalogos/bancos', ayuda: 'Entidades bancarias (código de 4 dígitos del IBAN español y BIC).',
    cols: ['Entidad', 'BIC', 'Nombre', 'País', 'Activo'],
    fila: b => [esc(b.codigoEntidad || ''), esc(b.bic || ''), esc(b.nombre), esc(b.pais || ''), siNo(b.activo)],
    campos: [['codigoEntidad', 'Código de entidad', 'text'], ['bic', 'BIC', 'text'], ['nombre', 'Nombre', 'text'], ['pais', 'País (ISO 2)', 'text'], ['activo', 'Activo', 'check']],
    guarda: (o, id) => put('/api/admin/catalogos/bancos', { ...o, id: id || 0 }),
  },
  iae: {
    url: '/api/admin/catalogos/epigrafes', ayuda: 'Sección 1 empresarial, 2 profesional, 3 artística. Se muestran como mucho 300: usa el buscador.', busca: true,
    cols: ['Sección', 'Código', 'Descripción', 'Profesional', 'Clave AEAT'],
    fila: e => [e.seccion, esc(e.codigo), esc(e.descripcion), siNo(e.esProfesional), esc(e.claveActividad || '')],
    campos: [['seccion', 'Sección', ['1', '2', '3'], 1], ['codigo', 'Código', 'text', 1], ['descripcion', 'Descripción', 'text'], ['esProfesional', 'Actividad profesional', 'check'],
             ['claveActividad', 'Clave de actividad (303/390)', ['', 'A01', 'A02', 'A03', 'A04', 'A05']]],
    guarda: o => put('/api/admin/catalogos/epigrafes', { ...o, seccion: +o.seccion }),
  },
  fp: {
    url: () => `/api/empresas/${$('#cgEmpresa').value}/formas-pago`, ayuda: 'Formas de pago de la empresa: generan los vencimientos de sus facturas. Una forma en uso no se puede borrar.', empresa: true,
    cols: ['Código', 'Nombre', 'Tipo', 'Plazos', 'Días al primero', 'Días entre plazos', ''],
    fila: f => [esc(f.codigo), esc(f.nombre), esc(f.tipo), f.numPlazos, f.diasPrimerPlazo, f.diasEntrePlazos,
      `<button class="btn" type="button" data-borra="${f.id}">Borrar</button>`],
    campos: [['codigo', 'Código', 'text'], ['nombre', 'Nombre', 'text'], ['tipo', 'Tipo', ['TRANSFERENCIA', 'RECIBO', 'EFECTIVO', 'TARJETA', 'PAGARE', 'CHEQUE', 'OTRO']],
      ['numPlazos', 'Número de plazos', 'number'], ['diasPrimerPlazo', 'Días hasta el primero', 'number'], ['diasEntrePlazos', 'Días entre plazos', 'number']],
    guarda: (o, id) => { const u = `/api/empresas/${$('#cgEmpresa').value}/formas-pago`;
      const c = { ...o, numPlazos: +o.numPlazos || 1, diasPrimerPlazo: +o.diasPrimerPlazo || 0, diasEntrePlazos: +o.diasEntrePlazos || 0 };
      return id ? put(`${u}/${id}`, c) : post(u, c); },
  },
};
const CA = { filas: [], sel: null };

async function cargaCatalogos() {
  if (!$('#cgEmpresa').options.length) {
    const emps = await api('/api/admin/empresas');
    $('#cgEmpresa').innerHTML = emps.map(e => `<option value="${e.id}">${esc(e.nombre)}</option>`).join('');
  }
  await pintaCatalogo();
}

async function pintaCatalogo() {
  const c = CAT[$('#cgTipo').value];
  $('#cgEmpresaL').hidden = !c.empresa; $('#cgBuscaL').hidden = !c.busca;
  $('#cgAyuda').textContent = c.ayuda; msg($('#cgMsg'), ''); msg($('#cgFormMsg'), '');
  $('#cgFormPanel').hidden = !!c.sinForm; $('#cgVigPanel').hidden = true;
  let url = typeof c.url === 'function' ? c.url() : c.url;
  if (c.busca && $('#cgBusca').value.trim()) url += '?q=' + encodeURIComponent($('#cgBusca').value.trim());
  if (c.empresa && !$('#cgEmpresa').value) { CA.filas = []; } else CA.filas = await api(url);
  $('#cgHead').innerHTML = '<tr>' + c.cols.map(x => `<th>${x}</th>`).join('') + (c.sinForm ? '' : '<th></th>') + '</tr>';
  $('#cgBody').innerHTML = CA.filas.map((f, i) => `<tr>${c.fila(f).map(v => `<td>${v}</td>`).join('')}${c.sinForm ? ''
    : `<td><button class="btn" type="button" data-edita="${i}">Modificar</button></td>`}</tr>`).join('')
    || `<tr><td colspan="${c.cols.length + 1}" class="vacio">No hay registros.</td></tr>`;
  if (!c.sinForm) formCatalogo(null);
}

function formCatalogo(fila) {
  const c = CAT[$('#cgTipo').value];
  CA.sel = fila;
  $('#cgFormTitulo').textContent = fila ? 'Modificar' : 'Nuevo';
  $('#cgForm').innerHTML = c.campos.map(([k, etq, tipo, soloAlta]) => {
      const v = fila ? fila[k] : (tipo === 'check' ? k === 'activo' : '');
      const bloquea = fila && soloAlta ? ' disabled' : '';
      if (fila && c.vigencias && ['porcentaje', 'recargo', 'fechaDesde'].includes(k)) return '';
      if (Array.isArray(tipo)) return `<label>${etq} <select data-k="${k}"${bloquea}>${tipo.map(o => `<option value="${o}"${String(v ?? '') === o ? ' selected' : ''}>${o || '—'}</option>`).join('')}</select></label>`;
      if (tipo === 'check') return `<label class="campo" style="flex-direction:row; align-items:center; gap:8px; margin:22px 0 0"><input type="checkbox" data-k="${k}"${v ? ' checked' : ''}> ${etq}</label>`;
      return `<label>${etq} <input data-k="${k}" type="${tipo}"${tipo === 'number' ? ' step="0.01"' : ''} value="${esc(v ?? '')}"${bloquea}></label>`;
    }).join('');
  $('#cgVigPanel').hidden = !(fila && c.vigencias);
  if (fila && c.vigencias) {
    $('#cgVigTitulo').textContent = `Nueva vigencia de ${fila.codigo}`;
    $('#cgVigRecL').hidden = !c.recargo; $('#cgVigPct').value = pctVigente(fila)?.porcentaje ?? ''; $('#cgVigDesde').value = '';
    msg($('#cgVigMsg'), '');
  }
}

$('#cgTipo').onchange = () => pintaCatalogo().catch(e => msg($('#cgMsg'), e.message, 'ayuda err-texto'));
$('#cgEmpresa').onchange = $('#cgTipo').onchange;
$('#cgBusca').addEventListener('keydown', e => { if (e.key === 'Enter') $('#cgTipo').onchange(); });
$('#cgNuevo').onclick = () => formCatalogo(null);
$('#cgBody').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
  const c = CAT[$('#cgTipo').value];
  try {
    if (b.dataset.edita !== undefined) { formCatalogo(CA.filas[+b.dataset.edita]); $('#cgFormPanel').scrollIntoView({ behavior: 'smooth' }); return; }
    if (b.dataset.act) { await put(`/api/admin/catalogos/modelos-fiscales/${b.dataset.act}/activo`, { activo: b.dataset.v === 'true' }); }
    if (b.dataset.borra) {
      if (!confirm('¿Borrar la forma de pago?')) return;
      await api(`${c.url()}/${b.dataset.borra}`, { method: 'DELETE' });
    }
    await pintaCatalogo();
    msg($('#cgMsg'), 'Hecho.', 'ayuda ok-texto');
  } catch (err) { msg($('#cgMsg'), err.message, 'ayuda err-texto'); }
});
$('#cgGuardar').onclick = async () => {
  const c = CAT[$('#cgTipo').value];
  const o = { ...(CA.sel || {}) };
  $$('#cgForm [data-k]').forEach(el => {
    o[el.dataset.k] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? (el.value === '' ? null : +el.value) : (el.value === '' ? null : el.value);
  });
  if ('recargo' in o && o.recargo == null) o.recargo = 0;
  try {
    await c.guarda(o, CA.sel?.id);
    await pintaCatalogo();
    msg($('#cgFormMsg'), 'Guardado.', 'ok-texto');
  } catch (err) { msg($('#cgFormMsg'), err.message, 'err-texto'); }
};
$('#cgVigGuardar').onclick = async () => {
  const c = CAT[$('#cgTipo').value];
  try {
    await post(`/api/admin/catalogos/${c.vigencias}/${CA.sel.id}/vigencias`,
      { porcentaje: +$('#cgVigPct').value, recargo: +$('#cgVigRec').value || 0, fechaDesde: $('#cgVigDesde').value || null });
    const id = CA.sel.id;
    await pintaCatalogo();
    formCatalogo(CA.filas.find(f => f.id === id));
    msg($('#cgVigMsg'), 'Vigencia abierta.', 'ok-texto');
  } catch (err) { msg($('#cgVigMsg'), err.message, 'err-texto'); }
};

// ---- usuarios
async function cargaUsuarios() {
  const us = await api('/api/admin/usuarios');
  S.admUsuarios = us;
  $('#admUsuariosBody').innerHTML = us.map(u => `<tr>
    <td>${esc(u.nombre)}${u.esSuperadmin ? ' <small>(superadmin)</small>' : ''}</td><td>${esc(u.email)}</td><td>${u.tipo === 'GESTOR' ? 'Gestor' : 'Empresa'}</td>
    <td class="${u.activo ? '' : 'estado-cerrado'}">${u.activo ? 'activo' : 'desactivado'}</td><td class="num">${u.numEmpresas}</td>
    <td>${u.ultimoAcceso ? new Date(u.ultimoAcceso).toLocaleString('es-ES') : '—'}</td>
    <td class="fila-acciones">
      <button class="btn" data-a="emp" data-id="${u.id}">Empresas</button>
      <button class="btn" data-a="act" data-id="${u.id}" data-v="${!u.activo}">${u.activo ? 'Desactivar' : 'Activar'}</button>
      <button class="btn" data-a="pwd" data-id="${u.id}">Nueva contraseña</button></td></tr>`).join('');
}

$('#admUsuariosBody').addEventListener('click', async e => {
  const b = e.target.closest('button[data-a]'); if (!b) return;
  const id = +b.dataset.id;
  try {
    if (b.dataset.a === 'emp') await abrirAsignaciones(id);
    if (b.dataset.a === 'act') { await put(`/api/admin/usuarios/${id}/activo`, { activo: b.dataset.v === 'true' }); await cargaUsuarios(); }
    if (b.dataset.a === 'pwd') {
      const p = prompt('Contraseña temporal (mínimo 12 caracteres). El usuario deberá cambiarla al entrar:');
      if (p) { await post(`/api/admin/usuarios/${id}/password`, { password: p }); alert('Contraseña restablecida.'); }
    }
  } catch (err) { alert(err.message); }
});

$('#nuCrear').onclick = async () => {
  try {
    await post('/api/admin/usuarios', { email: $('#nuEmail').value, nombre: $('#nuNombre').value, tipo: $('#nuTipo').value, password: $('#nuPassword').value });
    msg($('#nuMsg'), 'Usuario creado.', 'ok-texto');
    ['#nuEmail', '#nuNombre', '#nuPassword'].forEach(i => $(i).value = '');
    await cargaUsuarios();
  } catch (e) { msg($('#nuMsg'), e.message, 'err-texto'); }
};

// ---- asignación de empresas y permisos
$('#asPermisos').innerHTML = Object.entries(MODULOS).map(([k, v]) =>
  `<label>${v}<select data-mod="${k}"><option value="">Según el rol</option><option value="NINGUNO">Sin acceso</option><option value="LECTURA">Lectura</option><option value="ESCRITURA">Escritura</option></select></label>`).join('');

async function abrirAsignaciones(usuarioId) {
  S.admUsuarioId = usuarioId;
  const u = S.admUsuarios.find(x => x.id === usuarioId);
  const [asig, emps] = await Promise.all([api(`/api/admin/usuarios/${usuarioId}/empresas`), api('/api/admin/empresas')]);
  S.admAsig = asig;
  $('#admAsigTitulo').textContent = `Empresas de ${u.nombre}`;
  $('#asEmpresa').innerHTML = emps.map(e => `<option value="${e.id}">${esc(e.nombre)}</option>`).join('');
  pintaAsignaciones();
  $('#admAsignaciones').hidden = false;
  $('#admAsignaciones').scrollIntoView({ behavior: 'smooth' });
}

function pintaAsignaciones() {
  $('#admAsigBody').innerHTML = S.admAsig.length ? S.admAsig.map(a => `<tr>
    <td>${esc(a.empresaNombre)}</td><td>${esc(a.rol)}</td><td>${a.saltaRevision ? 'sí' : 'no'}</td>
    <td><small>${Object.entries(a.permisos).map(([m, n]) => `${esc(MODULOS[m] || m)}: ${esc(n.toLowerCase())}`).join(' · ') || 'según el rol'}</small></td>
    <td class="fila-acciones"><button class="btn" data-a="ed" data-e="${a.empresaId}">Editar</button><button class="btn" data-a="del" data-e="${a.empresaId}">Quitar</button></td></tr>`).join('')
    : '<tr><td colspan="5" class="vacio">Sin empresas asignadas.</td></tr>';
}

$('#admAsigBody').addEventListener('click', async e => {
  const b = e.target.closest('button[data-a]'); if (!b) return;
  const empresaId = +b.dataset.e;
  if (b.dataset.a === 'ed') {
    const a = S.admAsig.find(x => x.empresaId === empresaId);
    $('#asEmpresa').value = String(empresaId); $('#asRol').value = a.rol; $('#asSalta').checked = a.saltaRevision;
    $$('#asPermisos select').forEach(s => s.value = a.permisos[s.dataset.mod] || '');
  } else if (confirm('¿Quitar el acceso a esta empresa?')) {
    try {
      await api(`/api/admin/usuarios/${S.admUsuarioId}/empresas/${empresaId}`, { method: 'DELETE' });
      S.admAsig = S.admAsig.filter(x => x.empresaId !== empresaId); pintaAsignaciones(); await cargaUsuarios();
    } catch (err) { alert(err.message); }
  }
});

$('#asGuardar').onclick = async () => {
  const empresaId = +$('#asEmpresa').value;
  const permisos = {};
  $$('#asPermisos select').forEach(s => { if (s.value) permisos[s.dataset.mod] = s.value; });
  try {
    await put(`/api/admin/usuarios/${S.admUsuarioId}/empresas/${empresaId}`, { rol: $('#asRol').value, saltaRevision: $('#asSalta').checked, permisos });
    msg($('#asMsg'), 'Asignación guardada.', 'ok-texto');
    S.admAsig = await api(`/api/admin/usuarios/${S.admUsuarioId}/empresas`); pintaAsignaciones(); await cargaUsuarios();
  } catch (e) { msg($('#asMsg'), e.message, 'err-texto'); }
};

// ---- empresas
async function cargaEmpresasAdmin() {
  const [emps, planes] = await Promise.all([api('/api/admin/empresas'), api('/api/admin/planes-modelo')]);
  $('#admEmpresasBody').innerHTML = emps.map(e => `<tr><td>${esc(e.codigo)}</td><td>${esc(e.nombre)}</td><td>${esc(e.nif)}</td>
    <td>${esc(e.formaJuridica)}</td><td>${esc(e.plan)}</td><td>${esc(e.impuestoIndirecto)}</td>
    <td><button class="btn" type="button" data-editar="${e.id}">Editar</button></td></tr>`).join('')
    || '<tr><td colspan="7" class="vacio">Todavía no hay empresas.</td></tr>';
  $('#nePlan').innerHTML = planes.map(p => `<option value="${p.id}">${esc(p.nombre)}</option>`).join('');
  if (!$('#a3Plan').options.length)
    $('#a3Plan').innerHTML = planes.map(p => `<option value="${p.id}" ${p.tipo === 'PYMES' ? 'selected' : ''}>${esc(p.nombre)}</option>`).join('');
  $('#ejEmpresa').innerHTML = emps.map(e => `<option value="${e.id}">${esc(e.nombre)}</option>`).join('');
}

// ---- edición de los datos y las actividades de una empresa
let ED = null, EPS = [];
$('#admEmpresasBody').addEventListener('click', async e => {
  const id = e.target.dataset.editar; if (!id) return;
  try { ED = await api(`/api/admin/empresas/${id}`); pintaEdicion(); $('#edPanel').hidden = false; $('#edPanel').scrollIntoView({ behavior: 'smooth' }); }
  catch (err) { alert(err.message); }
});
function pintaEdicion() {
  $('#edTitulo').textContent = `Editar empresa · ${ED.codigo}`;
  $('#edFijos').textContent = `NIF ${ED.nif} · plan ${ED.plan} · ${ED.digitosCuenta} dígitos de cuenta · ${ED.impuestoIndirecto}` +
    `${ED.provincia ? ' · provincia ' + ED.provincia : ''}. Estos datos no se cambian: con contabilidad hecha la descuadrarían.`;
  const v = { edNombre: ED.nombre, edComercial: ED.nombreComercial, edForma: ED.formaJuridica, edDireccion: ED.direccion, edCp: ED.codigoPostal,
              edPoblacion: ED.poblacion, edTelefono: ED.telefono, edEmail: ED.email, edSepa: ED.sepaSufijo };
  for (const [k, x] of Object.entries(v)) $('#' + k).value = x || '';
  $('#edActiva').checked = ED.activa;
  pintaActividadesEd();
  msg($('#edMsg'), '', '');
}
function pintaActividadesEd() {
  $('#edActividades').innerHTML = ED.actividades.map((a, i) => `<tr>
    <td>${esc(a.epigrafeCodigo)} <small>${esc(a.epigrafeDescripcion)} (sección ${a.seccion})</small></td><td>${esc(a.claveActividad || '')}</td>
    <td><input data-a="${i}" data-k="nombre" value="${esc(a.nombre || '')}" maxlength="200" aria-label="Nombre"></td>
    <td><input type="radio" name="edPrincipal" data-a="${i}" data-k="esPrincipal" ${a.esPrincipal ? 'checked' : ''} aria-label="Principal"></td>
    <td><input data-a="${i}" data-k="sectorDiferenciado" value="${a.sectorDiferenciado ?? ''}" inputmode="numeric" style="width:4em" aria-label="Sector"></td>
    <td><input type="date" data-a="${i}" data-k="fechaAlta" value="${a.fechaAlta}" aria-label="Alta"></td>
    <td><input type="date" data-a="${i}" data-k="fechaBaja" value="${a.fechaBaja || ''}" aria-label="Baja"></td>
    <td><button class="x" type="button" data-quitar="${i}" ${a.enUso ? 'disabled title="En uso: indica la fecha de baja"' : ''} aria-label="Quitar">×</button></td></tr>`).join('')
    || '<tr><td colspan="8" class="vacio">Sin actividades.</td></tr>';
}
$('#edActividades').addEventListener('change', e => {
  const t = e.target, a = ED.actividades[+t.dataset.a]; if (!a) return;
  if (t.dataset.k === 'esPrincipal') ED.actividades.forEach(x => x.esPrincipal = x === a);
  else if (t.dataset.k === 'sectorDiferenciado') a.sectorDiferenciado = t.value ? +t.value : null;
  else if (t.dataset.k === 'fechaBaja') a.fechaBaja = t.value || null;
  else a[t.dataset.k] = t.value;
});
$('#edActividades').addEventListener('click', e => {
  const i = e.target.dataset.quitar; if (i === undefined) return;
  ED.actividades.splice(+i, 1); pintaActividadesEd();
});
let buscaEp = null;
$('#edBuscaEp').addEventListener('input', () => {
  clearTimeout(buscaEp);
  buscaEp = setTimeout(async () => {
    const q = $('#edBuscaEp').value.trim(); if (q.length < 2) return;
    EPS = await api('/api/admin/catalogos/epigrafes?q=' + encodeURIComponent(q));
    $('#edEpigrafes').innerHTML = EPS.map((x, i) => `<option value="${i}">${esc(x.codigo)} · ${esc(x.descripcion.slice(0, 90))} (sección ${x.seccion})</option>`).join('')
      || '<option value="">Ningún epígrafe</option>';
  }, 300);
});
$('#edAnadirAct').onclick = () => {
  const x = EPS[+$('#edEpigrafes').value]; if (!x) return;
  ED.actividades.push({ id: null, epigrafeId: x.id, seccion: x.seccion, epigrafeCodigo: x.codigo, epigrafeDescripcion: x.descripcion,
    claveActividad: x.claveActividad, nombre: null, esPrincipal: !ED.actividades.some(a => a.esPrincipal && !a.fechaBaja),
    sectorDiferenciado: null, fechaAlta: hoy(), fechaBaja: null, enUso: false });
  pintaActividadesEd();
};
$('#edCerrar').onclick = () => { $('#edPanel').hidden = true; ED = null; };

// ---- importación de empresas de a3ECO: análisis (no cambia nada) y luego importación de las marcadas
let A3 = null;   // { contenido, empresas }
const FORMAS = [['SL', 'S.L.'], ['SA', 'S.A.'], ['SLU', 'S.L.U.'], ['AUTONOMO', 'Autónomo'], ['COMUNIDAD_BIENES', 'Comunidad de bienes'],
  ['SOCIEDAD_CIVIL', 'Sociedad civil'], ['COOPERATIVA', 'Cooperativa'], ['ASOCIACION', 'Asociación'], ['OTRA', 'Otra']];
$('#a3Fichero').onchange = () => { A3 = null; $('#a3Resultado').innerHTML = ''; $('#a3Importar').hidden = true; msg($('#a3Msg'), '', 'ayuda'); };
$('#a3Analizar').onclick = async () => {
  const f = $('#a3Fichero').files[0];
  if (!f) return msg($('#a3Msg'), 'Elige el fichero .zip.', 'ayuda err-texto');
  try {
    msg($('#a3Msg'), 'Analizando…', 'ayuda');
    const contenido = A3?.contenido ?? await new Promise((ok, mal) => { const r = new FileReader(); r.onload = () => ok(String(r.result).split(',')[1] || ''); r.onerror = mal; r.readAsDataURL(f); });
    A3 = { contenido, empresas: await post('/api/admin/importar-a3/analisis', { contenido }) };
    pintaA3(); msg($('#a3Msg'), '', 'ayuda');
  } catch (e) { msg($('#a3Msg'), e.message, 'err-texto'); }
};
function pintaA3() {
  const anio = a => `<span title="${esc(a.motivo || '')}" style="${a.importable ? '' : 'text-decoration:line-through;opacity:.6'}">${a.anio}</span>
    <small>(${a.asientos} asientos${a.descuadrados ? `, ${a.descuadrados} sin cuadrar` : ''}${a.comprobado ? '' : ', <b>no cuadra con a3</b>'})</small>`;
  $('#a3Resultado').innerHTML = `
    <table><thead><tr><th></th><th>a3</th><th>Empresa</th><th>NIF</th><th>En IConta</th><th>Ejercicios</th><th>Observaciones</th></tr></thead>
    <tbody>${A3.empresas.map((e, i) => {
      const importables = e.anios.filter(a => a.importable).length;
      const marca = !e.error && importables > 0;
      const destino = e.error ? '—' : e.empresaId ? `Ya existe: ${esc(e.empresaNombre)}`
        : `Nueva · código <input data-i="${i}" data-k="codigo" value="${esc(e.codigo)}" maxlength="10" style="width:7em">
           <select data-i="${i}" data-k="forma">${FORMAS.map(([v, t]) => `<option value="${v}" ${v === e.formaJuridica ? 'selected' : ''}>${t}</option>`).join('')}</select>`;
      const obs = [e.error, ...e.anios.filter(a => !a.importable).map(a => `${a.anio}: ${a.motivo}`), ...e.avisos].filter(Boolean);
      return `<tr><td><input type="checkbox" data-i="${i}" data-k="marcada" ${marca ? 'checked' : 'disabled'}></td>
        <td>${esc(e.carpeta)}</td><td>${esc(e.nombre)}</td><td>${esc(e.nif)}</td><td>${destino}</td>
        <td>${e.anios.map(anio).join('<br>') || '—'}</td><td class="ayuda">${obs.map(esc).join('<br>')}</td></tr>`;
    }).join('')}</tbody></table>`;
  $('#a3Importar').hidden = !A3.empresas.some(e => !e.error && e.anios.some(a => a.importable));
}
$('#a3Importar').onclick = async () => {
  const val = (i, k) => $(`#a3Resultado [data-i="${i}"][data-k="${k}"]`);
  const empresas = A3.empresas.map((e, i) => ({ e, i })).filter(({ i }) => val(i, 'marcada')?.checked)
    .map(({ e, i }) => ({ carpeta: e.carpeta, codigo: val(i, 'codigo')?.value || null, formaJuridica: val(i, 'forma')?.value || null }));
  if (!empresas.length) return msg($('#a3Msg'), 'Marca alguna empresa.', 'ayuda err-texto');
  if (!confirm(`¿Importar ${empresas.length} empresa(s) de a3ECO? Se crean las empresas nuevas y se importan todos sus ejercicios.`)) return;
  try {
    msg($('#a3Msg'), 'Importando… puede tardar unos minutos.', 'ayuda'); $('#a3Importar').disabled = true;
    const r = await post('/api/admin/importar-a3', { contenido: A3.contenido, planModeloId: +$('#a3Plan').value, impuestoIndirecto: $('#a3Impuesto').value, empresas });
    $('#a3Importar').hidden = true;
    $('#a3Resultado').innerHTML = `<ul>${r.map(x => `<li><strong>${esc(x.carpeta)} · ${esc(x.nombre)}</strong>: ${x.error ? `<span class="err-texto">${esc(x.error)}</span>`
      : `${x.creada ? 'empresa creada; ' : ''}ejercicios ${x.anios.join(', ')} con ${x.asientos} asiento(s)${x.borradores ? `; ${x.borradores} en borrador para revisar` : ''}.`}
      ${x.avisos.length ? `<br><small class="ayuda">${x.avisos.map(esc).join('<br>')}</small>` : ''}</li>`).join('')}</ul>`;
    const bien = r.filter(x => !x.error).length;
    msg($('#a3Msg'), `Importación terminada: ${bien} de ${r.length} empresa(s) sin errores.`, 'ayuda');
    cargaEmpresasAdmin();
  } catch (e) { msg($('#a3Msg'), e.message, 'err-texto'); }
  finally { $('#a3Importar').disabled = false; }
};$('#edGuardar').onclick = async () => {
  const cuerpo = { ...ED, nombre: $('#edNombre').value, nombreComercial: $('#edComercial').value || null, formaJuridica: $('#edForma').value,
    direccion: $('#edDireccion').value || null, codigoPostal: $('#edCp').value || null, poblacion: $('#edPoblacion').value || null,
    telefono: $('#edTelefono').value || null, email: $('#edEmail').value || null, sepaSufijo: $('#edSepa').value, activa: $('#edActiva').checked };
  try {
    ED = await put(`/api/admin/empresas/${ED.id}`, cuerpo); pintaEdicion();
    msg($('#edMsg'), 'Datos de la empresa guardados.', 'ok-texto');
    await cargaEmpresasAdmin();
  } catch (e) { msg($('#edMsg'), e.message, 'err-texto'); }
};

$('#neCrear').onclick = async () => {
  const cmd = { nif: $('#neNif').value, nombre: $('#neNombre').value, codigo: $('#neCodigo').value, formaJuridica: $('#neForma').value,
    planModeloId: +$('#nePlan').value, digitosCuenta: +$('#neDigitos').value, impuestoIndirecto: $('#neImpuesto').value, tributaIs: $('#neIs').checked,
    tipoDocumento: $('#neForma').value === 'AUTONOMO' ? 'NIF' : 'CIF' };
  if ($('#neEjCodigo').value) cmd.primerEjercicio = { codigo: $('#neEjCodigo').value, fechaInicio: $('#neEjInicio').value, fechaFin: $('#neEjFin').value };
  try {
    const r = await post('/api/admin/empresas', cmd);
    msg($('#neMsg'), 'Empresa creada.' + (r.avisos.length ? ' Avisos: ' + r.avisos.join(' ') : ''), r.avisos.length ? '' : 'ok-texto');
    ['#neNif', '#neNombre', '#neCodigo', '#neEjCodigo', '#neEjInicio', '#neEjFin'].forEach(i => $(i).value = '');
    await cargaEmpresasAdmin();
  } catch (e) { msg($('#neMsg'), e.message, 'err-texto'); }
};

$('#ejCrear').onclick = async () => {
  try {
    await post(`/api/admin/empresas/${$('#ejEmpresa').value}/ejercicios`, { codigo: $('#ejCodigo').value, fechaInicio: $('#ejInicio').value, fechaFin: $('#ejFin').value });
    msg($('#ejMsg'), 'Ejercicio creado.', 'ok-texto');
    ['#ejCodigo', '#ejInicio', '#ejFin'].forEach(i => $(i).value = '');
  } catch (e) { msg($('#ejMsg'), e.message, 'err-texto'); }
};

// ============================================================ arranque
(async function init() {
  if (!S.token) { vista('login'); return; }
  try {
    const u = await api('/api/auth/yo');
    S.user = u; sessionStorage.setItem('usuario', JSON.stringify(u));
    await despuesDeLogin();
  } catch { salir(''); }
})();
