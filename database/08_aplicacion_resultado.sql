-- Aplicación del resultado: el asiento que reparte el saldo de la 129 (reservas, remanente, dividendos...)
-- tiene su propio origen. Ejecutar DESPUÉS de 07_pgc.sql. Se puede volver a ejecutar.

ALTER TABLE conta.asiento DROP CONSTRAINT IF EXISTS asiento_origen_check;
ALTER TABLE conta.asiento ADD CONSTRAINT asiento_origen_check CHECK (origen IN (
  'MANUAL','PLANTILLA','FACTURA','TESORERIA','AMORTIZACION','IVA','APERTURA','REGULARIZACION','CIERRE','IMPORTACION','APLICACION'));
