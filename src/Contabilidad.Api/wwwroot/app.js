'use strict';
// ============================================================ utilidades
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
  const r = await fetch(url, { ...opt, headers });
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
  $('#contexto').textContent = id === 'app' ? `${S.empresa.nombre} · Ejercicio ${S.ejercicio.codigo}` : id === 'admin' ? 'Administración' : '';
}

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
    $('#tituloEjercicio').textContent = `${empresa.nombre}: ¿qué ejercicio?`;
    $('#listaEjercicios').innerHTML = ejercicios.length
      ? ejercicios.map(x => `<button type="button" data-id="${x.id}"><span>Ejercicio ${esc(x.codigo)}${x.esPartido ? ' (partido)' : ''}<br><small>${fechaES(x.fechaInicio)} – ${fechaES(x.fechaFin)}</small></span>
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
  if (req === 'BALANCES') return ['BALANCES', 'CONTABILIDAD'].some(m => ORDEN[S.acceso.permisos[m]] >= ORDEN.LECTURA);
  if (req === 'CONTABILIDAD_DIRECTO') return S.acceso.puedeContabilizarDirecto && ORDEN[S.acceso.permisos.CONTABILIDAD] >= ORDEN.ESCRITURA;
  if (req === 'REVISOR')
    return S.acceso.puedeContabilizarDirecto && ['CONTABILIDAD', 'FACTURACION'].some(m => ORDEN[S.acceso.permisos[m]] >= ORDEN.ESCRITURA);
  const [m, n] = req.split(':');
  return ORDEN[S.acceso.permisos[m]] >= ORDEN[n];
}
const aplicaPermisos = () => $$('#vista-app [data-req]').forEach(el => el.hidden = !permitido(el.dataset.req));

async function entrarEmpresa(empresa, ejercicio) {
  S.empresa = empresa; S.ejercicio = ejercicio;
  S.acceso = await api(urlE('/mi-acceso'));
  aplicaPermisos();

  aplicaEstadoEjercicio();
  $('#avisoBorrador').hidden = S.acceso.puedeContabilizarDirecto;

  // Las fechas de trabajo quedan acotadas al ejercicio elegido
  const dentro = d => d < ejercicio.fechaInicio ? ejercicio.fechaInicio : d > ejercicio.fechaFin ? ejercicio.fechaFin : d;
  for (const id of ['#fecha', '#fFecha']) {
    $(id).min = ejercicio.fechaInicio; $(id).max = ejercicio.fechaFin; $(id).value = dentro(hoy());
  }

  const pide = [];
  if (permitido('CONTABILIDAD:LECTURA') || permitido('FACTURACION:LECTURA'))
    pide.push(api(urlE('/cuentas')), api(urlE('/canales')), api(urlE('/diarios')));
  [S.cuentas = [], S.canales = [], S.diarios = []] = await Promise.all(pide);

  $('#dl-cuentas').innerHTML = S.cuentas.map(c => `<option value="${esc(c.codigo)}">${esc(c.nombre)}</option>`).join('');
  $('#diario').innerHTML = S.diarios.map(d => `<option value="${d.id}">${esc(d.codigo)} · ${esc(d.nombre)}</option>`).join('');
  $('#mayorCanal').innerHTML = '<option value="">Todos los canales</option>' + baseCanales().map(c => `<option value="${c.id}">Canal ${esc(c.codigo)}</option>`).join('');
  S.lineas = [nuevaLinea(), nuevaLinea()];
  fst.lineas = [];
  MF.modelos = []; MF.actual = null; MF.presentando = null; MF.liq = null;
  $('#mfResultado').hidden = true; $('#mfPresentarPanel').hidden = true; $('#mfLiqPanel').hidden = true;
  TES.liq = null; $('#liqPanel').hidden = true;
  pintaLineas(); recalcula();

  vista('app');
  const primera = $$('#navApp button').find(b => !b.hidden);
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
  ['asiento', 'factura', 'diario', 'mayor', 'tesoreria', 'fiscal', 'pendientes', 'balances', 'cierre'].forEach(t => $('#tab-' + t).hidden = t !== nombre);
  const carga = { factura: cargaFacturas, diario: cargaDiario, tesoreria: cargaTesoreria, fiscal: cargaFiscal, pendientes: cargaPendientes,
                  balances: cargaBalances, cierre: cargaCierre }[nombre];
  if (carga) carga().catch(e => alert(e.message));
}
$('#navApp').addEventListener('click', e => { const b = e.target.closest('button[data-tab]'); if (b) abreTab(b.dataset.tab); });

// ------------------------------------------------------------ nuevo asiento
const nuevaLinea = () => ({ cuenta: '', cuentaId: null, nombre: '', concepto: '', canalId: null, debe: '', haber: '' });
const canalOpts = sel => S.canales.map(c => `<option value="${c.id}" ${String(sel) === String(c.id) ? 'selected' : ''}>${esc(c.codigo)}</option>`).join('');

function pintaLineas() {
  const def = S.canales.find(c => c.codigo === 'A') || S.canales[0];
  S.lineas.forEach(l => { if (!l.canalId && def) l.canalId = def.id; });
  $('#lineas').innerHTML = S.lineas.map((l, i) => `
    <tr data-i="${i}">
      <td><input data-f="cuenta" list="dl-cuentas" value="${esc(l.cuenta)}" aria-label="Cuenta apunte ${i + 1}"></td>
      <td class="nombre">${esc(l.nombre) || '<span style="opacity:.6">—</span>'}</td>
      <td><select data-f="canalId" aria-label="Canal apunte ${i + 1}">${canalOpts(l.canalId)}</select></td>
      <td><input data-f="debe" class="num" inputmode="decimal" value="${esc(l.debe)}" aria-label="Debe apunte ${i + 1}"></td>
      <td><input data-f="haber" class="num" inputmode="decimal" value="${esc(l.haber)}" aria-label="Haber apunte ${i + 1}"></td>
      <td><button class="x" type="button" data-del="${i}" aria-label="Quitar apunte ${i + 1}">×</button></td>
    </tr>`).join('');
}

$('#lineas').addEventListener('input', e => {
  const tr = e.target.closest('tr'); if (!tr) return;
  const l = S.lineas[+tr.dataset.i], f = e.target.dataset.f;
  l[f] = e.target.value;
  if (f === 'cuenta') {
    const c = S.cuentas.find(x => x.codigo === l.cuenta.trim());
    l.cuentaId = c ? c.id : null; l.nombre = c ? c.nombre : '';
    tr.querySelector('.nombre').textContent = l.nombre || '—';
  }
  if (f === 'debe' && num(l.debe)) { l.haber = ''; tr.querySelector('[data-f=haber]').value = ''; }
  if (f === 'haber' && num(l.haber)) { l.debe = ''; tr.querySelector('[data-f=debe]').value = ''; }
  recalcula();
});
$('#lineas').addEventListener('change', e => {
  if (e.target.dataset.f === 'canalId') { S.lineas[+e.target.closest('tr').dataset.i].canalId = +e.target.value; recalcula(); }
});
$('#lineas').addEventListener('click', e => {
  const d = e.target.dataset.del; if (d === undefined) return;
  S.lineas.splice(+d, 1); if (!S.lineas.length) S.lineas.push(nuevaLinea());
  pintaLineas(); recalcula();
});
$('#addLinea').onclick = () => { S.lineas.push(nuevaLinea()); pintaLineas(); recalcula(); };
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
  const cmd = { fecha: $('#fecha').value, diarioId: +$('#diario').value, concepto: $('#concepto').value, borrador,
    apuntes: lineas.map(l => ({ cuentaId: l.cuentaId, canalId: l.canalId, debe: num(l.debe), haber: num(l.haber), concepto: l.concepto || null })) };
  try {
    const r = await post(urlX('/asientos'), cmd);
    msg($('#msg'), r.estado === 'CONTABILIZADO' ? `Asiento ${r.numero} contabilizado.`
      : `Asiento ${r.numero} guardado como borrador${r.forzadoBorrador ? ': queda pendiente de revisión' : ''}.`, 'ok');
    S.lineas = [nuevaLinea(), nuevaLinea()]; $('#concepto').value = '';
    pintaLineas(); calcula();
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
      h += `<tr class="asiento-cab"><td colspan="5">${esc(f.diario)} · Nº ${f.numero} · ${fechaES(f.fecha)} · ${esc(f.concepto)}${f.estado !== 'CONTABILIZADO' ? ' (' + esc(f.estado.toLowerCase()) + ')' : ''}</td></tr>`;
    }
    h += `<tr><td>${esc(f.cuenta)}</td><td class="nombre">${esc(f.cuentaNombre)}</td><td>${esc(f.canal)}</td><td class="num">${f.debe ? fmt(f.debe) : ''}</td><td class="num">${f.haber ? fmt(f.haber) : ''}</td></tr>`;
  }
  $('#diarioBody').innerHTML = h;
}

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
const nuevaFL = () => ({ tipoIvaId: null, base: '', regla: 'TOTAL', pct: '' });
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
      <td ${esEmitida() ? 'style="display:none"' : ''}>
        <select data-f="regla" aria-label="Deducción ${i + 1}">
          <option value="TOTAL" ${l.regla === 'TOTAL' ? 'selected' : ''}>Deducible al 100 %</option>
          <option value="PRORRATA" ${l.regla === 'PRORRATA' ? 'selected' : ''}>Prorrata</option>
          <option value="PORCENTAJE_AFECTACION" ${l.regla === 'PORCENTAJE_AFECTACION' ? 'selected' : ''}>% de afectación</option>
          <option value="NINGUNA" ${l.regla === 'NINGUNA' ? 'selected' : ''}>No deducible</option>
        </select>
        ${l.regla === 'PORCENTAJE_AFECTACION' ? `<input data-f="pct" class="num" inputmode="decimal" value="${esc(l.pct)}" placeholder="% afectación" aria-label="% afectación ${i + 1}">` : ''}
      </td>
      <td><button class="x" type="button" data-fdel="${i}" aria-label="Quitar línea ${i + 1}">×</button></td>
    </tr>`).join('');
}

