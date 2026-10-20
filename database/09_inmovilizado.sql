-- Inmovilizado: fichas, amortización lineal por días, bajas y ventas.
-- Ejecutar DESPUÉS de 08_aplicacion_resultado.sql. Se puede volver a ejecutar.

-- Lo amortizado antes de dar de alta el elemento en la aplicación (traspaso desde otro programa) y datos de la baja
ALTER TABLE inmov.inmovilizado
  ADD COLUMN IF NOT EXISTS amortizacion_previa numeric(18,2) NOT NULL DEFAULT 0 CHECK (amortizacion_previa >= 0),
  ADD COLUMN IF NOT EXISTS importe_venta numeric(18,2),
  ADD COLUMN IF NOT EXISTS creado_en timestamptz NOT NULL DEFAULT now();

-- Cada ejecución de la amortización registra hasta qué fecha llega; puede haber varias por mes
ALTER TABLE inmov.amortizacion
  ADD COLUMN IF NOT EXISTS fecha_desde date,
  ADD COLUMN IF NOT EXISTS fecha_hasta date;
ALTER TABLE inmov.amortizacion DROP CONSTRAINT IF EXISTS amortizacion_inmovilizado_id_ejercicio_id_periodo_id_base_c_key;
CREATE UNIQUE INDEX IF NOT EXISTS ux_amortizacion_hasta ON inmov.amortizacion (inmovilizado_id, fecha_hasta, base_calculo);
CREATE INDEX IF NOT EXISTS ix_amortizacion_asiento ON inmov.amortizacion (asiento_id);
CREATE INDEX IF NOT EXISTS ix_inmovilizado_empresa ON inmov.inmovilizado (empresa_id, estado);

-- Subcuenta del plan de la empresa para un código del PGC (p. ej. 2817 → 28170000); si no existe, la crea con el nombre del plan modelo
CREATE OR REPLACE FUNCTION inmov.asegurar_cuenta(p_empresa bigint, p_codigo text) RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE v_id bigint; v_cod text; v_nombre text; v_nat text;
BEGIN
  IF p_codigo IS NULL THEN RETURN NULL; END IF;
  SELECT rpad(p_codigo, e.digitos_cuenta, '0') INTO v_cod FROM core.empresa e WHERE e.id = p_empresa;
  SELECT id INTO v_id FROM conta.cuenta WHERE empresa_id = p_empresa AND codigo = v_cod;
  IF v_id IS NOT NULL THEN RETURN v_id; END IF;

  SELECT m.nombre, m.naturaleza INTO v_nombre, v_nat
  FROM cat.cuenta_modelo m JOIN core.empresa e ON e.plan_modelo_id = m.plan_modelo_id
  WHERE e.id = p_empresa AND m.codigo = p_codigo;
  IF v_nombre IS NULL THEN RETURN NULL; END IF;

  INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, padre_id, naturaleza, es_imputable)
  VALUES (p_empresa, v_cod, v_nombre, 4,
          (SELECT id FROM conta.cuenta WHERE empresa_id = p_empresa AND codigo = left(p_codigo, 3)), v_nat, true)
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- Categorías habituales con sus cuentas y una vida útil dentro de los coeficientes máximos de la tabla fiscal (Ley 27/2014, art. 12)
CREATE OR REPLACE FUNCTION inmov.crear_categorias_defecto(p_empresa bigint) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; n integer := 0;
BEGIN
  IF EXISTS (SELECT 1 FROM inmov.categoria WHERE empresa_id = p_empresa) THEN RETURN 0; END IF;
  FOR r IN SELECT * FROM (VALUES
      ('Terrenos y bienes naturales',            'MATERIAL',   '2100', NULL,   NULL,   'NO_AMORTIZA', NULL::int),
      ('Construcciones',                         'MATERIAL',   '2110', '2811', '6810', 'LINEAL', 600),
      ('Instalaciones técnicas',                 'MATERIAL',   '2120', '2812', '6810', 'LINEAL', 120),
      ('Maquinaria',                             'MATERIAL',   '2130', '2813', '6810', 'LINEAL', 100),
      ('Utillaje',                               'MATERIAL',   '2140', '2814', '6810', 'LINEAL', 48),
      ('Otras instalaciones',                    'MATERIAL',   '2150', '2815', '6810', 'LINEAL', 120),
      ('Mobiliario',                             'MATERIAL',   '2160', '2816', '6810', 'LINEAL', 120),
      ('Equipos para procesos de información',   'MATERIAL',   '2170', '2817', '6810', 'LINEAL', 48),
      ('Elementos de transporte',                'MATERIAL',   '2180', '2818', '6810', 'LINEAL', 75),
      ('Otro inmovilizado material',             'MATERIAL',   '2190', '2819', '6810', 'LINEAL', 120),
      ('Propiedad industrial',                   'INTANGIBLE', '2030', '2803', '6800', 'LINEAL', 120),
      ('Aplicaciones informáticas',              'INTANGIBLE', '2060', '2806', '6800', 'LINEAL', 36)
    ) v(nombre, tipo, c_inm, c_amo, c_gas, metodo, meses)
  LOOP
    INSERT INTO inmov.categoria (empresa_id, nombre, tipo, cuenta_inmovilizado_id, cuenta_amort_acumulada_id, cuenta_gasto_amort_id,
                                 metodo_defecto, vida_util_meses_defecto)
    SELECT p_empresa, r.nombre, r.tipo, inmov.asegurar_cuenta(p_empresa, r.c_inm), inmov.asegurar_cuenta(p_empresa, r.c_amo),
           inmov.asegurar_cuenta(p_empresa, r.c_gas), r.metodo, r.meses
    WHERE inmov.asegurar_cuenta(p_empresa, r.c_inm) IS NOT NULL;
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- Empresas que ya existen
SELECT inmov.crear_categorias_defecto(id) FROM core.empresa;

-- Asientos de amortización: diario propio si la empresa lo tiene; las nuevas lo reciben al darse de alta
INSERT INTO conta.diario (empresa_id, codigo, nombre, tipo)
SELECT e.id, 'AMOR', 'Amortizaciones', 'AMORTIZACION' FROM core.empresa e
WHERE NOT EXISTS (SELECT 1 FROM conta.diario d WHERE d.empresa_id = e.id AND d.tipo = 'AMORTIZACION');
