-- Declaraciones editables y corrección en el periodo siguiente
--  · casillas_calculadas: lo que salió del cálculo (con las correcciones de periodos anteriores), para poder recuperarlo
--  · casillas_manuales: casillas escritas a mano, que se respetan al recalcular
--  · fiscal.correccion_declaracion: diferencias de un periodo ya presentado que se llevan al periodo siguiente del mismo modelo
ALTER TABLE fiscal.declaracion
  ADD COLUMN IF NOT EXISTS casillas_calculadas jsonb,
  ADD COLUMN IF NOT EXISTS casillas_manuales jsonb NOT NULL DEFAULT '{}'::jsonb;

CREATE TABLE IF NOT EXISTS fiscal.correccion_declaracion (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  modelo_id   integer NOT NULL REFERENCES fiscal.modelo_fiscal(id),
  origen_id   bigint NOT NULL UNIQUE REFERENCES fiscal.declaracion(id),
  anio        smallint NOT NULL,                     -- periodo en el que se corrige
  periodo     varchar(3) NOT NULL,
  casillas    jsonb NOT NULL,                        -- {casilla del periodo destino: diferencia}
  detalle     jsonb NOT NULL,                        -- diferencias por casilla del periodo de origen
  creado_por  bigint REFERENCES core.usuario(id),
  creado_en   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_correccion_destino ON fiscal.correccion_declaracion(empresa_id, modelo_id, anio, periodo);
