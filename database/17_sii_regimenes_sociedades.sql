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