$('#fLineas').addEventListener('input', e => {
  const tr = e.target.closest('tr'); if (!tr || !e.target.dataset.f) return;
  fst.lineas[+tr.dataset.i][e.target.dataset.f] = e.target.dataset.f === 'tipoIvaId' ? +e.target.value : e.target.value;
});
$('#fLineas').addEventListener('change', e => { if (e.target.dataset.f === 'regla') pintaFL(); });
$('#fLineas').addEventListener('click', e => {
  const d = e.target.dataset.fdel; if (d === undefined) return;
  fst.lineas.splice(+d, 1); if (!fst.lineas.length) fst.lineas.push(nuevaFL()); pintaFL();
});
$('#fAdd').onclick = () => { const l = nuevaFL(); l.tipoIvaId = (fst.tipos[0] || {}).id ?? null; fst.lineas.push(l); pintaFL(); };
$('#fLibro').onchange = () => { fst.lineas.forEach(l => l.regla = 'TOTAL'); $('#fPreview').hidden = true; cargaFacturas().catch(e => setF(e.message, 'err')); };
$('#fFecha').onchange = () => cargaFacturas().catch(e => setF(e.message, 'err'));

function cmdFactura(borrador) {
  return {
    libro: $('#fLibro').value, tipoFactura: 'ORDINARIA', numero: $('#fNumero').value.trim(),
    terceroId: +$('#fTercero').value || 0, formaPagoId: +$('#fForma').value || null, canalId: +$('#fCanal').value || 0,
    fechaExpedicion: $('#fFecha').value, tipoRetencionId: +$('#fRet').value || null, borrador,
    lineas: fst.lineas.filter(l => l.tipoIvaId && num(l.base)).map(l => {
      const t = fst.tipos.find(x => x.id === l.tipoIvaId);
      return { tipoIvaId: l.tipoIvaId, base: num(l.base), tipoOperacion: t && t.esExento ? 'EXENTA' : 'INTERIOR',
               reglaDeduccion: esEmitida() ? 'TOTAL' : l.regla, porcentajeDeduccion: l.regla === 'PORCENTAJE_AFECTACION' ? num(l.pct) : null };
    })
  };
}

