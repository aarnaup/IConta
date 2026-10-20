-- Remesas SEPA: adeudos directos (cobro de recibos domiciliados, pain.008) y transferencias (pagos, pain.001).
-- Ejecutar DESPUÉS de 11_conciliacion.sql. Se puede volver a ejecutar.

-- Sufijo del identificador de acreedor SEPA (ES + control + sufijo + NIF); normalmente 000
ALTER TABLE core.empresa ADD COLUMN IF NOT EXISTS sepa_sufijo char(3) NOT NULL DEFAULT '000';

-- Mandato de domiciliación firmado por el cliente (orden de domiciliación SEPA)
CREATE TABLE IF NOT EXISTS tes.mandato (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id         bigint NOT NULL REFERENCES core.empresa(id),
  tercero_id         bigint NOT NULL REFERENCES conta.tercero(id),
  cuenta_bancaria_id bigint NOT NULL REFERENCES tes.cuenta_bancaria(id),   -- IBAN del deudor
  referencia         varchar(35) NOT NULL,
  fecha_firma        date NOT NULL,
  esquema            varchar(4) NOT NULL DEFAULT 'CORE' CHECK (esquema IN ('CORE','B2B')),
  recurrente         boolean NOT NULL DEFAULT true,
  usado              boolean NOT NULL DEFAULT false,      -- con el primer adeudo pasa de FRST a RCUR
  activo             boolean NOT NULL DEFAULT true,
  creado_en          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (empresa_id, referencia)
);
CREATE INDEX IF NOT EXISTS ix_mandato_tercero ON tes.mandato (tercero_id) WHERE activo;

CREATE TABLE IF NOT EXISTS tes.remesa (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id         bigint NOT NULL REFERENCES core.empresa(id),
  tipo               varchar(5) NOT NULL CHECK (tipo IN ('COBRO','PAGO')),
  cuenta_bancaria_id bigint NOT NULL REFERENCES tes.cuenta_bancaria(id),
  referencia         varchar(35) NOT NULL,                -- MsgId del fichero
  fecha_cargo        date NOT NULL,                       -- fecha de cobro (adeudos) o de ejecución (transferencias)
  importe            numeric(18,2) NOT NULL,
  num_operaciones    integer NOT NULL,
  estado             varchar(14) NOT NULL DEFAULT 'GENERADA' CHECK (estado IN ('GENERADA','CONTABILIZADA','ANULADA')),
  fichero            text NOT NULL,                       -- XML generado, para volver a descargarlo
  asiento_id         bigint REFERENCES conta.asiento(id),
  creado_por         bigint REFERENCES core.usuario(id),
  creado_en          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (empresa_id, referencia)
);

CREATE TABLE IF NOT EXISTS tes.remesa_vencimiento (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  remesa_id          bigint NOT NULL REFERENCES tes.remesa(id),
  vencimiento_id     bigint NOT NULL REFERENCES tes.vencimiento(id),
  importe            numeric(18,2) NOT NULL CHECK (importe > 0),
  mandato_id         bigint REFERENCES tes.mandato(id),
  secuencia          varchar(4) CHECK (secuencia IN ('FRST','RCUR','OOFF')),
  iban               varchar(34) NOT NULL,
  end_to_end_id      varchar(35) NOT NULL,
  estado             varchar(10) NOT NULL DEFAULT 'INCLUIDO' CHECK (estado IN ('INCLUIDO','DEVUELTO')),
  cobro_pago_id      bigint REFERENCES tes.cobro_pago(id),
  fecha_devolucion   date,
  motivo_devolucion  varchar(100),
  asiento_devolucion_id bigint REFERENCES conta.asiento(id)
);
CREATE INDEX IF NOT EXISTS ix_remesa_vencimiento ON tes.remesa_vencimiento (vencimiento_id);
CREATE INDEX IF NOT EXISTS ix_remesa_vencimiento_remesa ON tes.remesa_vencimiento (remesa_id);
