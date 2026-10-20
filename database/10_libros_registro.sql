-- Libros registro de IVA/IGIC: número de registro (orden de recepción) de las facturas recibidas.
-- El número se asigna al contabilizar la factura, correlativo por empresa y año de la fecha contable, y no cambia después.
-- Ejecutar DESPUÉS de 09_inmovilizado.sql. Se puede volver a ejecutar.

ALTER TABLE fiscal.factura
  ADD COLUMN IF NOT EXISTS anio_registro   smallint,
  ADD COLUMN IF NOT EXISTS numero_registro integer;
CREATE UNIQUE INDEX IF NOT EXISTS ux_factura_registro ON fiscal.factura (empresa_id, libro, anio_registro, numero_registro)
  WHERE numero_registro IS NOT NULL;

CREATE TABLE IF NOT EXISTS fiscal.contador_registro (
  empresa_id    bigint   NOT NULL REFERENCES core.empresa(id),
  libro         varchar(10) NOT NULL,
  anio          smallint NOT NULL,
  ultimo_numero integer  NOT NULL DEFAULT 0,
  PRIMARY KEY (empresa_id, libro, anio)
);

-- Numeración: el UPSERT bloquea la fila del contador, así que dos contabilizaciones simultáneas no repiten número
CREATE OR REPLACE FUNCTION fiscal.numerar_registro() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.libro = 'RECIBIDAS' AND NEW.estado = 'CONTABILIZADA' AND NEW.numero_registro IS NULL THEN
    NEW.anio_registro := EXTRACT(YEAR FROM NEW.fecha_contable)::smallint;
    INSERT INTO fiscal.contador_registro (empresa_id, libro, anio, ultimo_numero)
    VALUES (NEW.empresa_id, NEW.libro, NEW.anio_registro, 1)
    ON CONFLICT (empresa_id, libro, anio) DO UPDATE SET ultimo_numero = fiscal.contador_registro.ultimo_numero + 1
    RETURNING ultimo_numero INTO NEW.numero_registro;
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS tr_factura_numerar_registro ON fiscal.factura;
CREATE TRIGGER tr_factura_numerar_registro BEFORE INSERT OR UPDATE OF estado ON fiscal.factura
  FOR EACH ROW EXECUTE FUNCTION fiscal.numerar_registro();

-- Facturas recibidas ya contabilizadas: se numeran por fecha contable y orden de alta
WITH pendientes AS (
  SELECT f.id, f.empresa_id, EXTRACT(YEAR FROM f.fecha_contable)::smallint AS anio,
         COALESCE((SELECT ultimo_numero FROM fiscal.contador_registro c
                   WHERE c.empresa_id = f.empresa_id AND c.libro = 'RECIBIDAS' AND c.anio = EXTRACT(YEAR FROM f.fecha_contable)), 0)
         + row_number() OVER (PARTITION BY f.empresa_id, EXTRACT(YEAR FROM f.fecha_contable) ORDER BY f.fecha_contable, f.id) AS numero
  FROM fiscal.factura f
  WHERE f.libro = 'RECIBIDAS' AND f.estado = 'CONTABILIZADA' AND f.numero_registro IS NULL
)
UPDATE fiscal.factura f SET anio_registro = p.anio, numero_registro = p.numero FROM pendientes p WHERE f.id = p.id;

INSERT INTO fiscal.contador_registro (empresa_id, libro, anio, ultimo_numero)
SELECT empresa_id, libro, anio_registro, MAX(numero_registro) FROM fiscal.factura
WHERE libro = 'RECIBIDAS' AND numero_registro IS NOT NULL GROUP BY 1, 2, 3
ON CONFLICT (empresa_id, libro, anio) DO UPDATE SET ultimo_numero = GREATEST(fiscal.contador_registro.ultimo_numero, EXCLUDED.ultimo_numero);
