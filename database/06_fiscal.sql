-- Modelos fiscales: cálculo, guardado y presentación de declaraciones.
-- Ejecutar DESPUÉS de 05_tesoreria.sql.

ALTER TABLE fiscal.declaracion
  ADD COLUMN creado_por  bigint REFERENCES core.usuario(id),
  ADD COLUMN calculada_en timestamptz,
  ADD COLUMN opciones    jsonb,
  ADD CONSTRAINT ck_decl_periodo CHECK (periodo ~ '^([1-4]T|0[1-9]|1[0-2]|0A)$'),
  ADD CONSTRAINT ck_decl_fechas  CHECK (fecha_fin >= fecha_inicio),
  ADD CONSTRAINT ck_decl_presentada CHECK (estado <> 'PRESENTADA' OR fecha_presentacion IS NOT NULL);

CREATE INDEX ix_declaracion_empresa ON fiscal.declaracion(empresa_id, modelo_id, fecha_fin);

-- Las claves de casilla de los modelos con IGIC son descriptivas (D7B, D9.5C, TDEV…)
ALTER TABLE fiscal.declaracion_casilla ALTER COLUMN casilla TYPE varchar(12);

-- Orden de presentación de las casillas
ALTER TABLE fiscal.declaracion_casilla ADD COLUMN orden smallint NOT NULL DEFAULT 0;
