/* =====================================================================
   APP DE CONTABILIDAD MULTIEMPRESA - Esquema PostgreSQL (14+)
   Stack: .NET 10 + Dapper + Autofac + Serilog

   Convenciones
   - snake_case en español. En Dapper: DefaultTypeMap.MatchNamesWithUnderscores = true
   - PK bigint identity. Importes numeric(18,2). Fechas date / timestamptz.
   - Todo lo "de negocio" lleva empresa_id (multiempresa en una sola BD).
   - Esquemas: cat (catálogos globales), core (empresas/NIF/actividades/ejercicios),
     canal (canales de apuntes), conta (plan, diarios, asientos, apuntes, terceros),
     fiscal (IVA/IGIC, retenciones, prorrata, modelos), tes (tesorería),
     inmov (inmovilizado y bienes de inversión), gest (analítica, presupuestos, balances)
   ===================================================================== */

CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE SCHEMA IF NOT EXISTS cat;
CREATE SCHEMA IF NOT EXISTS core;
CREATE SCHEMA IF NOT EXISTS canal;
CREATE SCHEMA IF NOT EXISTS conta;
CREATE SCHEMA IF NOT EXISTS fiscal;
CREATE SCHEMA IF NOT EXISTS tes;
CREATE SCHEMA IF NOT EXISTS inmov;
CREATE SCHEMA IF NOT EXISTS gest;

/* =====================================================================
   1. CATÁLOGOS GLOBALES (cat)
   ===================================================================== */

CREATE TABLE cat.pais (
  id          smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  iso2        char(2)      NOT NULL UNIQUE,
  iso3        char(3)      NOT NULL UNIQUE,
  nombre      varchar(100) NOT NULL,
  es_ue       boolean      NOT NULL DEFAULT false,
  prefijo_vies varchar(2)
);

CREATE TABLE cat.provincia (
  id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  pais_id     smallint     NOT NULL REFERENCES cat.pais(id),
  codigo      varchar(4)   NOT NULL,              -- código INE (35 Las Palmas, 38 S.C. Tenerife...)
  nombre      varchar(100) NOT NULL,
  comunidad_autonoma varchar(80),
  -- territorio fiscal: determina IVA vs IGIC y administración
  territorio_fiscal varchar(15) NOT NULL DEFAULT 'COMUN'
    CHECK (territorio_fiscal IN ('COMUN','CANARIAS','CEUTA','MELILLA','NAVARRA','ALAVA','BIZKAIA','GIPUZKOA','EXTRANJERO')),
  UNIQUE (pais_id, codigo)
);

CREATE TABLE cat.banco (
  id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  codigo_entidad varchar(4),                      -- 4 dígitos del IBAN español
  bic            varchar(11),
  nombre         varchar(150) NOT NULL,
  pais_id        smallint REFERENCES cat.pais(id),
  activo         boolean NOT NULL DEFAULT true
);
CREATE UNIQUE INDEX ux_banco_entidad ON cat.banco(pais_id, codigo_entidad) WHERE codigo_entidad IS NOT NULL;

CREATE TABLE cat.delegacion_hacienda (
  id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  administracion varchar(20) NOT NULL DEFAULT 'AEAT'
    CHECK (administracion IN ('AEAT','ATC','HACIENDA_FORAL','OTRA')),  -- ATC = Agencia Tributaria Canaria
  codigo         varchar(10)  NOT NULL,
  nombre         varchar(150) NOT NULL,
  provincia_id   integer REFERENCES cat.provincia(id),
  direccion      varchar(200),
  UNIQUE (administracion, codigo)
);

CREATE TABLE cat.epigrafe_iae (
  id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  seccion      smallint NOT NULL CHECK (seccion IN (1,2,3)),  -- 1 empresarial, 2 profesional, 3 artistas
  codigo       varchar(10)  NOT NULL,
  descripcion  varchar(300) NOT NULL,
  es_profesional boolean NOT NULL DEFAULT false,  -- true => aplica retención IRPF en facturas emitidas
  UNIQUE (seccion, codigo)
);

-- Tipos de IVA / IGIC (con vigencia por fechas)
CREATE TABLE cat.tipo_iva (
  id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  impuesto  varchar(6)  NOT NULL DEFAULT 'IVA' CHECK (impuesto IN ('IVA','IGIC','IPSI')),
  codigo    varchar(20) NOT NULL UNIQUE,          -- IVA_GEN, IVA_RED, IVA_SUP, IVA_EXE, IGIC_GEN...
  nombre    varchar(100) NOT NULL,
  es_exento boolean NOT NULL DEFAULT false,
  activo    boolean NOT NULL DEFAULT true
);

CREATE TABLE cat.tipo_iva_vigencia (
  id                    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tipo_iva_id           integer NOT NULL REFERENCES cat.tipo_iva(id),
  porcentaje            numeric(5,2) NOT NULL,
  recargo_equivalencia  numeric(5,2) NOT NULL DEFAULT 0,
  fecha_desde           date NOT NULL,            -- fecha de activación
  fecha_hasta           date,                     -- NULL = vigente
  EXCLUDE USING gist (tipo_iva_id WITH =, daterange(fecha_desde, fecha_hasta, '[)') WITH &&)
);

-- Tipos de retención IRPF (con vigencia)
CREATE TABLE cat.tipo_retencion_irpf (
  id            integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  codigo        varchar(20)  NOT NULL UNIQUE,
  nombre        varchar(150) NOT NULL,
  naturaleza    varchar(20)  NOT NULL DEFAULT 'PROFESIONAL'
    CHECK (naturaleza IN ('PROFESIONAL','ARRENDAMIENTO','CAPITAL','AGRICOLA','TRABAJO','OTRA')),
  modelo_ingreso varchar(10),                     -- 111, 115...
  clave_resumen  varchar(5),                      -- clave en 190 / 180
  activo        boolean NOT NULL DEFAULT true
);

CREATE TABLE cat.tipo_retencion_irpf_vigencia (
  id            integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tipo_retencion_id integer NOT NULL REFERENCES cat.tipo_retencion_irpf(id),
  porcentaje    numeric(5,2) NOT NULL,
  fecha_desde   date NOT NULL,
  fecha_hasta   date,
  EXCLUDE USING gist (tipo_retencion_id WITH =, daterange(fecha_desde, fecha_hasta, '[)') WITH &&)
);

-- Planes contables modelo (Normal, PYMES...) que se copian a la empresa al crearla
CREATE TABLE cat.plan_modelo (
  id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  codigo      varchar(20)  NOT NULL UNIQUE,       -- PGC_NORMAL, PGC_PYMES
  nombre      varchar(150) NOT NULL,
  tipo        varchar(15)  NOT NULL CHECK (tipo IN ('NORMAL','PYMES','PERSONALIZADO')),
  version     varchar(20),
  vigente_desde date
);

