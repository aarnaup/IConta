-- SII, régimen especial del recargo de equivalencia y simplificado de la empresa, modelo 131, Impuesto sobre Sociedades
-- (200/202) y catálogos. Ejecutar DESPUÉS de 16_prorrata_cierre_tesoreria.sql. Se puede volver a ejecutar.

-- ---------------------------------------------------------------- SII
-- Empresa acogida al SII (obligatoria por volumen, REDEME o grupo, o voluntaria)
ALTER TABLE core.empresa ADD COLUMN IF NOT EXISTS sii boolean NOT NULL DEFAULT false;

-- Estado de cada factura en el SII: PENDIENTE (sin enviar), ACEPTADA, ACEPTADA_CON_ERRORES o RECHAZADA
ALTER TABLE fiscal.factura ADD COLUMN IF NOT EXISTS sii_estado varchar(25);
ALTER TABLE fiscal.factura ADD COLUMN IF NOT EXISTS sii_csv varchar(20);
ALTER TABLE fiscal.factura ADD COLUMN IF NOT EXISTS sii_enviada_en timestamptz;
ALTER TABLE fiscal.factura ADD COLUMN IF NOT EXISTS sii_error varchar(500);

-- Historial de envíos (mensaje y respuesta) para poder revisarlos
CREATE TABLE IF NOT EXISTS fiscal.envio_sii (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id    bigint NOT NULL REFERENCES core.empresa(id),
  libro         varchar(10) NOT NULL CHECK (libro IN ('EMITIDAS','RECIBIDAS')),
  tipo_comunicacion char(2) NOT NULL,
  entorno       varchar(10) NOT NULL CHECK (entorno IN ('PRUEBAS','PRODUCCION','MANUAL')),
  facturas      integer NOT NULL,
  estado_envio  varchar(30),
  csv           varchar(20),
  mensaje       text NOT NULL,
  respuesta     text,
  creado_en     timestamptz NOT NULL DEFAULT now(),
  creado_por    bigint REFERENCES core.usuario(id)
);
CREATE INDEX IF NOT EXISTS ix_envio_sii ON fiscal.envio_sii (empresa_id, creado_en DESC);

-- ---------------------------------------------------------------- Estimación objetiva y régimen simplificado (módulos)
-- Datos de la orden de módulos de cada año por actividad: rendimiento neto previo del IRPF (131) y, en el régimen simplificado
-- del IVA, la cuota devengada y la soportada por operaciones corrientes, el % de ingreso a cuenta y el % de cuota mínima.
CREATE TABLE IF NOT EXISTS fiscal.modulos (
  id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id              bigint NOT NULL REFERENCES core.empresa(id),
  anio                    smallint NOT NULL,
  actividad_id            bigint REFERENCES core.empresa_actividad(id),
  descripcion             varchar(100) NOT NULL,
  rendimiento_neto_irpf   numeric(18,2) NOT NULL DEFAULT 0,
  personas_asalariadas    numeric(6,2) NOT NULL DEFAULT 0,
  agricola                boolean NOT NULL DEFAULT false,
  cuota_iva_devengada     numeric(18,2) NOT NULL DEFAULT 0,
  cuota_iva_soportada     numeric(18,2) NOT NULL DEFAULT 0,
  porcentaje_ingreso_iva  numeric(5,2) NOT NULL DEFAULT 0 CHECK (porcentaje_ingreso_iva BETWEEN 0 AND 100),
  porcentaje_cuota_minima numeric(5,2) NOT NULL DEFAULT 0 CHECK (porcentaje_cuota_minima BETWEEN 0 AND 100)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_modulos ON fiscal.modulos (empresa_id, anio, descripcion);

-- ---------------------------------------------------------------- Impuesto sobre Sociedades (200 y 202)
-- Tipo de gravamen: GENERAL (25 %), NUEVA_CREACION (15 %), REDUCIDA_DIMENSION, MICRO (tramos) o PERSONALIZADO (is_porcentaje)
ALTER TABLE core.empresa ADD COLUMN IF NOT EXISTS is_tipo varchar(20) NOT NULL DEFAULT 'GENERAL';
ALTER TABLE core.empresa DROP CONSTRAINT IF EXISTS empresa_is_tipo_check;
ALTER TABLE core.empresa ADD CONSTRAINT empresa_is_tipo_check
  CHECK (is_tipo IN ('GENERAL','NUEVA_CREACION','REDUCIDA_DIMENSION','MICRO','PERSONALIZADO'));
ALTER TABLE core.empresa ADD COLUMN IF NOT EXISTS is_porcentaje numeric(5,2);
-- Modalidad del pago fraccionado: art. 40.2 (18 % de la cuota del último periodo) o 40.3 (sobre la base del periodo transcurrido)
ALTER TABLE core.empresa ADD COLUMN IF NOT EXISTS is_modalidad_202 varchar(10) NOT NULL DEFAULT 'ART_40_2';
ALTER TABLE core.empresa DROP CONSTRAINT IF EXISTS empresa_is_modalidad_202_check;
ALTER TABLE core.empresa ADD CONSTRAINT empresa_is_modalidad_202_check CHECK (is_modalidad_202 IN ('ART_40_2','ART_40_3'));

-- Datos del periodo impositivo que no salen de la contabilidad: ajustes extracontables, reserva de capitalización, bonificaciones,
-- deducciones e importe neto de la cifra de negocios (límite de compensación de bases negativas)
CREATE TABLE IF NOT EXISTS fiscal.impuesto_sociedades (
  empresa_id     bigint NOT NULL REFERENCES core.empresa(id),
  ejercicio_id   bigint NOT NULL REFERENCES core.ejercicio(id),
  ajustes        jsonb NOT NULL DEFAULT '[]',
  reserva_capitalizacion numeric(18,2) NOT NULL DEFAULT 0,
  bonificaciones numeric(18,2) NOT NULL DEFAULT 0,
  deducciones    numeric(18,2) NOT NULL DEFAULT 0,
  incn           numeric(18,2) NOT NULL DEFAULT 0,
  bins_anteriores numeric(18,2) NOT NULL DEFAULT 0,      -- bases negativas de antes de usar la aplicación
  PRIMARY KEY (empresa_id, ejercicio_id)
);

-- Periodos de los pagos fraccionados del 202: 1P (abril), 2P (octubre) y 3P (diciembre)
ALTER TABLE fiscal.declaracion DROP CONSTRAINT IF EXISTS ck_decl_periodo;
ALTER TABLE fiscal.declaracion ADD CONSTRAINT ck_decl_periodo CHECK (periodo ~ '^([1-4]T|0[1-9]|1[0-2]|0A|[1-3]P)$');
