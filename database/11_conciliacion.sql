-- Conciliación bancaria: extractos importados (Norma 43 o CSV) y su casación con los apuntes de la cuenta contable del banco.
-- Ejecutar DESPUÉS de 10_libros_registro.sql. Se puede volver a ejecutar.

-- Un fichero importado puede traer varias cuentas; cada una es un extracto
CREATE TABLE IF NOT EXISTS tes.extracto (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id         bigint NOT NULL REFERENCES core.empresa(id),
  cuenta_bancaria_id bigint NOT NULL REFERENCES tes.cuenta_bancaria(id),
  formato            varchar(10) NOT NULL CHECK (formato IN ('NORMA43','CSV')),
  fichero            varchar(200),
  fecha_desde        date NOT NULL,
  fecha_hasta        date NOT NULL,
  saldo_inicial      numeric(18,2),
  saldo_final        numeric(18,2),
  importado_por      bigint REFERENCES core.usuario(id),
  importado_en       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_extracto_cuenta ON tes.extracto (cuenta_bancaria_id, fecha_hasta);

CREATE TABLE IF NOT EXISTS tes.extracto_linea (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  extracto_id        bigint NOT NULL REFERENCES tes.extracto(id) ON DELETE CASCADE,
  empresa_id         bigint NOT NULL REFERENCES core.empresa(id),
  cuenta_bancaria_id bigint NOT NULL REFERENCES tes.cuenta_bancaria(id),
  orden              integer NOT NULL,
  fecha              date NOT NULL,
  fecha_valor        date,
  importe            numeric(18,2) NOT NULL CHECK (importe <> 0),   -- + abono (ingreso) / − cargo
  concepto           varchar(400),
  concepto_comun     varchar(2),                                    -- Norma 43: 01 talones, 02 abonarés, 03 domiciliados, 04 transferencias...
  documento          varchar(20),
  referencia1        varchar(20),
  referencia2        varchar(20),
  estado             varchar(10) NOT NULL DEFAULT 'PENDIENTE' CHECK (estado IN ('PENDIENTE','CONCILIADA','IGNORADA'))
);
CREATE INDEX IF NOT EXISTS ix_extracto_linea_cuenta ON tes.extracto_linea (cuenta_bancaria_id, estado, fecha);

-- Una conciliación casa una o varias líneas del banco con uno o varios apuntes de la cuenta del banco; las sumas coinciden
CREATE TABLE IF NOT EXISTS tes.conciliacion (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id         bigint NOT NULL REFERENCES core.empresa(id),
  cuenta_bancaria_id bigint NOT NULL REFERENCES tes.cuenta_bancaria(id),
  tipo               varchar(10) NOT NULL CHECK (tipo IN ('AUTOMATICA','MANUAL','ASIENTO')),
  importe            numeric(18,2) NOT NULL,
  creado_por         bigint REFERENCES core.usuario(id),
  creado_en          timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS tes.conciliacion_linea (
  conciliacion_id    bigint NOT NULL REFERENCES tes.conciliacion(id) ON DELETE CASCADE,
  extracto_linea_id  bigint NOT NULL UNIQUE REFERENCES tes.extracto_linea(id) ON DELETE CASCADE,
  PRIMARY KEY (conciliacion_id, extracto_linea_id)
);
CREATE TABLE IF NOT EXISTS tes.conciliacion_apunte (
  conciliacion_id    bigint NOT NULL REFERENCES tes.conciliacion(id) ON DELETE CASCADE,
  apunte_id          bigint NOT NULL UNIQUE REFERENCES conta.apunte(id) ON DELETE CASCADE,
  PRIMARY KEY (conciliacion_id, apunte_id)
);