CREATE TABLE cat.cuenta_modelo (
  id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  plan_modelo_id integer NOT NULL REFERENCES cat.plan_modelo(id),
  codigo         varchar(12)  NOT NULL,           -- sin ceros de relleno: 4, 40, 400, 4000...
  nombre         varchar(200) NOT NULL,
  nivel          smallint     NOT NULL,           -- 1 grupo, 2 subgrupo, 3 cuenta, 4 subcuenta
  padre_codigo   varchar(12),
  naturaleza     varchar(10)  NOT NULL DEFAULT 'MIXTA' CHECK (naturaleza IN ('DEUDORA','ACREEDORA','MIXTA')),
  UNIQUE (plan_modelo_id, codigo)
);

/* =====================================================================
   2. NÚCLEO: NIF, EMPRESAS, ACTIVIDADES, EJERCICIOS (core)
   ===================================================================== */

-- Tabla única de NIF para todas las empresas (identidad fiscal compartida)
CREATE TABLE core.nif (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  pais_id        smallint NOT NULL REFERENCES cat.pais(id),
  tipo_documento varchar(10) NOT NULL DEFAULT 'NIF'
    CHECK (tipo_documento IN ('NIF','NIE','CIF','PASAPORTE','VIES','OTRO')),
  numero         varchar(20) NOT NULL,
  razon_social   varchar(200) NOT NULL,
  nombre_comercial varchar(200),
  es_persona_fisica boolean NOT NULL DEFAULT false,
  direccion      varchar(250),
  codigo_postal  varchar(10),
  poblacion      varchar(100),
  provincia_id   integer REFERENCES cat.provincia(id),
  email          varchar(200),
  telefono       varchar(30),
  verificado_vies boolean,
  creado_en      timestamptz NOT NULL DEFAULT now(),
  UNIQUE (pais_id, numero)
);