function pintaPreview(r) {
  const t = r.calculo.totales;
  const caja = (n, v) => `<div><span>${n}</span><strong>${fmt(v)}</strong></div>`;
  $('#fTotales').innerHTML = caja('Base', t.base) + caja('Cuota', t.cuota)
    + (esEmitida() ? '' : caja('Cuota deducible', t.cuotaDeducible) + caja('No deducible', t.cuotaNoDeducible))
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
    <td class="num">${fmt(f.total)}</td><td>${f.asientoNumero ? 'asiento ' + f.asientoNumero : esc(f.estado.toLowerCase())}</td></tr>`).join('')
    : '<tr><td colspan="9" class="vacio">Todavía no hay facturas en este ejercicio.</td></tr>';
}

$('#tCrear').onclick = async () => {
  const tipo = $('#tTipo').value;
  try {
    await post(urlE('/terceros'), { nif: $('#tNif').value, nombre: $('#tNombre').value, esCliente: tipo !== 'PROVEEDOR', esProveedor: tipo !== 'CLIENTE' });
    msg($('#tMsg'), 'Tercero creado.', 'ok-texto'); $('#tNif').value = ''; $('#tNombre').value = '';
    await cargaFacturas();
  } catch (e) { msg($('#tMsg'), e.message, 'err-texto'); }
};


// ------------------------------------------------------------ tesorería
const TES = { venc: [], bancos: [], liq: null };
const estadoTxt = { PENDIENTE: 'pendiente', PARCIAL: 'parcial', LIQUIDADO: 'liquidado', ANULADO: 'anulado' };

async function cargaTesoreria() {
  await Promise.all([cargaPrevision(), cargaVencimientos(), cargaBancos()]);
}

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
        ${v.estado === 'PENDIENTE' && v.importePendiente === v.importe && directo ? `<button class="btn" data-anul="${v.id}">Anular</button>` : ''}</td></tr>`;
  }).join('') : '<tr><td colspan="9" class="vacio">No hay vencimientos con ese filtro.</td></tr>';
}
$('#vTipo').onchange = $('#vEstado').onchange = () => cargaVencimientos().catch(e => msg($('#vMsg'), e.message, 'ayuda err-texto'));

