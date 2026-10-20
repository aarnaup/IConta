-- Datos de demo: empresa canaria (IGIC) con ejercicio PARTIDO 01/07/2026 - 30/06/2027,
-- canales 1,2,3 + derivados A=123, B=12, C=13, D=23 y E (60% canal 1 / resto canal 2).
-- Ejecutar DESPUÉS de contabilidad_schema.sql
DO $$
DECLARE v_pl int; v_nif bigint; v_emp bigint; v_ej bigint;
BEGIN
  SELECT id INTO v_pl FROM cat.plan_modelo WHERE codigo = 'PGC_PYMES';

  INSERT INTO core.nif (pais_id, tipo_documento, numero, razon_social)
  SELECT id, 'CIF', 'B12345678', 'Demo Canarias S.L.' FROM cat.pais WHERE iso2 = 'ES' RETURNING id INTO v_nif;

  INSERT INTO core.empresa (nif_id, codigo, nombre, forma_juridica, plan_modelo_id, impuesto_indirecto, tributa_is)
  VALUES (v_nif, 'DEMO', 'Demo Canarias S.L.', 'SL', v_pl, 'IGIC', true) RETURNING id INTO v_emp;

  INSERT INTO core.ejercicio (empresa_id, codigo, fecha_inicio, fecha_fin)
  VALUES (v_emp, '2026/27', '2026-07-01', '2027-06-30') RETURNING id INTO v_ej;

  INSERT INTO core.periodo (ejercicio_id, numero, tipo, fecha_inicio, fecha_fin)
  SELECT v_ej, n, 'NORMAL',
         (date '2026-07-01' + make_interval(months => n - 1))::date,
         (date '2026-07-01' + make_interval(months => n) - interval '1 day')::date
  FROM generate_series(1, 12) n;

  INSERT INTO conta.diario (empresa_id, codigo, nombre, tipo) VALUES
    (v_emp, 'GEN',  'Diario general', 'GENERAL'),
    (v_emp, 'COMP', 'Compras', 'COMPRAS'),
    (v_emp, 'VENT', 'Ventas', 'VENTAS'),
    (v_emp, 'BANC', 'Bancos', 'BANCOS');

  INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, naturaleza) VALUES
    (v_emp, '10000000', 'Capital social', 4, 'ACREEDORA'),
    (v_emp, '12900000', 'Resultado del ejercicio', 4, 'MIXTA'),
    (v_emp, '47000000', 'H.P. deudora por IGIC', 4, 'DEUDORA'),
    (v_emp, '47500000', 'H.P. acreedora por IGIC', 4, 'ACREEDORA'),
    (v_emp, '40000000', 'Proveedores', 4, 'ACREEDORA'),
    (v_emp, '43000000', 'Clientes', 4, 'DEUDORA'),
    (v_emp, '47200000', 'H.P. IGIC soportado', 4, 'DEUDORA'),
    (v_emp, '47700000', 'H.P. IGIC repercutido', 4, 'ACREEDORA'),
    (v_emp, '57200000', 'Bancos c/c', 4, 'DEUDORA'),
    (v_emp, '60000000', 'Compras de mercaderías', 4, 'DEUDORA'),
    (v_emp, '62900000', 'Otros servicios', 4, 'DEUDORA'),
    (v_emp, '70000000', 'Ventas de mercaderías', 4, 'ACREEDORA');

  -- Canales
  INSERT INTO canal.canal (empresa_id, codigo, nombre, tipo) VALUES
    (v_emp, '1', 'Canal 1', 'BASE'), (v_emp, '2', 'Canal 2', 'BASE'), (v_emp, '3', 'Canal 3', 'BASE'),
    (v_emp, 'A', 'A = 1+2+3', 'DERIVADO'), (v_emp, 'B', 'B = 1+2', 'DERIVADO'),
    (v_emp, 'C', 'C = 1+3', 'DERIVADO'),   (v_emp, 'D', 'D = 2+3', 'DERIVADO'),
    (v_emp, 'E', 'E = 60% canal 1 / resto canal 2', 'DERIVADO');

  INSERT INTO canal.canal_version (canal_id, vigente_desde, descripcion)
  SELECT id, date '2026-01-01', 'Versión inicial' FROM canal.canal WHERE empresa_id = v_emp AND tipo = 'DERIVADO';

  -- A, B, C, D: importe COMPLETO a cada canal base indicado
  INSERT INTO canal.canal_version_linea (canal_version_id, orden, canal_base_id, modo, porcentaje)
  SELECT v.id, (row_number() OVER (PARTITION BY d.codigo ORDER BY b.codigo))::smallint, b.id, 'PORCENTAJE', 100
  FROM (VALUES ('A','1'),('A','2'),('A','3'),('B','1'),('B','2'),('C','1'),('C','3'),('D','2'),('D','3')) m(der, bas)
  JOIN canal.canal d ON d.empresa_id = v_emp AND d.codigo = m.der
  JOIN canal.canal_version v ON v.canal_id = d.id
  JOIN canal.canal b ON b.empresa_id = v_emp AND b.codigo = m.bas;

  -- E: 60% por fórmula al canal 1 y el resto al canal 2
  INSERT INTO canal.canal_version_linea (canal_version_id, orden, canal_base_id, modo, expresion)
  SELECT v.id, 1, b.id, 'FORMULA', 'IMPORTE * 0.6'
  FROM canal.canal e JOIN canal.canal_version v ON v.canal_id = e.id
  JOIN canal.canal b ON b.empresa_id = v_emp AND b.codigo = '1'
  WHERE e.empresa_id = v_emp AND e.codigo = 'E';

  INSERT INTO canal.canal_version_linea (canal_version_id, orden, canal_base_id, modo)
  SELECT v.id, 2, b.id, 'RESTO'
  FROM canal.canal e JOIN canal.canal_version v ON v.canal_id = e.id
  JOIN canal.canal b ON b.empresa_id = v_emp AND b.codigo = '2'
  WHERE e.empresa_id = v_emp AND e.codigo = 'E';
END $$;