CREATE TABLE core.usuario (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  email         varchar(200) NOT NULL UNIQUE,
  nombre        varchar(150) NOT NULL,
  password_hash varchar(255),
  activo        boolean NOT NULL DEFAULT true,
  creado_en     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE core.empresa (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  nif_id          bigint NOT NULL UNIQUE REFERENCES core.nif(id),
  codigo          varchar(10)  NOT NULL UNIQUE,
  nombre          varchar(200) NOT NULL,
  forma_juridica  varchar(20)  NOT NULL
    CHECK (forma_juridica IN ('AUTONOMO','SL','SA','SLU','COMUNIDAD_BIENES','SOCIEDAD_CIVIL','COOPERATIVA','ASOCIACION','OTRA')),
  plan_modelo_id  integer NOT NULL REFERENCES cat.plan_modelo(id),   -- Normal o PYMES
  digitos_cuenta  smallint NOT NULL DEFAULT 8 CHECK (digitos_cuenta BETWEEN 6 AND 12),
  impuesto_indirecto varchar(10) NOT NULL DEFAULT 'IVA' CHECK (impuesto_indirecto IN ('IVA','IGIC','IPSI','EXENTO')),
  regimen_iva     varchar(25) NOT NULL DEFAULT 'GENERAL'
    CHECK (regimen_iva IN ('GENERAL','SIMPLIFICADO','RECARGO_EQUIVALENCIA','AGRICULTURA_GYP','CRITERIO_CAJA','EXENTO')),
  regimen_irpf    varchar(25) CHECK (regimen_irpf IN ('DIRECTA_NORMAL','DIRECTA_SIMPLIFICADA','OBJETIVA')),
  tributa_is      boolean NOT NULL DEFAULT false,         -- Impuesto de Sociedades
  delegacion_hacienda_id integer REFERENCES cat.delegacion_hacienda(id),
  moneda          char(3) NOT NULL DEFAULT 'EUR',
  fecha_alta      date NOT NULL DEFAULT current_date,
  activa          boolean NOT NULL DEFAULT true,
  creado_en       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE core.usuario_empresa (
  usuario_id bigint NOT NULL REFERENCES core.usuario(id),
  empresa_id bigint NOT NULL REFERENCES core.empresa(id),
  rol        varchar(20) NOT NULL DEFAULT 'LECTURA' CHECK (rol IN ('ADMIN','CONTABLE','LECTURA')),
  PRIMARY KEY (usuario_id, empresa_id)
);

-- Actividades de la empresa (epígrafes IAE). Base para retención IRPF, sectores diferenciados y prorrata
CREATE TABLE core.empresa_actividad (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint  NOT NULL REFERENCES core.empresa(id),
  epigrafe_id   integer NOT NULL REFERENCES cat.epigrafe_iae(id),
  nombre        varchar(200),
  es_principal  boolean NOT NULL DEFAULT false,
  sector_diferenciado smallint,                 -- NULL = sin sector diferenciado; agrupa actividades para prorrata
  regimen_iva_especial varchar(25),
  fecha_alta    date NOT NULL DEFAULT current_date,
  fecha_baja    date
);
CREATE UNIQUE INDEX ux_actividad_principal ON core.empresa_actividad(empresa_id)
  WHERE es_principal AND fecha_baja IS NULL;

-- Ejercicio: fechas libres => soporta ejercicio partido (ej. 01/07/2025 - 30/06/2026)
CREATE TABLE core.ejercicio (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id   bigint NOT NULL REFERENCES core.empresa(id),
  codigo       varchar(10) NOT NULL,            -- '2025' o '2025/26'
  fecha_inicio date NOT NULL,
  fecha_fin    date NOT NULL,
  es_partido   boolean GENERATED ALWAYS AS (EXTRACT(YEAR FROM fecha_inicio) <> EXTRACT(YEAR FROM fecha_fin)) STORED,
  estado       varchar(12) NOT NULL DEFAULT 'ABIERTO' CHECK (estado IN ('ABIERTO','CERRADO','BLOQUEADO')),
  CHECK (fecha_fin > fecha_inicio),
  UNIQUE (empresa_id, codigo),
  EXCLUDE USING gist (empresa_id WITH =, daterange(fecha_inicio, fecha_fin, '[]') WITH &&)
);

CREATE TABLE core.periodo (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  ejercicio_id bigint NOT NULL REFERENCES core.ejercicio(id),
  numero       smallint NOT NULL,               -- 0 apertura, 1..12 meses, 13 regularización, 14 cierre
  tipo         varchar(15) NOT NULL DEFAULT 'NORMAL' CHECK (tipo IN ('APERTURA','NORMAL','REGULARIZACION','CIERRE')),
  fecha_inicio date NOT NULL,
  fecha_fin    date NOT NULL,
  estado       varchar(10) NOT NULL DEFAULT 'ABIERTO' CHECK (estado IN ('ABIERTO','CERRADO')),
  UNIQUE (ejercicio_id, numero)
);

CREATE TABLE core.forma_pago (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  codigo      varchar(10) NOT NULL,
  nombre      varchar(100) NOT NULL,
  tipo        varchar(15) NOT NULL DEFAULT 'TRANSFERENCIA'
    CHECK (tipo IN ('EFECTIVO','TRANSFERENCIA','RECIBO','TARJETA','PAGARE','CHEQUE','OTRO')),
  num_plazos  smallint NOT NULL DEFAULT 1,
  dias_primer_plazo smallint NOT NULL DEFAULT 0,
  dias_entre_plazos smallint NOT NULL DEFAULT 0,
  UNIQUE (empresa_id, codigo)
);

/* =====================================================================
   3. CANALES DE APUNTES (canal)
   Canales BASE: 1, 2, 3 (cada uno es una "contabilidad" paralela de la empresa).
   Canales DERIVADOS: A=123, B=12, C=13, D=23 y los definidos por el usuario (E, F...)
   con reparto por porcentaje o fórmula. Cada cambio de reparto = nueva versión con fecha.
   Al grabar un apunte se materializa el reparto en conta.apunte_canal.
   ===================================================================== */

CREATE TABLE canal.canal (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id bigint NOT NULL REFERENCES core.empresa(id),
  codigo     varchar(5)   NOT NULL,               -- '1','2','3','A','B','E'...
  nombre     varchar(100) NOT NULL,
  tipo       varchar(10)  NOT NULL CHECK (tipo IN ('BASE','DERIVADO')),
  activo     boolean NOT NULL DEFAULT true,
  UNIQUE (empresa_id, codigo),
  UNIQUE (id, tipo)                               -- permite FKs compuestas que fuerzan el tipo
);

-- Versiones de la fórmula de un canal DERIVADO (inmutables: se crea una nueva, no se edita)
CREATE TABLE canal.canal_version (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  canal_id      bigint NOT NULL,
  tipo_canal    varchar(10) NOT NULL DEFAULT 'DERIVADO' CHECK (tipo_canal = 'DERIVADO'),
  vigente_desde date NOT NULL,                    -- la versión aplicable es la última con vigente_desde <= fecha del asiento
  descripcion   varchar(300),
  creado_en     timestamptz NOT NULL DEFAULT now(),
  creado_por    bigint REFERENCES core.usuario(id),
  UNIQUE (canal_id, vigente_desde),
  FOREIGN KEY (canal_id, tipo_canal) REFERENCES canal.canal(id, tipo)
);

-- Líneas: a qué canales BASE va y cómo se calcula el importe de cada uno
--   A=123 => 3 líneas PORCENTAJE 100 | E => ej. canal 1 = 60%, canal 2 = RESTO
CREATE TABLE canal.canal_version_linea (
  id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  canal_version_id  bigint NOT NULL REFERENCES canal.canal_version(id),
  orden             smallint NOT NULL DEFAULT 1,
  canal_base_id     bigint NOT NULL,
  tipo_base         varchar(10) NOT NULL DEFAULT 'BASE' CHECK (tipo_base = 'BASE'),
  modo              varchar(12) NOT NULL DEFAULT 'PORCENTAJE' CHECK (modo IN ('PORCENTAJE','FORMULA','RESTO')),
  porcentaje        numeric(9,6) DEFAULT 100,
  -- Variables disponibles en la fórmula: IMPORTE, DEBE, HABER, CUENTA, FECHA ... (evaluar con NCalc/DynamicExpresso)
  expresion         text,
  decimales         smallint NOT NULL DEFAULT 2,
  CHECK ((modo = 'PORCENTAJE' AND porcentaje IS NOT NULL)
      OR (modo = 'FORMULA'    AND expresion IS NOT NULL)
      OR  modo = 'RESTO'),
  UNIQUE (canal_version_id, canal_base_id),
  FOREIGN KEY (canal_base_id, tipo_base) REFERENCES canal.canal(id, tipo)
);

CREATE FUNCTION canal.version_vigente(p_canal_id bigint, p_fecha date)
RETURNS bigint LANGUAGE sql STABLE AS $$
  SELECT id FROM canal.canal_version
  WHERE canal_id = p_canal_id AND vigente_desde <= p_fecha
  ORDER BY vigente_desde DESC LIMIT 1
$$;

/* =====================================================================
   4. CONTABILIDAD (conta)
   ===================================================================== */

CREATE TABLE conta.cuenta (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id      bigint NOT NULL REFERENCES core.empresa(id),
  codigo          varchar(15)  NOT NULL,
  nombre          varchar(200) NOT NULL,
  nivel           smallint     NOT NULL,
  padre_id        bigint REFERENCES conta.cuenta(id),
  naturaleza      varchar(10)  NOT NULL DEFAULT 'MIXTA' CHECK (naturaleza IN ('DEUDORA','ACREEDORA','MIXTA')),
  es_imputable    boolean NOT NULL DEFAULT true,   -- true = subcuenta que admite apuntes
  requiere_tercero   boolean NOT NULL DEFAULT false,
  requiere_analitica boolean NOT NULL DEFAULT false,
  cuenta_modelo_id integer REFERENCES cat.cuenta_modelo(id),
  activa          boolean NOT NULL DEFAULT true,
  UNIQUE (empresa_id, codigo)
);
CREATE INDEX ix_cuenta_padre ON conta.cuenta(padre_id);

-- Cuentas "por rol" para que las plantillas y la contabilización automática sepan dónde asentar
CREATE TABLE conta.cuenta_automatica (
  id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id        bigint NOT NULL REFERENCES core.empresa(id),
  rol               varchar(40) NOT NULL,          -- IVA_SOPORTADO, IVA_REPERCUTIDO, RECARGO_EQUIV, RETENCION_PRACTICADA,
                                                   -- RETENCION_SOPORTADA, HP_ACREEDORA_IVA, HP_DEUDORA_IVA, COMPRAS, VENTAS, ...
  tipo_iva_id       integer REFERENCES cat.tipo_iva(id),
  tipo_retencion_id integer REFERENCES cat.tipo_retencion_irpf(id),
  cuenta_id         bigint NOT NULL REFERENCES conta.cuenta(id)
);
CREATE UNIQUE INDEX ux_cuenta_automatica ON conta.cuenta_automatica
  (empresa_id, rol, COALESCE(tipo_iva_id,0), COALESCE(tipo_retencion_id,0));

CREATE TABLE conta.diario (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id bigint NOT NULL REFERENCES core.empresa(id),
  codigo     varchar(10)  NOT NULL,
  nombre     varchar(100) NOT NULL,
  tipo       varchar(20)  NOT NULL DEFAULT 'GENERAL'
    CHECK (tipo IN ('GENERAL','COMPRAS','VENTAS','BANCOS','CAJA','NOMINAS','AMORTIZACION','APERTURA','REGULARIZACION','CIERRE','AJUSTES')),
  activo     boolean NOT NULL DEFAULT true,
  UNIQUE (empresa_id, codigo)
);

-- Clientes y proveedores (maestro por empresa; la identidad fiscal vive en core.nif)
CREATE TABLE conta.tercero (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id      bigint NOT NULL REFERENCES core.empresa(id),
  nif_id          bigint NOT NULL REFERENCES core.nif(id),
  codigo          varchar(15) NOT NULL,
  nombre          varchar(200) NOT NULL,
  es_cliente      boolean NOT NULL DEFAULT false,
  es_proveedor    boolean NOT NULL DEFAULT false,
  es_acreedor     boolean NOT NULL DEFAULT false,
  cuenta_cliente_id   bigint REFERENCES conta.cuenta(id),   -- 430xxxx
  cuenta_proveedor_id bigint REFERENCES conta.cuenta(id),   -- 400xxxx / 410xxxx
  forma_pago_id   bigint REFERENCES core.forma_pago(id),
  tipo_iva_defecto_id integer REFERENCES cat.tipo_iva(id),
  tipo_retencion_defecto_id integer REFERENCES cat.tipo_retencion_irpf(id),
  recargo_equivalencia boolean NOT NULL DEFAULT false,
  direccion       varchar(250),
  email           varchar(200),
  telefono        varchar(30),
  activo          boolean NOT NULL DEFAULT true,
  UNIQUE (empresa_id, codigo),
  UNIQUE (empresa_id, nif_id)
);

CREATE TABLE conta.asiento (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  ejercicio_id  bigint NOT NULL REFERENCES core.ejercicio(id),
  periodo_id    bigint NOT NULL REFERENCES core.periodo(id),
  diario_id     bigint NOT NULL REFERENCES conta.diario(id),
  numero        integer NOT NULL,
  fecha         date NOT NULL,
  concepto      varchar(250) NOT NULL,
  estado        varchar(14) NOT NULL DEFAULT 'BORRADOR' CHECK (estado IN ('BORRADOR','CONTABILIZADO','ANULADO')),
  origen        varchar(20) NOT NULL DEFAULT 'MANUAL'
    CHECK (origen IN ('MANUAL','PLANTILLA','FACTURA','TESORERIA','AMORTIZACION','IVA','APERTURA','REGULARIZACION','CIERRE','IMPORTACION')),
  plantilla_id  bigint,                           -- FK añadida más abajo
  documento     varchar(40),
  asiento_origen_id bigint REFERENCES conta.asiento(id),    -- para anulaciones / extornos
  creado_por    bigint REFERENCES core.usuario(id),
  creado_en     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (empresa_id, ejercicio_id, diario_id, numero)
);
CREATE INDEX ix_asiento_fecha ON conta.asiento(empresa_id, ejercicio_id, fecha);

-- Numeración correlativa por empresa / ejercicio / diario (UPDATE ... RETURNING en una transacción)
CREATE TABLE conta.contador_asiento (
  empresa_id   bigint NOT NULL REFERENCES core.empresa(id),
  ejercicio_id bigint NOT NULL REFERENCES core.ejercicio(id),
  diario_id    bigint NOT NULL REFERENCES conta.diario(id),
  ultimo_numero integer NOT NULL DEFAULT 0,
  PRIMARY KEY (empresa_id, ejercicio_id, diario_id)
);

CREATE TABLE conta.apunte (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  asiento_id  bigint NOT NULL REFERENCES conta.asiento(id) ON DELETE CASCADE,
  linea       smallint NOT NULL,
  cuenta_id   bigint NOT NULL REFERENCES conta.cuenta(id),
  tercero_id  bigint REFERENCES conta.tercero(id),
  actividad_id bigint REFERENCES core.empresa_actividad(id),
  canal_id    bigint NOT NULL REFERENCES canal.canal(id),    -- canal elegido al teclear: 1,2,3,A,B,E...
  debe        numeric(18,2) NOT NULL DEFAULT 0,
  haber       numeric(18,2) NOT NULL DEFAULT 0,
  concepto    varchar(250),
  documento   varchar(40),
  fecha_valor date,
  CHECK (NOT (debe <> 0 AND haber <> 0)),
  UNIQUE (asiento_id, linea)
);
CREATE INDEX ix_apunte_cuenta ON conta.apunte(empresa_id, cuenta_id);
CREATE INDEX ix_apunte_tercero ON conta.apunte(tercero_id) WHERE tercero_id IS NOT NULL;

-- Reparto materializado por canal BASE (de aquí salen los mayores, balances y presupuestos por canal)
-- Canal base directo => 1 fila al 100%. Canal A => 3 filas con el importe completo. Canal E => reparto según fórmula.
CREATE TABLE conta.apunte_canal (
  apunte_id        bigint NOT NULL REFERENCES conta.apunte(id) ON DELETE CASCADE,
  canal_base_id    bigint NOT NULL,
  tipo_base        varchar(10) NOT NULL DEFAULT 'BASE' CHECK (tipo_base = 'BASE'),
  canal_version_id bigint REFERENCES canal.canal_version(id),   -- versión de fórmula usada (auditoría); NULL si canal base directo
  debe             numeric(18,2) NOT NULL DEFAULT 0,
  haber            numeric(18,2) NOT NULL DEFAULT 0,
  PRIMARY KEY (apunte_id, canal_base_id),
  FOREIGN KEY (canal_base_id, tipo_base) REFERENCES canal.canal(id, tipo)
);
CREATE INDEX ix_apunte_canal_canal ON conta.apunte_canal(canal_base_id, apunte_id);

-- Un asiento contabilizado debe cuadrar en CADA canal base (devuelve los canales descuadrados)
CREATE FUNCTION conta.validar_asiento(p_asiento_id bigint)
RETURNS TABLE (canal_base_id bigint, debe numeric, haber numeric) LANGUAGE sql STABLE AS $$
  SELECT ac.canal_base_id, SUM(ac.debe), SUM(ac.haber)
  FROM conta.apunte a
  JOIN conta.apunte_canal ac ON ac.apunte_id = a.id
  WHERE a.asiento_id = p_asiento_id
  GROUP BY ac.canal_base_id
  HAVING SUM(ac.debe) <> SUM(ac.haber)
$$;

-- Mayor por canal base
CREATE VIEW conta.v_mayor AS
SELECT s.empresa_id, s.ejercicio_id, s.periodo_id, s.fecha, s.id AS asiento_id, s.numero, s.diario_id,
       a.id AS apunte_id, a.cuenta_id, a.tercero_id, a.actividad_id, a.concepto,
       ac.canal_base_id, ac.debe, ac.haber
FROM conta.asiento s
JOIN conta.apunte a        ON a.asiento_id = s.id
JOIN conta.apunte_canal ac ON ac.apunte_id = a.id
WHERE s.estado = 'CONTABILIZADO';

-- Asientos predefinidos (compras, ventas, cobros...). empresa_id NULL = plantilla del sistema
CREATE TABLE conta.plantilla_asiento (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id     bigint REFERENCES core.empresa(id),
  codigo         varchar(20)  NOT NULL,
  nombre         varchar(150) NOT NULL,
  tipo_operacion varchar(20)  NOT NULL
    CHECK (tipo_operacion IN ('COMPRA','VENTA','GASTO','COBRO','PAGO','NOMINA','AMORTIZACION','LIQUIDACION_IVA','INTRACOMUNITARIA','IMPORTACION','OTRO')),
  diario_id      bigint REFERENCES conta.diario(id),
  canal_id       bigint REFERENCES canal.canal(id),
  concepto_modelo varchar(250),                    -- admite marcadores: {tercero} {factura} {fecha}
  activa         boolean NOT NULL DEFAULT true
);
CREATE UNIQUE INDEX ux_plantilla ON conta.plantilla_asiento(COALESCE(empresa_id,0), codigo);

CREATE TABLE conta.plantilla_asiento_linea (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  plantilla_id  bigint NOT NULL REFERENCES conta.plantilla_asiento(id) ON DELETE CASCADE,
  orden         smallint NOT NULL,
  cuenta_id     bigint REFERENCES conta.cuenta(id),     -- cuenta fija...
  cuenta_rol    varchar(40),                            -- ...o resuelta por rol (cuenta_automatica / tercero)
  lado          varchar(5) NOT NULL CHECK (lado IN ('DEBE','HABER')),
  origen_importe varchar(20) NOT NULL
    CHECK (origen_importe IN ('BASE','CUOTA','RECARGO','RETENCION','TOTAL','CUOTA_NO_DEDUCIBLE','LIBRE','EXPRESION')),
  expresion     text,
  usa_tercero   boolean NOT NULL DEFAULT false,
  canal_id      bigint REFERENCES canal.canal(id),
  concepto      varchar(250),
  CHECK (cuenta_id IS NOT NULL OR cuenta_rol IS NOT NULL),
  UNIQUE (plantilla_id, orden)
);
ALTER TABLE conta.asiento ADD CONSTRAINT fk_asiento_plantilla
  FOREIGN KEY (plantilla_id) REFERENCES conta.plantilla_asiento(id);

/* =====================================================================
   5. FISCAL (fiscal): facturas/libros IVA-IGIC, retenciones, prorrata, modelos
   ===================================================================== */

CREATE TABLE fiscal.factura (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id      bigint NOT NULL REFERENCES core.empresa(id),
  ejercicio_id    bigint NOT NULL REFERENCES core.ejercicio(id),
  libro           varchar(10) NOT NULL CHECK (libro IN ('EMITIDAS','RECIBIDAS')),
  tipo_factura    varchar(20) NOT NULL DEFAULT 'ORDINARIA'
    CHECK (tipo_factura IN ('ORDINARIA','SIMPLIFICADA','RECTIFICATIVA','AUTOFACTURA','INTRACOMUNITARIA','IMPORTACION')),
  serie           varchar(10),
  numero          varchar(30) NOT NULL,           -- nº propio (emitidas) o nº del proveedor (recibidas)
  tercero_id      bigint NOT NULL REFERENCES conta.tercero(id),
  actividad_id    bigint REFERENCES core.empresa_actividad(id),
  canal_id        bigint REFERENCES canal.canal(id),
  plantilla_id    bigint REFERENCES conta.plantilla_asiento(id),
  asiento_id      bigint REFERENCES conta.asiento(id),
  fecha_expedicion date NOT NULL,
  fecha_operacion  date,
  fecha_contable   date NOT NULL,                 -- determina el periodo de liquidación
  descripcion     varchar(300),
  base_total      numeric(18,2) NOT NULL DEFAULT 0,
  cuota_total     numeric(18,2) NOT NULL DEFAULT 0,
  recargo_total   numeric(18,2) NOT NULL DEFAULT 0,
  retencion_total numeric(18,2) NOT NULL DEFAULT 0,
  total           numeric(18,2) NOT NULL DEFAULT 0,
  clave_operacion_347 char(1),                    -- A adquisiciones / B entregas
  criterio_caja   boolean NOT NULL DEFAULT false,
  factura_rectificada_id bigint REFERENCES fiscal.factura(id),
  estado          varchar(14) NOT NULL DEFAULT 'BORRADOR' CHECK (estado IN ('BORRADOR','CONTABILIZADA','ANULADA')),
  creado_en       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_factura_fecha ON fiscal.factura(empresa_id, libro, fecha_contable);
-- COALESCE: con serie NULL un UNIQUE normal permitiría duplicados
CREATE UNIQUE INDEX ux_factura_numero ON fiscal.factura(empresa_id, libro, tercero_id, COALESCE(serie,''), numero);

-- Desglose por tipo de IVA/IGIC. Aquí van recargo, prorrata y bien de inversión
CREATE TABLE fiscal.factura_iva (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  factura_id      bigint NOT NULL REFERENCES fiscal.factura(id) ON DELETE CASCADE,
  tipo_iva_id     integer NOT NULL REFERENCES cat.tipo_iva(id),
  tipo_iva_vigencia_id integer REFERENCES cat.tipo_iva_vigencia(id),   -- snapshot del % usado
  tipo_operacion  varchar(20) NOT NULL DEFAULT 'INTERIOR'
    CHECK (tipo_operacion IN ('INTERIOR','EXENTA','NO_SUJETA','INTRACOM_ADQUISICION','INTRACOM_ENTREGA','IMPORTACION','EXPORTACION','INVERSION_SUJETO_PASIVO')),
  base            numeric(18,2) NOT NULL,
  porcentaje      numeric(5,2)  NOT NULL,
  cuota           numeric(18,2) NOT NULL,
  porcentaje_recargo numeric(5,2) NOT NULL DEFAULT 0,
  cuota_recargo   numeric(18,2) NOT NULL DEFAULT 0,
  -- Deducibilidad (solo recibidas)
  regla_deduccion varchar(20) NOT NULL DEFAULT 'TOTAL'
    CHECK (regla_deduccion IN ('TOTAL','NINGUNA','PRORRATA','PORCENTAJE_AFECTACION')),
  porcentaje_deduccion numeric(5,2) NOT NULL DEFAULT 100,
  cuota_deducible numeric(18,2) NOT NULL DEFAULT 0,
  es_bien_inversion boolean NOT NULL DEFAULT false
);

-- Retenciones IRPF en factura (emitidas si la empresa tiene actividad profesional; recibidas si se retiene a un profesional)
CREATE TABLE fiscal.factura_retencion (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  factura_id    bigint NOT NULL REFERENCES fiscal.factura(id) ON DELETE CASCADE,
  tipo_retencion_id integer NOT NULL REFERENCES cat.tipo_retencion_irpf(id),
  vigencia_id   integer REFERENCES cat.tipo_retencion_irpf_vigencia(id),
  base          numeric(18,2) NOT NULL,
  porcentaje    numeric(5,2)  NOT NULL,
  importe       numeric(18,2) NOT NULL
);

-- Prorrata de IVA: se calcula por AÑO NATURAL (no por ejercicio) y por sector diferenciado
CREATE TABLE fiscal.prorrata (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  anio          smallint NOT NULL,
  sector_diferenciado smallint,
  tipo          varchar(10) NOT NULL DEFAULT 'GENERAL' CHECK (tipo IN ('GENERAL','ESPECIAL')),
  operaciones_con_derecho numeric(18,2),
  operaciones_totales     numeric(18,2),
  porcentaje_provisional  smallint CHECK (porcentaje_provisional BETWEEN 0 AND 100),  -- el del año anterior
  porcentaje_definitivo   smallint CHECK (porcentaje_definitivo   BETWEEN 0 AND 100),  -- redondeado al alza
  estado        varchar(12) NOT NULL DEFAULT 'PROVISIONAL' CHECK (estado IN ('PROVISIONAL','DEFINITIVA')),
  observaciones varchar(300)
);
CREATE UNIQUE INDEX ux_prorrata ON fiscal.prorrata(empresa_id, anio, COALESCE(sector_diferenciado,0));

-- Catálogo de modelos (estatales y canarios)
CREATE TABLE fiscal.modelo_fiscal (
  id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  administracion varchar(20) NOT NULL CHECK (administracion IN ('AEAT','ATC','HACIENDA_FORAL')),
  codigo         varchar(10) NOT NULL,
  nombre         varchar(200) NOT NULL,
  impuesto       varchar(20) NOT NULL,            -- IVA, IGIC, IRPF, IS, TERCEROS...
  periodicidades text[] NOT NULL DEFAULT '{}',    -- {MENSUAL,TRIMESTRAL,ANUAL}
  activo         boolean NOT NULL DEFAULT true,
  UNIQUE (administracion, codigo)
);

CREATE TABLE fiscal.modelo_casilla (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  modelo_id   integer NOT NULL REFERENCES fiscal.modelo_fiscal(id),
  casilla     varchar(10) NOT NULL,
  descripcion varchar(250) NOT NULL,
  orden       smallint NOT NULL DEFAULT 0,
  regla       jsonb,                              -- cómo se calcula (origen: factura_iva, cuentas, expresión...)
  UNIQUE (modelo_id, casilla)
);

CREATE TABLE fiscal.empresa_modelo (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id   bigint  NOT NULL REFERENCES core.empresa(id),
  modelo_id    integer NOT NULL REFERENCES fiscal.modelo_fiscal(id),
  periodicidad varchar(10) NOT NULL CHECK (periodicidad IN ('MENSUAL','TRIMESTRAL','ANUAL')),
  fecha_alta   date NOT NULL DEFAULT current_date,
  fecha_baja   date,
  UNIQUE (empresa_id, modelo_id, fecha_alta)
);

CREATE TABLE fiscal.declaracion (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint  NOT NULL REFERENCES core.empresa(id),
  modelo_id     integer NOT NULL REFERENCES fiscal.modelo_fiscal(id),
  ejercicio_id  bigint REFERENCES core.ejercicio(id),      -- solo relevante para IS (200/202)
  anio          smallint NOT NULL,                         -- año natural del periodo
  periodo       varchar(3) NOT NULL,                       -- 1T..4T, 01..12, 0A
  fecha_inicio  date NOT NULL,
  fecha_fin     date NOT NULL,
  numero_presentacion smallint NOT NULL DEFAULT 1,
  tipo_declaracion varchar(15) NOT NULL DEFAULT 'ORDINARIA' CHECK (tipo_declaracion IN ('ORDINARIA','COMPLEMENTARIA','SUSTITUTIVA')),
  estado        varchar(12) NOT NULL DEFAULT 'BORRADOR' CHECK (estado IN ('BORRADOR','CALCULADA','PRESENTADA','ANULADA')),
  resultado     numeric(18,2),
  a_ingresar    numeric(18,2),
  a_devolver    numeric(18,2),
  a_compensar   numeric(18,2),
  justificante  varchar(30),
  fecha_presentacion date,
  asiento_id    bigint REFERENCES conta.asiento(id),
  datos         jsonb,
  UNIQUE (empresa_id, modelo_id, anio, periodo, numero_presentacion)
);

CREATE TABLE fiscal.declaracion_casilla (
  declaracion_id bigint NOT NULL REFERENCES fiscal.declaracion(id) ON DELETE CASCADE,
  casilla        varchar(10) NOT NULL,
  importe        numeric(18,2),
  texto          varchar(250),
  PRIMARY KEY (declaracion_id, casilla)
);

/* =====================================================================
   6. TESORERÍA (tes)
   ===================================================================== */

CREATE TABLE tes.cuenta_bancaria (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  tercero_id  bigint REFERENCES conta.tercero(id),    -- NULL = cuenta propia; con valor = banco de cliente/proveedor
  banco_id    integer REFERENCES cat.banco(id),
  alias       varchar(100),
  iban        varchar(34) NOT NULL,
  bic         varchar(11),
  cuenta_contable_id bigint REFERENCES conta.cuenta(id),   -- 572xxxx
  activa      boolean NOT NULL DEFAULT true,
  UNIQUE (empresa_id, iban)
);

CREATE TABLE tes.vencimiento (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id     bigint NOT NULL REFERENCES core.empresa(id),
  tipo           varchar(5) NOT NULL CHECK (tipo IN ('COBRO','PAGO')),
  factura_id     bigint REFERENCES fiscal.factura(id),
  tercero_id     bigint NOT NULL REFERENCES conta.tercero(id),
  forma_pago_id  bigint REFERENCES core.forma_pago(id),
  cuenta_bancaria_id bigint REFERENCES tes.cuenta_bancaria(id),
  fecha_vencimiento date NOT NULL,
  importe        numeric(18,2) NOT NULL,
  importe_pendiente numeric(18,2) NOT NULL,
  estado         varchar(12) NOT NULL DEFAULT 'PENDIENTE' CHECK (estado IN ('PENDIENTE','PARCIAL','LIQUIDADO','ANULADO','REMESADO'))
);
CREATE INDEX ix_vencimiento ON tes.vencimiento(empresa_id, estado, fecha_vencimiento);

CREATE TABLE tes.movimiento_bancario (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  cuenta_bancaria_id bigint NOT NULL REFERENCES tes.cuenta_bancaria(id),
  fecha       date NOT NULL,
  fecha_valor date,
  importe     numeric(18,2) NOT NULL,             -- + ingreso / - cargo
  concepto    varchar(300),
  referencia  varchar(60),
  origen      varchar(10) NOT NULL DEFAULT 'MANUAL' CHECK (origen IN ('MANUAL','EXTRACTO')),
  conciliado  boolean NOT NULL DEFAULT false,
  asiento_id  bigint REFERENCES conta.asiento(id)
);

CREATE TABLE tes.cobro_pago (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  vencimiento_id bigint NOT NULL REFERENCES tes.vencimiento(id),
  movimiento_id bigint REFERENCES tes.movimiento_bancario(id),
  fecha         date NOT NULL,
  importe       numeric(18,2) NOT NULL,
  asiento_id    bigint REFERENCES conta.asiento(id)
);

/* =====================================================================
   7. INMOVILIZADO Y BIENES DE INVERSIÓN (inmov)
   ===================================================================== */

CREATE TABLE inmov.categoria (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  nombre        varchar(150) NOT NULL,
  tipo          varchar(20) NOT NULL DEFAULT 'MATERIAL' CHECK (tipo IN ('MATERIAL','INTANGIBLE','INVERSION_INMOBILIARIA')),
  cuenta_inmovilizado_id   bigint REFERENCES conta.cuenta(id),   -- 21x
  cuenta_amort_acumulada_id bigint REFERENCES conta.cuenta(id),  -- 281x
  cuenta_gasto_amort_id    bigint REFERENCES conta.cuenta(id),   -- 681x
  metodo_defecto varchar(15) NOT NULL DEFAULT 'LINEAL' CHECK (metodo_defecto IN ('LINEAL','DEGRESIVO','COEFICIENTE_TABLA','NO_AMORTIZA')),
  vida_util_meses_defecto integer
);

CREATE TABLE inmov.inmovilizado (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  categoria_id  bigint NOT NULL REFERENCES inmov.categoria(id),
  actividad_id  bigint REFERENCES core.empresa_actividad(id),
  canal_id      bigint REFERENCES canal.canal(id),
  codigo        varchar(20)  NOT NULL,
  descripcion   varchar(250) NOT NULL,
  factura_id    bigint REFERENCES fiscal.factura(id),
  proveedor_id  bigint REFERENCES conta.tercero(id),
  fecha_adquisicion date NOT NULL,
  fecha_inicio_amortizacion date NOT NULL,
  valor_adquisicion numeric(18,2) NOT NULL,
  valor_residual    numeric(18,2) NOT NULL DEFAULT 0,
  metodo        varchar(15) NOT NULL DEFAULT 'LINEAL',
  vida_util_meses integer,
  coeficiente   numeric(7,4),
  estado        varchar(10) NOT NULL DEFAULT 'ALTA' CHECK (estado IN ('ALTA','BAJA','VENDIDO')),
  fecha_baja    date,
  asiento_alta_id bigint REFERENCES conta.asiento(id),
  asiento_baja_id bigint REFERENCES conta.asiento(id),
  UNIQUE (empresa_id, codigo)
);

-- Cuadro de amortización: contable y fiscal por separado (para diferencias temporarias)
CREATE TABLE inmov.amortizacion (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  inmovilizado_id bigint NOT NULL REFERENCES inmov.inmovilizado(id),
  ejercicio_id  bigint NOT NULL REFERENCES core.ejercicio(id),
  periodo_id    bigint REFERENCES core.periodo(id),
  base_calculo  varchar(10) NOT NULL DEFAULT 'CONTABLE' CHECK (base_calculo IN ('CONTABLE','FISCAL')),
  importe       numeric(18,2) NOT NULL,
  acumulada     numeric(18,2) NOT NULL,
  asiento_id    bigint REFERENCES conta.asiento(id),
  UNIQUE (inmovilizado_id, ejercicio_id, periodo_id, base_calculo)
);

-- Bien de inversión (IVA/IGIC): regularización de deducciones durante 5 años (10 si inmueble)
CREATE TABLE inmov.bien_inversion (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  inmovilizado_id bigint NOT NULL UNIQUE REFERENCES inmov.inmovilizado(id),
  es_inmueble     boolean NOT NULL DEFAULT false,
  anio_inicio_utilizacion smallint NOT NULL,
  anios_regularizacion smallint NOT NULL DEFAULT 5 CHECK (anios_regularizacion IN (5,10)),
  cuota_soportada numeric(18,2) NOT NULL,
  cuota_deducida  numeric(18,2) NOT NULL,
  prorrata_inicial smallint NOT NULL DEFAULT 100
);

CREATE TABLE inmov.bien_inversion_regularizacion (
  id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  bien_inversion_id bigint NOT NULL REFERENCES inmov.bien_inversion(id),
  anio             smallint NOT NULL,
  prorrata_definitiva smallint NOT NULL,
  importe_regularizacion numeric(18,2) NOT NULL,   -- (+) deducir más / (-) reintegrar
  declaracion_id   bigint REFERENCES fiscal.declaracion(id),
  UNIQUE (bien_inversion_id, anio)
);

/* =====================================================================
   8. GESTIÓN: ANALÍTICA, PRESUPUESTOS, BALANCES (gest)
   ===================================================================== */

-- Dimensiones analíticas libres (centros de coste, proyectos, departamentos...)
CREATE TABLE gest.dimension_analitica (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id bigint NOT NULL REFERENCES core.empresa(id),
  codigo     varchar(10)  NOT NULL,
  nombre     varchar(100) NOT NULL,
  UNIQUE (empresa_id, codigo)
);

CREATE TABLE gest.elemento_analitico (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  dimension_id bigint NOT NULL REFERENCES gest.dimension_analitica(id),
  padre_id     bigint REFERENCES gest.elemento_analitico(id),
  codigo       varchar(20)  NOT NULL,
  nombre       varchar(150) NOT NULL,
  activo       boolean NOT NULL DEFAULT true,
  UNIQUE (dimension_id, codigo)
);

CREATE TABLE gest.apunte_analitica (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  apunte_id   bigint NOT NULL REFERENCES conta.apunte(id) ON DELETE CASCADE,
  elemento_id bigint NOT NULL REFERENCES gest.elemento_analitico(id),
  porcentaje  numeric(7,4) NOT NULL DEFAULT 100,
  debe        numeric(18,2) NOT NULL DEFAULT 0,
  haber       numeric(18,2) NOT NULL DEFAULT 0
);
CREATE INDEX ix_apunte_analitica ON gest.apunte_analitica(elemento_id);

-- Reparto analítico por defecto de una cuenta
CREATE TABLE gest.cuenta_analitica_defecto (
  cuenta_id   bigint NOT NULL REFERENCES conta.cuenta(id),
  elemento_id bigint NOT NULL REFERENCES gest.elemento_analitico(id),
  porcentaje  numeric(7,4) NOT NULL DEFAULT 100,
  PRIMARY KEY (cuenta_id, elemento_id)
);

CREATE TABLE gest.presupuesto (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  ejercicio_id  bigint NOT NULL REFERENCES core.ejercicio(id),
  codigo        varchar(20)  NOT NULL,
  nombre        varchar(150) NOT NULL,
  version       smallint NOT NULL DEFAULT 1,
  canal_base_id bigint,
  tipo_base     varchar(10) DEFAULT 'BASE' CHECK (tipo_base = 'BASE'),
  estado        varchar(10) NOT NULL DEFAULT 'BORRADOR' CHECK (estado IN ('BORRADOR','APROBADO','CERRADO')),
  FOREIGN KEY (canal_base_id, tipo_base) REFERENCES canal.canal(id, tipo),
  UNIQUE (empresa_id, ejercicio_id, codigo, version)
);

CREATE TABLE gest.presupuesto_linea (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  presupuesto_id bigint NOT NULL REFERENCES gest.presupuesto(id) ON DELETE CASCADE,
  cuenta_patron  varchar(15) NOT NULL,            -- prefijo: '62' agrupa todas las 62x
  elemento_id    bigint REFERENCES gest.elemento_analitico(id),
  actividad_id   bigint REFERENCES core.empresa_actividad(id),
  periodo_numero smallint NOT NULL,               -- 1..12 dentro del ejercicio
  importe        numeric(18,2) NOT NULL
);

-- Definición de balances / cuentas anuales (modelo normal, PYMES, personalizados)
CREATE TABLE gest.balance_modelo (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint REFERENCES core.empresa(id),  -- NULL = modelo del sistema
  codigo      varchar(20)  NOT NULL,
  nombre      varchar(150) NOT NULL,
  tipo        varchar(25)  NOT NULL
    CHECK (tipo IN ('BALANCE_SITUACION','PERDIDAS_GANANCIAS','ANALITICO','ESTADO_CAMBIOS_PN','PERSONALIZADO')),
  plan_tipo   varchar(15)  NOT NULL DEFAULT 'CUALQUIERA' CHECK (plan_tipo IN ('NORMAL','PYMES','ABREVIADO','CUALQUIERA')),
  version     smallint NOT NULL DEFAULT 1,
  activo      boolean NOT NULL DEFAULT true
);
CREATE UNIQUE INDEX ux_balance_modelo ON gest.balance_modelo(COALESCE(empresa_id,0), codigo, version);

CREATE TABLE gest.balance_linea (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  modelo_id   bigint NOT NULL REFERENCES gest.balance_modelo(id) ON DELETE CASCADE,
  codigo      varchar(20)  NOT NULL,              -- 'A.I.1', 'B.II'...
  descripcion varchar(250) NOT NULL,
  padre_id    bigint REFERENCES gest.balance_linea(id),
  orden       integer NOT NULL,
  nivel       smallint NOT NULL DEFAULT 1,
  tipo        varchar(10) NOT NULL DEFAULT 'CUENTAS' CHECK (tipo IN ('TITULO','CUENTAS','FORMULA')),
  expresion   text,                               -- p.ej. 'A.I + A.II - B' (referencia a códigos de línea)
  negrita     boolean NOT NULL DEFAULT false,
  UNIQUE (modelo_id, codigo)
);

CREATE TABLE gest.balance_linea_cuenta (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  linea_id      bigint NOT NULL REFERENCES gest.balance_linea(id) ON DELETE CASCADE,
  cuenta_desde  varchar(15) NOT NULL,             -- prefijo o rango de cuentas
  cuenta_hasta  varchar(15),
  tipo_saldo    varchar(10) NOT NULL DEFAULT 'NETO' CHECK (tipo_saldo IN ('NETO','DEUDOR','ACREEDOR')),
  signo         smallint NOT NULL DEFAULT 1 CHECK (signo IN (1,-1)),
  solo_si_positivo boolean NOT NULL DEFAULT false -- p.ej. 572 con saldo acreedor pasa al pasivo
);

/* =====================================================================
   9. SEMILLAS MÍNIMAS (verificar importes/fechas vigentes antes de producción)
   ===================================================================== */

INSERT INTO cat.pais (iso2, iso3, nombre, es_ue, prefijo_vies) VALUES
  ('ES','ESP','España', true, 'ES'),
  ('PT','PRT','Portugal', true, 'PT'),
  ('FR','FRA','Francia', true, 'FR'),
  ('DE','DEU','Alemania', true, 'DE'),
  ('GB','GBR','Reino Unido', false, NULL);

INSERT INTO cat.plan_modelo (codigo, nombre, tipo, version) VALUES
  ('PGC_NORMAL','Plan General de Contabilidad - Normal','NORMAL','RD 1514/2007'),
  ('PGC_PYMES','Plan General de Contabilidad - PYMES','PYMES','RD 1515/2007');

INSERT INTO cat.tipo_iva (impuesto, codigo, nombre, es_exento) VALUES
  ('IVA','IVA_GEN','IVA general', false),
  ('IVA','IVA_RED','IVA reducido', false),
  ('IVA','IVA_SUP','IVA superreducido', false),
  ('IVA','IVA_EXE','Exento / 0%', true),
  ('IGIC','IGIC_GEN','IGIC general', false),
  ('IGIC','IGIC_RED','IGIC reducido', false),
  ('IGIC','IGIC_INC','IGIC incrementado', false),
  ('IGIC','IGIC_EXE','IGIC exento / 0%', true);

INSERT INTO cat.tipo_iva_vigencia (tipo_iva_id, porcentaje, recargo_equivalencia, fecha_desde)
SELECT id, v.pct, v.rec, v.fd::date FROM cat.tipo_iva t
JOIN (VALUES ('IVA_GEN',21,5.2,'2012-09-01'),('IVA_RED',10,1.4,'2012-09-01'),
             ('IVA_SUP',4,0.5,'1995-01-01'),('IVA_EXE',0,0,'1986-01-01')) AS v(cod,pct,rec,fd)
  ON v.cod = t.codigo;
-- Los tipos IGIC se cargan con su vigencia real desde la administración canaria (ATC).

INSERT INTO cat.tipo_retencion_irpf (codigo, nombre, naturaleza, modelo_ingreso) VALUES
  ('PROF_GEN','Profesionales (general)','PROFESIONAL','111'),
  ('PROF_INI','Profesionales (inicio actividad)','PROFESIONAL','111'),
  ('ARREND','Arrendamiento de inmuebles urbanos','ARRENDAMIENTO','115');

INSERT INTO cat.tipo_retencion_irpf_vigencia (tipo_retencion_id, porcentaje, fecha_desde)
SELECT id, v.pct, v.fd::date FROM cat.tipo_retencion_irpf t
JOIN (VALUES ('PROF_GEN',15,'2015-07-12'),('PROF_INI',7,'2015-01-01'),('ARREND',19,'2016-01-01')) AS v(cod,pct,fd)
  ON v.cod = t.codigo;

INSERT INTO fiscal.modelo_fiscal (administracion, codigo, nombre, impuesto, periodicidades) VALUES
  ('AEAT','303','IVA - Autoliquidación','IVA','{MENSUAL,TRIMESTRAL}'),
  ('AEAT','390','IVA - Resumen anual','IVA','{ANUAL}'),
  ('AEAT','349','Operaciones intracomunitarias','IVA','{MENSUAL,TRIMESTRAL,ANUAL}'),
  ('AEAT','347','Operaciones con terceras personas','TERCEROS','{ANUAL}'),
  ('AEAT','130','IRPF - Pago fraccionado estimación directa','IRPF','{TRIMESTRAL}'),
  ('AEAT','131','IRPF - Pago fraccionado estimación objetiva','IRPF','{TRIMESTRAL}'),
  ('AEAT','111','Retenciones trabajo y actividades profesionales','IRPF','{MENSUAL,TRIMESTRAL}'),
  ('AEAT','115','Retenciones arrendamientos','IRPF','{MENSUAL,TRIMESTRAL}'),
  ('AEAT','190','Resumen anual retenciones trabajo/profesionales','IRPF','{ANUAL}'),
  ('AEAT','180','Resumen anual retenciones arrendamientos','IRPF','{ANUAL}'),
  ('AEAT','200','Impuesto sobre Sociedades','IS','{ANUAL}'),
  ('AEAT','202','Pago fraccionado Sociedades','IS','{TRIMESTRAL}'),
  ('ATC','420','IGIC - Autoliquidación','IGIC','{MENSUAL,TRIMESTRAL}'),
  ('ATC','425','IGIC - Resumen anual','IGIC','{ANUAL}'),
  ('ATC','415','IGIC - Operaciones con terceras personas','TERCEROS','{ANUAL}');
