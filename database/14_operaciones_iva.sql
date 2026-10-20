-- Operaciones intracomunitarias, importaciones e inversión del sujeto pasivo en facturas.
-- Ejecutar DESPUÉS de 13_bienes_inversion.sql. Se puede volver a ejecutar.

-- 'INVERSION_SUJETO_PASIVO' (23 caracteres) no cabía en varchar(20)
ALTER TABLE fiscal.factura_iva ALTER COLUMN tipo_operacion TYPE varchar(25);
