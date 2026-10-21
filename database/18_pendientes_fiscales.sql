-- 18: suma de dígitos, no sujetas por localización, nóminas, REAGP, IGIC simplificado/minorista, 309, IS (asientos y diferido),
--     inmuebles del 180 y regularización de bienes de inversión por afectación.

-- ---------------------------------------------------------------- amortización por suma de dígitos
ALTER TABLE inmov.categoria DROP CONSTRAINT IF EXISTS categoria_metodo_defecto_check;
ALTER TABLE inmov.categoria ADD CONSTRAINT categoria_metodo_defecto_check
  CHECK (metodo_defecto IN ('LINEAL','DEGRESIVO','DIGITOS_DECRECIENTE','DIGITOS_CRECIENTE','COEFICIENTE_TABLA','NO_AMORTIZA'));
ALTER TABLE inmov.inmovilizado DROP CONSTRAINT IF EXISTS inmovilizado_metodo_fiscal_check;
ALTER TABLE inmov.inmovilizado ADD CONSTRAINT inmovilizado_metodo_fiscal_check
  CHECK (metodo_fiscal IN ('LINEAL','DEGRESIVO','DIGITOS_DECRECIENTE','DIGITOS_CRECIENTE','COEFICIENTE_TABLA','LIBRE'));

-- ---------------------------------------------------------------- no sujetas por reglas de localización (casillas 120 y 123 del 303)
ALTER TABLE fiscal.factura_iva DROP CONSTRAINT IF EXISTS factura_iva_tipo_operacion_check;
ALTER TABLE fiscal.factura_iva ADD CONSTRAINT factura_iva_tipo_operacion_check
  CHECK (tipo_operacion IN ('INTERIOR','EXENTA','NO_SUJETA','NO_SUJETA_LOCAL','NO_SUJETA_OSS','INTRACOM_ADQUISICION','INTRACOM_ENTREGA',
                            'IMPORTACION','EXPORTACION','INVERSION_SUJETO_PASIVO'));

-- ---------------------------------------------------------------- nóminas (retenciones del trabajo en el 111 y clave A del 190)
ALTER TABLE conta.asiento DROP CONSTRAINT IF EXISTS asiento_origen_check;
ALTER TABLE conta.asiento ADD CONSTRAINT asiento_origen_check CHECK (origen IN (
  'MANUAL','PLANTILLA','FACTURA','TESORERIA','AMORTIZACION','IVA','APERTURA','REGULARIZACION','CIERRE','IMPORTACION','APLICACION',
  'BIEN_INVERSION','CRITERIO_CAJA','PRORRATA','NOMINA','IMPUESTO_SOCIEDADES'));

