-- Legislación por fechas (Administración → Legislación, solo superadmin)
--  · cat.parametro_fiscal: porcentajes y límites de los cálculos con su fecha «desde»; rige el más reciente que no pase de la fecha del periodo
--  · fiscal.formulario_version: casillas y disposición del formulario de un modelo desde un año (JSON)
--  · fiscal.diseno_version: diseño de registro del fichero de un modelo desde una fecha (JSON); tiene preferencia sobre el del programa
CREATE TABLE IF NOT EXISTS cat.parametro_fiscal (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  clave       varchar(40) NOT NULL,
  desde       date NOT NULL,
  valor       numeric(18,4) NOT NULL,
  descripcion varchar(200) NOT NULL DEFAULT '',
  UNIQUE (clave, desde)
);

CREATE TABLE IF NOT EXISTS fiscal.formulario_version (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  modelo      varchar(20) NOT NULL,   -- 303, 130… o BALANCE_200 / PYG_200 (cuentas de cada partida del 200)
  desde_anio  smallint NOT NULL,
  definicion  jsonb NOT NULL,
  comentario  varchar(250),
  creado_en   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (modelo, desde_anio)
);

CREATE TABLE IF NOT EXISTS fiscal.diseno_version (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  modelo      varchar(5) NOT NULL,
  desde       date NOT NULL,
  hasta       date,
  definicion  jsonb NOT NULL,
  comentario  varchar(250),
  creado_en   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (modelo, desde),
  CHECK (hasta IS NULL OR hasta >= desde)
);

INSERT INTO cat.parametro_fiscal (clave, desde, valor, descripcion) VALUES
  ('IRPF_130_PCT', DATE '2000-01-01', 20, '130: porcentaje sobre el rendimiento neto acumulado'),
  ('IVA_347_UMBRAL', DATE '2000-01-01', 3005.06, '347: importe a partir del cual se declara un tercero'),
  ('IVA_BIEN_INVERSION_MINIMO', DATE '2000-01-01', 3005.06, 'IVA: importe mínimo para que un bien sea de inversión (art. 108)'),
  ('IS_TIPO_GENERAL', DATE '2000-01-01', 25, 'Sociedades: tipo general'),
  ('IS_TIPO_NUEVA_CREACION', DATE '2000-01-01', 15, 'Sociedades: entidades de nueva creación'),
  ('IS_TIPO_REDUCIDA_DIMENSION', DATE '2000-01-01', 25, 'Sociedades: reducida dimensión'),
  ('IS_TIPO_REDUCIDA_DIMENSION', DATE '2025-01-01', 24, 'Sociedades: reducida dimensión'),
  ('IS_TIPO_REDUCIDA_DIMENSION', DATE '2026-01-01', 23, 'Sociedades: reducida dimensión'),
  ('IS_TIPO_REDUCIDA_DIMENSION', DATE '2027-01-01', 22, 'Sociedades: reducida dimensión'),
  ('IS_TIPO_REDUCIDA_DIMENSION', DATE '2028-01-01', 21, 'Sociedades: reducida dimensión'),
  ('IS_TIPO_REDUCIDA_DIMENSION', DATE '2029-01-01', 20, 'Sociedades: reducida dimensión'),
  ('IS_MICRO_TRAMO1', DATE '2000-01-01', 25, 'Sociedades: microempresas, tipo de los primeros euros de base'),
  ('IS_MICRO_TRAMO1', DATE '2023-01-01', 23, 'Sociedades: microempresas, tipo de los primeros euros de base'),
  ('IS_MICRO_TRAMO1', DATE '2025-01-01', 21, 'Sociedades: microempresas, tipo de los primeros euros de base'),
  ('IS_MICRO_TRAMO1', DATE '2026-01-01', 19, 'Sociedades: microempresas, tipo de los primeros euros de base'),
  ('IS_MICRO_TRAMO1', DATE '2027-01-01', 17, 'Sociedades: microempresas, tipo de los primeros euros de base'),
  ('IS_MICRO_RESTO', DATE '2000-01-01', 25, 'Sociedades: microempresas, tipo del resto de la base'),
  ('IS_MICRO_RESTO', DATE '2023-01-01', 23, 'Sociedades: microempresas, tipo del resto de la base'),
  ('IS_MICRO_RESTO', DATE '2025-01-01', 22, 'Sociedades: microempresas, tipo del resto de la base'),
  ('IS_MICRO_RESTO', DATE '2026-01-01', 21, 'Sociedades: microempresas, tipo del resto de la base'),
  ('IS_MICRO_RESTO', DATE '2027-01-01', 20, 'Sociedades: microempresas, tipo del resto de la base'),
  ('IS_MICRO_TRAMO', DATE '2000-01-01', 50000, 'Sociedades: microempresas, base del primer tramo (anual)'),
  ('IS_202_PCT_40_2', DATE '2000-01-01', 18, '202 (art. 40.2): porcentaje sobre la cuota del último periodo')
ON CONFLICT (clave, desde) DO NOTHING;

-- Instalaciones que ya tenían la tabla con el código de modelo corto
ALTER TABLE fiscal.formulario_version ALTER COLUMN modelo TYPE varchar(20);
