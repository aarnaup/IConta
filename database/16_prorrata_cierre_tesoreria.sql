-- Asiento de regularización de la prorrata definitiva, cierre de los grupos 8 y 9, extorno de cobros/pagos y reglas de conciliación.
-- Ejecutar DESPUÉS de 15_retenciones_349_caja.sql. Se puede volver a ejecutar.

ALTER TABLE conta.asiento DROP CONSTRAINT IF EXISTS asiento_origen_check;
ALTER TABLE conta.asiento ADD CONSTRAINT asiento_origen_check CHECK (origen IN (
  'MANUAL','PLANTILLA','FACTURA','TESORERIA','AMORTIZACION','IVA','APERTURA','REGULARIZACION','CIERRE','IMPORTACION','APLICACION',
  'BIEN_INVERSION','CRITERIO_CAJA','PRORRATA'));

-- Regularización por prorrata definitiva contabilizada (472 contra 6341/6342 o 6391/6392)
CREATE TABLE IF NOT EXISTS fiscal.regularizacion_prorrata (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  anio        smallint NOT NULL,
  fecha       date NOT NULL,
  importe     numeric(18,2) NOT NULL,            -- (+) más deducción / (-) menos
  asiento_id  bigint NOT NULL REFERENCES conta.asiento(id)
);
CREATE INDEX IF NOT EXISTS ix_regularizacion_prorrata ON fiscal.regularizacion_prorrata (empresa_id, anio);

-- Reglas de conciliación: si el concepto de una línea del extracto contiene el texto, se contabiliza contra la cuenta indicada
CREATE TABLE IF NOT EXISTS tes.regla_conciliacion (
  id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id         bigint NOT NULL REFERENCES core.empresa(id),
  cuenta_bancaria_id bigint REFERENCES tes.cuenta_bancaria(id),        -- NULL = todas las cuentas
  texto              varchar(100) NOT NULL,
  signo              varchar(6) NOT NULL DEFAULT 'AMBOS' CHECK (signo IN ('CARGO','ABONO','AMBOS')),
  cuenta_id          bigint NOT NULL REFERENCES conta.cuenta(id),
  canal_id           bigint NOT NULL REFERENCES canal.canal(id),
  concepto           varchar(250),
  orden              integer NOT NULL DEFAULT 100,
  activa             boolean NOT NULL DEFAULT true
);
CREATE INDEX IF NOT EXISTS ix_regla_conciliacion ON tes.regla_conciliacion (empresa_id) WHERE activa;
