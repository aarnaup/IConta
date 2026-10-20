-- Regularización de las deducciones de los bienes de inversión (arts. 107-110 LIVA) y prorrata desde la aplicación.
-- Ejecutar DESPUÉS de 12_remesas.sql. Se puede volver a ejecutar.

-- El asiento de regularización tiene su propio origen
ALTER TABLE conta.asiento DROP CONSTRAINT IF EXISTS asiento_origen_check;
ALTER TABLE conta.asiento ADD CONSTRAINT asiento_origen_check CHECK (origen IN (
  'MANUAL','PLANTILLA','FACTURA','TESORERIA','AMORTIZACION','IVA','APERTURA','REGULARIZACION','CIERRE','IMPORTACION','APLICACION',
  'BIEN_INVERSION'));

ALTER TABLE inmov.bien_inversion ADD COLUMN IF NOT EXISTS sector_diferenciado smallint;
-- Venta del bien exenta o no sujeta (p. ej., segunda entrega de un inmueble): la regularización por venta se hace al 0 %
ALTER TABLE inmov.bien_inversion ADD COLUMN IF NOT EXISTS transmision_exenta boolean NOT NULL DEFAULT false;
ALTER TABLE inmov.bien_inversion ADD COLUMN IF NOT EXISTS creado_en timestamptz NOT NULL DEFAULT now();

ALTER TABLE inmov.bien_inversion_regularizacion ADD COLUMN IF NOT EXISTS tipo varchar(12) NOT NULL DEFAULT 'ANUAL'
  CHECK (tipo IN ('ANUAL','TRANSMISION'));
ALTER TABLE inmov.bien_inversion_regularizacion ADD COLUMN IF NOT EXISTS anios_regularizados smallint NOT NULL DEFAULT 1;
ALTER TABLE inmov.bien_inversion_regularizacion ADD COLUMN IF NOT EXISTS prorrata_inicial smallint;
ALTER TABLE inmov.bien_inversion_regularizacion ADD COLUMN IF NOT EXISTS fecha date;
ALTER TABLE inmov.bien_inversion_regularizacion ADD COLUMN IF NOT EXISTS asiento_id bigint REFERENCES conta.asiento(id);
-- Al anular el asiento, la fila se borra; una por bien y año (la venta sustituye a la anual de ese año)
CREATE INDEX IF NOT EXISTS ix_bien_inversion_regularizacion_asiento ON inmov.bien_inversion_regularizacion (asiento_id);
