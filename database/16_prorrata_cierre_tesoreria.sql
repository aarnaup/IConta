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

-- Provincias (código INE): la del perceptor va en los modelos 190 y 180; se deduce del código postal
INSERT INTO cat.provincia (pais_id, codigo, nombre, territorio_fiscal)
SELECT p.id, v.codigo, v.nombre, v.territorio FROM cat.pais p CROSS JOIN (VALUES
  ('01','Araba/Álava','ALAVA'), ('02','Albacete','COMUN'), ('03','Alicante/Alacant','COMUN'), ('04','Almería','COMUN'), ('05','Ávila','COMUN'),
  ('06','Badajoz','COMUN'), ('07','Illes Balears','COMUN'), ('08','Barcelona','COMUN'), ('09','Burgos','COMUN'), ('10','Cáceres','COMUN'),
  ('11','Cádiz','COMUN'), ('12','Castellón/Castelló','COMUN'), ('13','Ciudad Real','COMUN'), ('14','Córdoba','COMUN'), ('15','A Coruña','COMUN'),
  ('16','Cuenca','COMUN'), ('17','Girona','COMUN'), ('18','Granada','COMUN'), ('19','Guadalajara','COMUN'), ('20','Gipuzkoa','GIPUZKOA'),
  ('21','Huelva','COMUN'), ('22','Huesca','COMUN'), ('23','Jaén','COMUN'), ('24','León','COMUN'), ('25','Lleida','COMUN'),
  ('26','La Rioja','COMUN'), ('27','Lugo','COMUN'), ('28','Madrid','COMUN'), ('29','Málaga','COMUN'), ('30','Murcia','COMUN'),
  ('31','Navarra','NAVARRA'), ('32','Ourense','COMUN'), ('33','Asturias','COMUN'), ('34','Palencia','COMUN'), ('35','Las Palmas','CANARIAS'),
  ('36','Pontevedra','COMUN'), ('37','Salamanca','COMUN'), ('38','Santa Cruz de Tenerife','CANARIAS'), ('39','Cantabria','COMUN'), ('40','Segovia','COMUN'),
  ('41','Sevilla','COMUN'), ('42','Soria','COMUN'), ('43','Tarragona','COMUN'), ('44','Teruel','COMUN'), ('45','Toledo','COMUN'),
  ('46','Valencia/València','COMUN'), ('47','Valladolid','COMUN'), ('48','Bizkaia','BIZKAIA'), ('49','Zamora','COMUN'), ('50','Zaragoza','COMUN'),
  ('51','Ceuta','CEUTA'), ('52','Melilla','MELILLA')
) AS v(codigo, nombre, territorio)
WHERE p.iso2 = 'ES'
ON CONFLICT (pais_id, codigo) DO NOTHING;
-- NIF españoles que ya tienen código postal y no provincia
UPDATE core.nif n SET provincia_id = pr.id
FROM cat.provincia pr JOIN cat.pais p ON p.id = pr.pais_id AND p.iso2 = 'ES'
WHERE n.provincia_id IS NULL AND n.pais_id = p.id AND pr.codigo = left(n.codigo_postal, 2);