$('#vBody').addEventListener('click', async e => {
  const b = e.target.closest('button'); if (!b) return;
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
const periodoTxt = p => ({ '0A': 'Anual', '1T': '1.er trimestre', '2T': '2.º trimestre', '3T': '3.er trimestre', '4T': '4.º trimestre' }[p] || 'Mes ' + p);

async function cargaFiscal() {
  if (!$('#mfAnio').value) $('#mfAnio').value = new Date().getFullYear();
  if (!MF.modelos.length) {
    MF.modelos = await api(urlE('/fiscal/modelos'));
    $('#mfModelo').innerHTML = MF.modelos.length
      ? MF.modelos.map(m => `<option value="${esc(m.codigo)}">${esc(m.codigo)} · ${esc(m.nombre)}</option>`).join('')
      : '<option value="">Ningún modelo aplicable a esta empresa</option>';
    pintaPeriodos();
  }
  await cargaDeclaraciones();
}

function pintaPeriodos() {
  const m = MF.modelos.find(x => x.codigo === $('#mfModelo').value);
  $('#mfPeriodo').innerHTML = m ? m.periodos.map(p => `<option value="${esc(p)}">${esc(periodoTxt(p))}</option>`).join('') : '';
  $('#mfDevolucionLabel').hidden = !m || !['IVA', 'IGIC'].includes(m.impuesto) || m.periodos[0] === '0A';
}
$('#mfModelo').onchange = pintaPeriodos;

const peticionModelo = () => ({ modelo: $('#mfModelo').value, anio: +$('#mfAnio').value, periodo: $('#mfPeriodo').value, solicitaDevolucion: $('#mfDevolucion').value === '1' });

function muestraDeclaracion(v) {
  MF.actual = v;
  const caja = (n, x, cls) => `<div><span>${n}</span><strong class="${cls || ''}">${fmt(x)}</strong></div>`;
  $('#mfTitulo').textContent = `Modelo ${v.modelo} · ${v.anio} · ${periodoTxt(v.periodo)}`;
  $('#mfSub').textContent = `${v.nombre}. Estado: ${estadoDecl[v.estado] || v.estado}. Plazo orientativo de presentación: ${fechaES(v.plazoDesde)} – ${fechaES(v.plazoHasta)}.`;
  $('#mfTotales').innerHTML = caja('Resultado', v.resultado, v.resultado < 0 ? 'estado-mal' : '')
    + (v.aIngresar ? caja('A ingresar', v.aIngresar) : '') + (v.aDevolver ? caja('A devolver', v.aDevolver) : '') + (v.aCompensar ? caja('A compensar', v.aCompensar) : '');
  $('#mfAvisos').innerHTML = v.avisos.map(a => `<li>${esc(a)}</li>`).join('');
  $('#mfCasillas').innerHTML = v.casillas.map(c => `<tr><td>${esc(c.clave)}</td><td>${esc(c.descripcion)}</td><td class="num">${fmt(c.importe)}</td></tr>`).join('')
    || '<tr><td colspan="3" class="vacio">No hay operaciones en este periodo.</td></tr>';
  const filas = Array.isArray(v.detalle) ? v.detalle : null;
  $('#mfDetalle').hidden = !filas;
  $('#mfDetalleBody').innerHTML = filas ? (filas.map(f => `<tr><td>${esc(f.nif)}</td><td>${esc(f.nombre)}</td><td>${esc(f.clave)}</td><td class="num">${fmt(f.total)}</td>
    <td class="num">${fmt(f.t1)}</td><td class="num">${fmt(f.t2)}</td><td class="num">${fmt(f.t3)}</td><td class="num">${fmt(f.t4)}</td></tr>`).join('')
    || '<tr><td colspan="8" class="vacio">Ningún tercero supera el umbral.</td></tr>') : '';
  $('#mfResultado').hidden = false;
}

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
  $('#mfLista').innerHTML = filas.length ? filas.map(d => `<tr><td>${esc(d.modelo)} <small>${esc(d.nombre)}</small></td><td>${d.anio}</td><td>${esc(periodoTxt(d.periodo))}</td>
    <td>${estadoDecl[d.estado] || esc(d.estado)}</td><td class="num">${d.resultado == null ? '' : fmt(d.resultado)}</td>
    <td>${d.fechaPresentacion ? fechaES(d.fechaPresentacion) + (d.justificante ? ' · ' + esc(d.justificante) : '') : ''}</td>
    <td>${d.asientoNumero ? 'nº ' + d.asientoNumero : ''}</td>
    <td class="fila-acciones"><button class="btn" data-ver="${d.id}">Ver</button>
      ${d.estado === 'CALCULADA' && directo ? `<button class="btn primario" data-pres="${d.id}">Presentar</button><button class="btn" data-desc="${d.id}">Descartar</button>` : ''}
      ${liquidable(d) && directo && !d.asientoNumero ? `<button class="btn primario" data-liq="${d.id}" ${abierto ? '' : 'disabled'}>Contabilizar liquidación</button>` : ''}
      ${d.asientoNumero && directo ? `<button class="btn" data-anliq="${d.id}" ${abierto ? '' : 'disabled'}>Anular asiento</button>` : ''}</td></tr>`).join('')
    : '<tr><td colspan="8" class="vacio">No hay declaraciones guardadas en este año.</td></tr>';
}
const liquidable = d => d.estado === 'PRESENTADA' && ['303', '420'].includes(d.modelo) && d.periodo !== '0A';
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
  const inf = $('#baInforme').value;
  $('#baDesdeL').hidden = inf === 'situacion';
  $('#baNivelL').hidden = $('#baIncluirL').hidden = inf !== 'sumas-saldos';
}
$('#baInforme').onchange = () => { pintaControlesBalance(); verBalance(); };

