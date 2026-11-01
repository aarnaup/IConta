// Ayuda de la pantalla en la que estás: botón «Ayuda» de la cabecera o F1. El panel se actualiza al cambiar de pantalla.
(() => {
  const L = (...xs) => '<ul>' + xs.map(x => `<li>${x}</li>`).join('') + '</ul>';
  const P = t => `<p>${t}</p>`;
  const H = t => `<h3>${t}</h3>`;
  const K = t => `<kbd>${t}</kbd>`;

  const CUENTA = 'En las cuentas puedes escribir con punto (430.5 = 43000005), el principio del código o parte del nombre.';
  const TECLAS_REJILLA = L(`${K('Enter')} pasa a la celda siguiente; al final de la línea, a una nueva.`,
    `${K('↑')} ${K('↓')} cambian de línea.`, `${K('Ctrl')}+${K('Supr')} borra la línea.`,
    'El importe que falta para cuadrar se propone solo en la línea nueva.');

  const AYUDA = {
    // ------------------------------------------------------------------ fuera de la empresa
    login: { t: 'Entrar', h: P('Escribe tu email y tu contraseña. Si es la primera vez o te la han cambiado, te pedirá una nueva antes de seguir.') },
    cambio: { t: 'Cambiar la contraseña', h: P('Tu contraseña es temporal: elige una nueva para poder usar la aplicación.') },
    empresas: { t: 'Elegir empresa', h: P('Pulsa una empresa y después el ejercicio con el que quieres trabajar.') +
      L('Solo ves las empresas a las que tienes acceso y, en cada una, lo que permiten tus permisos.',
        'Desde «Cambiar empresa» vuelves aquí en cualquier momento.',
        'Si eres superadmin, «Administración» abre usuarios, empresas, catálogos y legislación.') },

    // ------------------------------------------------------------------ administración
    'admin:usuarios': { t: 'Usuarios', h: P('Alta de usuarios y acceso a las empresas.') +
      L('Un usuario <b>gestor</b> trabaja en varias empresas; uno de <b>empresa</b>, solo en la suya.',
        'En cada empresa se le da un rol y permisos por módulo (contabilidad, facturación, impuestos…), de lectura o de escritura.',
        'Al crearlo recibe una contraseña temporal que deberá cambiar al entrar.',
        'Los cambios quedan en la auditoría.') },
    'admin:empresas': { t: 'Empresas', h: P('Alta y datos de las empresas y sus ejercicios.') +
      L('Al crear una empresa se copia el plan contable modelo, se crean diarios, canales y cuentas automáticas.',
        '«Editar» cambia nombre, dirección, forma jurídica, si está activa y sus actividades (epígrafe del IAE). El NIF, el plan y los dígitos de cuenta no cambian.',
        'Una actividad con movimientos no se quita: se le pone fecha de baja.',
        'Un ejercicio no puede solaparse con otro; puede ser partido (por ejemplo, de julio a junio).',
        '«Importar de a3ECO»: desde un zip o la carpeta de a3ECO. Crea las empresas que no existen (por NIF) con todos sus ejercicios.') },
    'admin:catalogos': { t: 'Catálogos', h: P('Tablas comunes a todas las empresas.') +
      L('Países, bancos (código de entidad y BIC) y epígrafes del IAE con su clave de actividad.',
        'Modelos fiscales: uno desactivado deja de ofrecerse en todas las empresas.',
        'Formas de pago de cada empresa: generan los vencimientos de sus facturas; una en uso no se puede borrar.',
        'Los tipos de IVA/IGIC y de retención están en <b>Legislación</b>.') },
    'admin:legislacion': { t: 'Legislación por ejercicio', h: P('Todo lo que cambia con la ley rige desde una fecha o un año. Cada declaración y cada balance usan lo vigente en su periodo: un cambio no altera los ejercicios anteriores ni lo ya presentado.') +
      L('Elige el <b>apartado</b> y el <b>ejercicio</b> para ver lo vigente ese año.', 'Cada cambio queda en la auditoría.') },
    'admin:legislacion:iva': { t: 'Tipos de IVA / IGIC', h: P('Los tipos no se borran: se dan de baja o se abre una vigencia nueva.') +
      L('«Modificar» un tipo y, abajo, «Abrir vigencia» con el porcentaje nuevo y la fecha desde la que se aplica.',
        'La vigencia anterior se cierra ese día: las facturas anteriores conservan su porcentaje.') },
    'admin:legislacion:ret': { t: 'Tipos de retención', h: P('Igual que los de IVA: cada porcentaje tiene su vigencia.') +
      L('El <b>modelo de ingreso</b> (111, 115…) y la <b>clave del resumen anual</b> (190, 180) deciden dónde se declara cada retención.') },
    'admin:legislacion:par': { t: 'Parámetros de cálculo', h: P('Valores que usan los cálculos: % del 130, umbral del 347, mínimo de bienes de inversión, tipos del Impuesto sobre Sociedades, % del 202…') +
      L('La columna «Vigente» muestra el valor del ejercicio elegido; el historial, todos con su fecha.',
        'Pulsa una clave para copiarla abajo, cambia el valor y la fecha «Rige desde» y guarda.',
        'Los porcentajes se escriben como porcentaje (25 = 25 %).',
        'La × borra un valor: esos periodos pasan a usar el anterior.') },
    'admin:legislacion:form': { t: 'Casillas de los modelos', h: P('La disposición de las casillas de cada modelo, por año.') +
      L('«Cargar el vigente en el ejercicio» trae el formulario actual para partir de él.',
        'Se edita en JSON: <code>apartados</code> (títulos, columnas y filas con sus casillas), <code>totales</code> (casilla = suma de casillas con signo), <code>resultado</code> y <code>renombrar</code> si la AEAT cambia la numeración.',
        'Se guarda como versión «desde el ejercicio»; al guardar se comprueba que el JSON sea correcto.') },
    'admin:legislacion:dis': { t: 'Diseños de fichero AEAT', h: P('El diseño de registro del fichero de presentación de cada modelo, con fechas de vigencia.') +
      L('La marca ✓ indica el diseño vigente en el ejercicio elegido.',
        '«Cargar el vigente» o «Abrir fichero .json», cambia lo necesario y guarda con su fecha «Desde» (y «Hasta» si tiene fin).',
        'Uno guardado tiene preferencia sobre el del programa con la misma fecha.',
        'Para convertir el Excel de la AEAT a JSON se usa <code>tools/disenos-aeat/oficiales.py</code>.') },
    'admin:legislacion:est': { t: 'Composición de los balances', h: P('Qué cuentas van a cada partida del balance y de la cuenta de pérdidas y ganancias: cuentas anuales (PYMES/abreviadas y normales) y páginas 3 a 8 del modelo 200.') +
      L('Busca una cuenta (por ejemplo 4709): se marca la partida a la que va, la del prefijo más largo que coincide.',
        'De cada cuenta se toma el <b>saldo</b>, el saldo <b>solo si es deudor</b> o <b>acreedor</b>, o la suma del <b>debe</b> o del <b>haber</b>; y <b>suma</b> o <b>resta</b>. La columna «Cálculo» lo resume.',
        'En activo y gastos el saldo es debe − haber; en patrimonio neto, pasivo e ingresos, haber − debe. Las amortizaciones y deterioros restan solos.',
        '«Editar» una partida y «Guardar versión» desde un ejercicio. Una cuenta no puede estar dos veces con el mismo concepto.',
        'Borrar una versión devuelve esos ejercicios a la anterior o a la del programa.') },

    // ------------------------------------------------------------------ contabilidad
    asiento: { t: 'Nuevo asiento', h: P('Asiento libre con varias líneas.') + L(CUENTA,
        'El asiento debe cuadrar (debe = haber) para contabilizarlo.', 'Elige diario, fecha y concepto; cada línea puede llevar su canal y su reparto analítico.') +
      H('Teclas') + TECLAS_REJILLA },
    factura: { t: 'Facturas', h: P('Facturas emitidas (ventas) y recibidas (compras) con su asiento.') +
      L('Elige el tercero (o créalo desde aquí), las bases con su tipo de IVA/IGIC y, si hay, la retención.',
        '«Cálculo y asiento previsto» muestra las cuotas y el asiento antes de guardar.',
        'La forma de pago genera los vencimientos en Tesorería.',
        'Las facturas alimentan los libros registro, el SII y las declaraciones de IVA.') },
    rejilla: { t: 'Rejilla de diario', h: P('Los asientos de un diario como una hoja de cálculo.') +
      L('Cada línea se graba al pasar a otra.', 'Un asiento que no cuadra se guarda como borrador y se marca en rojo con su descuadre; al cuadrar, se contabiliza.',
        'Al final de la última línea, si el asiento cuadra, empieza uno nuevo.', CUENTA) + H('Teclas') + TECLAS_REJILLA },
    diario: { t: 'Diario', h: P('Los últimos asientos del ejercicio.') +
      L('«Modificar» abre el asiento en «Nuevo asiento» (conserva su diario y su número); «Borrar» lo elimina.',
        'Los asientos generados por facturas, cierres o amortizaciones se cambian desde su origen.',
        'En un ejercicio cerrado solo se consulta.') },
    mayor: { t: 'Mayor', h: P('Los movimientos de una cuenta con su saldo acumulado.') + L('Escribe el código de la cuenta, elige el canal y pulsa «Consultar».') },
    nominas: { t: 'Nóminas', h: P('Registro de nóminas con su asiento y la retención del trabajo.') +
      L('Indica bruto, retención, Seguridad Social, anticipos y especie; el líquido se calcula solo.',
        'El asiento lleva la 640 por el bruto y la 642 por la Seguridad Social de la empresa.', 'Las retenciones van al modelo 111 y al resumen anual 190.', 'En «Trabajadores» se completan los datos que pide el 190.') },
    pendientes: { t: 'Pendientes de revisión', h: P('Asientos y facturas guardados como borrador por usuarios que necesitan revisión.') +
      L('«Aprobar» lo contabiliza; «Rechazar» lo devuelve. En un ejercicio cerrado no se puede aprobar.') },

    // ------------------------------------------------------------------ tesorería, impuestos y demás
    tesoreria: { t: 'Tesorería', h: P('Cobros y pagos pendientes, conciliación bancaria y remesas SEPA.') +
      L('Los vencimientos salen de las facturas según su forma de pago.', '«Registrar cobro/pago» genera el asiento contra el banco elegido.',
        'Conciliación: importa el extracto (Norma 43) y empareja movimientos; las reglas por concepto lo hacen solas la próxima vez.',
        'Remesas: agrupa recibos (cobros) o transferencias (pagos) y genera el fichero SEPA para el banco.') },
    fiscal: { t: 'Declaraciones', h: P('Cuadro de las declaraciones del año: cada modelo con sus periodos y su estado.') +
      L('Pulsa un periodo para abrir el modelo en su impreso.', 'En el menú tienes un acceso directo a cada modelo.',
        'Los periodos calculados y los presentados se distinguen por su color.') },
    'fiscal-modelo': { t: 'Modelo', h: P('El modelo en el formato del impreso oficial.') +
      L('Elige el periodo: «Vista previa» calcula sin guardar y «Calcular y guardar» guarda la declaración con las casillas sacadas de la contabilidad.',
        'Puedes escribir en una casilla a mano: se marca en color y los totales se recalculan. ↺ recupera el valor calculado.',
        '«Corregir en el periodo siguiente» lleva una diferencia a la declaración del mes o trimestre siguiente.',
        'Al terminar: «Descargar fichero» para la sede de la AEAT, «Marcar como presentada» con su justificante y «Contabilizar» la liquidación.',
        'Una declaración presentada ya no se recalcula; para cambiarla, complementaria o sustitutiva.') },
    'fiscal-libros': { t: 'Libros registro', h: P('Libros registro de IVA/IGIC de facturas emitidas y recibidas.') + L('Elige el periodo y pulsa «Ver».', 'Descárgalos en CSV o en el formato de la AEAT (XLSX, solo IVA).') },
    'fiscal-sii': { t: 'SII', h: P('Suministro Inmediato de Información: XML de las facturas emitidas y recibidas para enviar a la AEAT.') +
      L('Solo para empresas en el SII (Configuración fiscal).', 'Revisa las facturas pendientes de enviar y genera el lote.') },
    'fiscal-config': { t: 'Configuración fiscal', h: P('Régimen de la empresa y datos para las declaraciones.') +
      L('Régimen de IVA (general, criterio de caja, recargo de equivalencia, simplificado, REAGP…), prorrata y SII.',
        'IRPF (estimación directa o módulos) e Impuesto sobre Sociedades: tipo de gravamen y modalidad del 202.',
        'Qué modelos presenta la empresa y con qué periodicidad.') },
    inmovilizado: { t: 'Inmovilizado', h: P('Elementos del inmovilizado y su amortización.') +
      L('Método: lineal, degresivo, suma de dígitos, coeficiente de tablas o sin amortizar.', 'La ficha muestra el cuadro de amortización y la diferencia contable/fiscal.',
        'Genera los asientos de amortización del periodo; las bajas y ventas calculan su resultado.') },
    balances: { t: 'Balances', h: P('Informes de la contabilidad. Elige el informe, el formato y el canal.') +
      L('El formato «Según el plan de la empresa» usa el modelo normal o el de PYMES/abreviado.',
        'Pulsa una partida para ver sus cuentas.',
        'Qué cuentas van a cada partida se configura en Administración → Legislación → Composición de los balances.') },
    'balances:sumas-saldos': { t: 'Sumas y saldos', h: P('Debe, haber y saldo de cada cuenta en el periodo, al nivel que elijas (grupo, subgrupo, cuenta o subcuenta).') },
    'balances:plan-cuentas': { t: 'Plan de cuentas con saldos', h: P('Todo el plan con saldo anterior, debe, haber y saldo final de cada cuenta.') + L('Puedes mostrar solo las cuentas con saldo.') },
    'balances:patrimonio-neto': { t: 'Cambios en el patrimonio neto', h: P('Estado de cambios en el patrimonio neto del ejercicio (modelo normal). Avisa si su saldo final no coincide con el del balance.') },
    'balances:flujos-efectivo': { t: 'Flujos de efectivo', h: P('Estado de flujos de efectivo a partir de los movimientos de tesorería. No es obligatorio en PYMES ni en cuentas abreviadas.') },
    'balances:memoria': { t: 'Memoria', h: P('Borrador de la memoria con los datos de la empresa y las cifras del balance y la PyG; revísalo y complétalo antes de depositar las cuentas.') },
    analitica: { t: 'Analítica', h: P('Contabilidad analítica por dimensiones (centros de coste, proyectos…).') +
      L('Define dimensiones y elementos y el reparto por defecto de cada cuenta.', '«Apuntes sin imputar» lista los que faltan por repartir.',
        'El informe muestra los importes por elemento, con o sin las cuentas.') },
    presupuestos: { t: 'Presupuestos', h: P('Presupuestos del ejercicio por cuenta y su comparación con lo real.') +
      L('Crea un presupuesto y añade líneas por cuenta o prefijo (62 = todas las 62x); ingresos y gastos se escriben en positivo.',
        '«Generar» propone las líneas a partir de un ejercicio con un incremento % (sustituye las que haya).',
        'Estados: borrador, aprobado y cerrado; «Nueva versión» parte de uno aprobado.',
        '«Comparar» muestra presupuesto, real, desviación y % de ejecución hasta una fecha.') },
    cierre: { t: 'Cierre', h: P('Cierre del ejercicio y aplicación del resultado.') +
      L('«Cerrar el ejercicio» hace la regularización, el traspaso de los grupos 8 y 9, el cierre y la apertura del siguiente.',
        '«Deshacer el cierre» lo revierte si necesitas corregir algo.',
        'Aplicación del resultado: reparte el resultado del ejercicio anterior (reservas, dividendos, compensación de pérdidas…), previsualiza y contabiliza.') },
  };

  // Qué pantalla está a la vista: vista, botón activo del menú y, en algunas, el informe o apartado elegido
  function contexto() {
    const vis = id => { const e = document.getElementById(id); return e && !e.hidden; };
    for (const v of ['login', 'cambio', 'empresas']) if (vis('vista-' + v)) return [v];
    if (vis('vista-admin')) {
      const t = document.querySelector('#navAdmin button[aria-pressed="true"]')?.dataset.tab || 'usuarios';
      const r = ['admin:' + t];
      if (t === 'legislacion') r.unshift(`admin:legislacion:${document.getElementById('lgTipo').value}`);
      return r;
    }
    if (vis('vista-app')) {
      const t = document.querySelector('#navApp button[aria-pressed="true"]')?.dataset.tab;
      if (!t) return [];
      if (t.startsWith('fiscal-m-')) return ['fiscal-modelo'];
      if (t === 'balances') return ['balances:' + document.getElementById('baInforme').value, 'balances'];
      return [t];
    }
    return [];
  }

  function pinta() {
    const ctx = contexto();
    const c = ctx.map(k => AYUDA[k]).find(Boolean);
    const general = ctx.length > 1 && AYUDA[ctx[ctx.length - 1]] !== c ? AYUDA[ctx[ctx.length - 1]] : null;
    const modelo = document.querySelector('#navApp button[aria-pressed="true"]')?.dataset.tab?.slice(9);
    document.getElementById('ayTitulo').textContent = c ? (ctx[0] === 'fiscal-modelo' && modelo ? `Modelo ${modelo}` : c.t) : 'Ayuda';
    document.getElementById('ayCuerpo').innerHTML = (c ? c.h : P('No hay ayuda para esta pantalla.')) +
      (general ? H(general.t) + general.h : '') +
      H('En toda la aplicación') + L(`${K('F1')} abre y cierra esta ayuda; ${K('Esc')} la cierra.`,
        'El menú de la izquierda agrupa las pantallas; en el móvil se abre con «Menú».',
        'Lo que no te deja hacer tu permiso aparece oculto o solo de consulta.');
  }

  const panel = () => document.getElementById('ayuda');
  function abre() { pinta(); panel().hidden = false; document.getElementById('btnAyuda').setAttribute('aria-expanded', 'true'); document.getElementById('ayCerrar').focus(); }
  function cierra() { panel().hidden = true; document.getElementById('btnAyuda').setAttribute('aria-expanded', 'false'); }
  const alterna = () => panel().hidden ? abre() : cierra();

  document.getElementById('btnAyuda').addEventListener('click', alterna);
  document.getElementById('ayCerrar').addEventListener('click', cierra);
  document.addEventListener('keydown', e => {
    if (e.key === 'F1') { e.preventDefault(); alterna(); }
    else if (e.key === 'Escape' && !panel().hidden) cierra();
  });
  // Si está abierta, sigue a la pantalla: se repinta cuando cambia la vista, el botón activo del menú o el informe o apartado elegido
  let pendiente = 0;
  const repinta = () => { if (!panel().hidden && !pendiente) pendiente = setTimeout(() => { pendiente = 0; pinta(); }, 50); };
  new MutationObserver(repinta).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['hidden', 'aria-pressed'] });
  document.addEventListener('change', e => { if (e.target.id === 'baInforme' || e.target.id === 'lgTipo') repinta(); });
})();
