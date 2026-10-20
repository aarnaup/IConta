-- 18: suma de dígitos, no sujetas por localización, nóminas, REAGP, IGIC simplificado/minorista, 309, IS (asientos y diferido),
--     inmuebles del 180 y regularización de bienes de inversión por afectación.

-- ---------------------------------------------------------------- amortización por suma de dígitos
ALTER TABLE inmov.categoria DROP CONSTRAINT IF EXISTS categoria_metodo_defecto_check;
ALTER TABLE inmov.categoria ADD CONSTRAINT categoria_metodo_defecto_check
  CHECK (metodo_defecto IN ('LINEAL','DEGRESIVO','DIGITOS_DECRECIENTE','DIGITOS_CRECIENTE','COEFICIENTE_TABLA','NO_AMORTIZA'));
ALTER TABLE inmov.inmovilizado DROP CONSTRAINT IF EXISTS inmovilizado_metodo_fiscal_check;
ALTER TABLE inmov.inmovilizado ADD CONSTRAINT inmovilizado_metodo_fiscal_check
  CHECK (metodo_fiscal IN ('LINEAL','DEGRESIVO','DIGITOS_DECRECIENTE','DIGITOS_CRECIENTE','COEFICIENTE_TABLA','LIBRE'));