CREATE TABLE IF NOT EXISTS conta.empleado (
  tercero_id          bigint PRIMARY KEY REFERENCES conta.tercero(id),
  empresa_id          bigint NOT NULL REFERENCES core.empresa(id),
  anio_nacimiento     smallint CHECK (anio_nacimiento BETWEEN 1900 AND 2100),
  situacion_familiar  smallint NOT NULL DEFAULT 3 CHECK (situacion_familiar BETWEEN 1 AND 3),
  nif_conyuge         varchar(9),
  discapacidad        smallint NOT NULL DEFAULT 0 CHECK (discapacidad BETWEEN 0 AND 3),
  contrato            smallint NOT NULL DEFAULT 1 CHECK (contrato BETWEEN 1 AND 4),
  movilidad_geografica boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS conta.nomina (
  id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id          bigint NOT NULL REFERENCES core.empresa(id),
  ejercicio_id        bigint NOT NULL REFERENCES core.ejercicio(id),
  tercero_id          bigint NOT NULL REFERENCES conta.tercero(id),
  canal_id            bigint NOT NULL REFERENCES canal.canal(id),
  fecha               date NOT NULL,
  periodo             varchar(60),
  bruto               numeric(18,2) NOT NULL DEFAULT 0,
  especie             numeric(18,2) NOT NULL DEFAULT 0,
  ingreso_a_cuenta    numeric(18,2) NOT NULL DEFAULT 0,
  ingreso_repercutido boolean NOT NULL DEFAULT true,
  retencion           numeric(18,2) NOT NULL DEFAULT 0,
  ss_trabajador       numeric(18,2) NOT NULL DEFAULT 0,
  ss_empresa          numeric(18,2) NOT NULL DEFAULT 0,
  anticipos           numeric(18,2) NOT NULL DEFAULT 0,
  liquido             numeric(18,2) NOT NULL DEFAULT 0,
  estado              varchar(14) NOT NULL DEFAULT 'CONTABILIZADA' CHECK (estado IN ('CONTABILIZADA','ANULADA')),
  asiento_id          bigint REFERENCES conta.asiento(id),
  creado_en           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_nomina_fecha ON conta.nomina (empresa_id, fecha) WHERE estado = 'CONTABILIZADA';

-- ---------------------------------------------------------------- bienes de inversión: % de deducción por año (cambio de afectación)
CREATE TABLE IF NOT EXISTS inmov.bien_inversion_porcentaje (
  bien_inversion_id bigint NOT NULL REFERENCES inmov.bien_inversion(id) ON DELETE CASCADE,
  anio              smallint NOT NULL,
  porcentaje        smallint NOT NULL CHECK (porcentaje BETWEEN 0 AND 100),
  PRIMARY KEY (bien_inversion_id, anio)
);

-- ---------------------------------------------------------------- régimen especial de la agricultura, ganadería y pesca (REAGP)
ALTER TABLE fiscal.factura_iva DROP CONSTRAINT IF EXISTS factura_iva_tipo_operacion_check;
ALTER TABLE fiscal.factura_iva ADD CONSTRAINT factura_iva_tipo_operacion_check
  CHECK (tipo_operacion IN ('INTERIOR','EXENTA','NO_SUJETA','NO_SUJETA_LOCAL','NO_SUJETA_OSS','INTRACOM_ADQUISICION','INTRACOM_ENTREGA',
                            'IMPORTACION','EXPORTACION','INVERSION_SUJETO_PASIVO','COMPENSACION_REAGP'));
-- Porcentajes de compensación (art. 130 LIVA): 12 % agrícolas y forestales, 10,5 % ganaderas y pesqueras
INSERT INTO cat.tipo_iva (impuesto, codigo, nombre, es_exento, activo)
VALUES ('IVA', 'REAGP_AGR', 'Compensación REAGP agrícola o forestal', false, true),
       ('IVA', 'REAGP_GAN', 'Compensación REAGP ganadera o pesquera', false, true)
ON CONFLICT (codigo) DO NOTHING;
INSERT INTO cat.tipo_iva_vigencia (tipo_iva_id, porcentaje, recargo_equivalencia, fecha_desde)
SELECT t.id, v.pct, 0, DATE '2013-01-01' FROM cat.tipo_iva t JOIN (VALUES ('REAGP_AGR', 12.00), ('REAGP_GAN', 10.50)) v(cod, pct) ON v.cod = t.codigo
WHERE NOT EXISTS (SELECT 1 FROM cat.tipo_iva_vigencia x WHERE x.tipo_iva_id = t.id);

-- ---------------------------------------------------------------- IGIC: régimen especial del comerciante minorista
ALTER TABLE core.empresa DROP CONSTRAINT IF EXISTS empresa_regimen_iva_check;
ALTER TABLE core.empresa ADD CONSTRAINT empresa_regimen_iva_check
  CHECK (regimen_iva IN ('GENERAL','SIMPLIFICADO','RECARGO_EQUIVALENCIA','AGRICULTURA_GYP','CRITERIO_CAJA','EXENTO','COMERCIANTE_MINORISTA'));

-- ---------------------------------------------------------------- modelo 309 (declaración no periódica del IVA)
INSERT INTO fiscal.modelo_fiscal (administracion, codigo, nombre, impuesto, periodicidades)
VALUES ('AEAT', '309', 'IVA - Declaración-liquidación no periódica', 'IVA', '{TRIMESTRAL}')
ON CONFLICT (administracion, codigo) DO NOTHING;
