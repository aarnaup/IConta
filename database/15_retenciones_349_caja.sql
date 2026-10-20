-- Modelos 111/115 y 349, y régimen especial del criterio de caja (arts. 163 decies a sexiesdecies LIVA).
-- Ejecutar DESPUÉS de 14_operaciones_iva.sql. Se puede volver a ejecutar.

-- 349: clave de la operación intracomunitaria (E entregas, S servicios prestados, T triangulares; A adquisiciones, I servicios recibidos)
ALTER TABLE fiscal.factura ADD COLUMN IF NOT EXISTS clave_operacion_349 char(1)
  CHECK (clave_operacion_349 IN ('E','S','T','A','I'));

-- Criterio de caja: se decide al contabilizar la factura y no cambia aunque la empresa salga del régimen
ALTER TABLE fiscal.factura ADD COLUMN IF NOT EXISTS criterio_caja boolean NOT NULL DEFAULT false;
-- Proveedor acogido al criterio de caja: quien le compra deduce el IVA cuando paga (art. 163 terdecies)
ALTER TABLE conta.tercero ADD COLUMN IF NOT EXISTS criterio_caja boolean NOT NULL DEFAULT false;

-- Traspaso del IVA de criterio de caja devengado en cada periodo (cobros, pagos y 31/12 del año siguiente)
CREATE TABLE IF NOT EXISTS fiscal.devengo_caja (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  empresa_id  bigint NOT NULL REFERENCES core.empresa(id),
  factura_id  bigint NOT NULL REFERENCES fiscal.factura(id),
  anio        smallint NOT NULL,
  periodo     varchar(3) NOT NULL,
  base        numeric(18,2) NOT NULL,
  cuota       numeric(18,2) NOT NULL,          -- repercutida (+ recargo) o deducible
  asiento_id  bigint NOT NULL REFERENCES conta.asiento(id),
  UNIQUE (factura_id, anio, periodo)
);
CREATE INDEX IF NOT EXISTS ix_devengo_caja_asiento ON fiscal.devengo_caja (asiento_id);

ALTER TABLE conta.asiento DROP CONSTRAINT IF EXISTS asiento_origen_check;
ALTER TABLE conta.asiento ADD CONSTRAINT asiento_origen_check CHECK (origen IN (
  'MANUAL','PLANTILLA','FACTURA','TESORERIA','AMORTIZACION','IVA','APERTURA','REGULARIZACION','CIERRE','IMPORTACION','APLICACION',
  'BIEN_INVERSION','CRITERIO_CAJA'));

-- Cuentas del IVA pendiente de devengo/deducción por criterio de caja (4779 y 4729) y sus roles
CREATE OR REPLACE FUNCTION fiscal.asegurar_cuentas_caja(p_empresa bigint) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r record; v_cod text; v_id bigint; v_imp text;
BEGIN
  SELECT CASE impuesto_indirecto WHEN 'IGIC' THEN 'IGIC' ELSE 'IVA' END INTO v_imp FROM core.empresa WHERE id = p_empresa;
  FOR r IN SELECT * FROM (VALUES
      ('4779', '477', 'ACREEDORA', 'IVA_REPERCUTIDO_CAJA', 'H.P., ' || v_imp || ' repercutido pendiente de devengo (criterio de caja)'),
      ('4729', '472', 'DEUDORA',   'IVA_SOPORTADO_CAJA',   'H.P., ' || v_imp || ' soportado pendiente de deducir (criterio de caja)')
    ) AS t(codigo, padre, nat, rol, nombre)
  LOOP
    SELECT rpad(r.codigo, e.digitos_cuenta, '0') INTO v_cod FROM core.empresa e WHERE e.id = p_empresa;
    SELECT id INTO v_id FROM conta.cuenta WHERE empresa_id = p_empresa AND codigo = v_cod;
    IF v_id IS NULL THEN
      INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, padre_id, naturaleza, es_imputable)
      VALUES (p_empresa, v_cod, r.nombre, 4, (SELECT id FROM conta.cuenta WHERE empresa_id = p_empresa AND codigo = r.padre), r.nat, true)
      RETURNING id INTO v_id;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM conta.cuenta_automatica WHERE empresa_id = p_empresa AND rol = r.rol) THEN
      INSERT INTO conta.cuenta_automatica (empresa_id, rol, cuenta_id) VALUES (p_empresa, r.rol, v_id);
    END IF;
  END LOOP;
END $$;

-- Países de la UE (y algunos habituales fuera de ella) para el 349 y los NIF de extranjeros
INSERT INTO cat.pais (iso2, iso3, nombre, es_ue, prefijo_vies) VALUES
  ('AT','AUT','Austria',true,'AT'), ('BE','BEL','Bélgica',true,'BE'), ('BG','BGR','Bulgaria',true,'BG'), ('CY','CYP','Chipre',true,'CY'),
  ('CZ','CZE','Chequia',true,'CZ'), ('DE','DEU','Alemania',true,'DE'), ('DK','DNK','Dinamarca',true,'DK'), ('EE','EST','Estonia',true,'EE'),
  ('ES','ESP','España',true,'ES'), ('FI','FIN','Finlandia',true,'FI'), ('FR','FRA','Francia',true,'FR'), ('GR','GRC','Grecia',true,'EL'),
  ('HR','HRV','Croacia',true,'HR'), ('HU','HUN','Hungría',true,'HU'), ('IE','IRL','Irlanda',true,'IE'), ('IT','ITA','Italia',true,'IT'),
  ('LT','LTU','Lituania',true,'LT'), ('LU','LUX','Luxemburgo',true,'LU'), ('LV','LVA','Letonia',true,'LV'), ('MT','MLT','Malta',true,'MT'),
  ('NL','NLD','Países Bajos',true,'NL'), ('PL','POL','Polonia',true,'PL'), ('PT','PRT','Portugal',true,'PT'), ('RO','ROU','Rumanía',true,'RO'),
  ('SE','SWE','Suecia',true,'SE'), ('SI','SVN','Eslovenia',true,'SI'), ('SK','SVK','Eslovaquia',true,'SK'),
  ('GB','GBR','Reino Unido',false,NULL), ('US','USA','Estados Unidos',false,NULL), ('CH','CHE','Suiza',false,NULL),
  ('NO','NOR','Noruega',false,NULL), ('AD','AND','Andorra',false,NULL), ('MA','MAR','Marruecos',false,NULL), ('CN','CHN','China',false,NULL),
  ('MX','MEX','México',false,NULL), ('AR','ARG','Argentina',false,NULL), ('CO','COL','Colombia',false,NULL), ('BR','BRA','Brasil',false,NULL)
ON CONFLICT (iso2) DO NOTHING;