async function verBalance() {
  const inf = $('#baInforme').value;
  const q = new URLSearchParams({ canalId: $('#baCanal').value, hasta: $('#baHasta').value });
  if (inf !== 'situacion') q.set('desde', $('#baDesde').value);
  if (inf === 'sumas-saldos') { q.set('nivel', $('#baNivel').value); q.set('incluir', $('#baIncluir').value); }
  try {
    const r = await api(urlX(`/balances/${inf}?${q}`));
    msg($('#baMsg'), '', 'ayuda');
    if (inf === 'sumas-saldos') pintaSumasSaldos(r); else pintaEstado(r);
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
$('#baBody').addEventListener('click', e => {
  const tr = e.target.closest('tr[data-abre]'); if (!tr) return;
  const abierta = tr.classList.toggle('abierta');
  $$(`#baBody tr[data-de="${CSS.escape(tr.dataset.abre)}"]`).forEach(d => d.hidden = !abierta);
});

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
  $('#adm-usuarios').hidden = t !== 'usuarios'; $('#adm-empresas').hidden = t !== 'empresas';
  (t === 'usuarios' ? cargaUsuarios : cargaEmpresasAdmin)().catch(e => alert(e.message));
}
$('#navAdmin').addEventListener('click', e => { const b = e.target.closest('button[data-tab]'); if (b) abreTabAdmin(b.dataset.tab); });

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
    <td>${esc(e.formaJuridica)}</td><td>${esc(e.plan)}</td><td>${esc(e.impuestoIndirecto)}</td></tr>`).join('')
    || '<tr><td colspan="6" class="vacio">Todavía no hay empresas.</td></tr>';
  $('#nePlan').innerHTML = planes.map(p => `<option value="${p.id}">${esc(p.nombre)}</option>`).join('');
  $('#ejEmpresa').innerHTML = emps.map(e => `<option value="${e.id}">${esc(e.nombre)}</option>`).join('');
}

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
