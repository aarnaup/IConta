-- Demo de facturación para la empresa DEMO (IGIC, Canarias). Ejecutar tras 01_schema.sql y 02_demo.sql.
-- IMPORTANTE: los % de IGIC y de retención son valores de ejemplo; verifícalos con la normativa vigente (ATC / AEAT).
DO $$
DECLARE v_emp bigint; v_pais smallint; v_nif bigint; c_cli bigint; c_pro bigint; v_pl bigint; v_epi int;
BEGIN
  SELECT id INTO v_emp FROM core.empresa WHERE codigo = 'DEMO';
  SELECT id INTO v_pais FROM cat.pais WHERE iso2 = 'ES';

  -- IGIC: vigencias (el seed del esquema no las carga)
  INSERT INTO cat.tipo_iva_vigencia (tipo_iva_id, porcentaje, recargo_equivalencia, fecha_desde)
  SELECT t.id, v.pct, 0, date '2012-07-01'
  FROM cat.tipo_iva t JOIN (VALUES ('IGIC_GEN',7.0),('IGIC_RED',3.0),('IGIC_INC',9.5),('IGIC_EXE',0.0)) v(cod,pct) ON v.cod = t.codigo
  WHERE NOT EXISTS (SELECT 1 FROM cat.tipo_iva_vigencia x WHERE x.tipo_iva_id = t.id);

  -- Epígrafe profesional de demostración + actividad de la empresa (permite retención IRPF en emitidas)
  INSERT INTO cat.epigrafe_iae (seccion, codigo, descripcion, es_profesional)
  VALUES (2, 'DEMO-P', 'Epígrafe profesional de demostración', true)
  ON CONFLICT (seccion, codigo) DO NOTHING;
  SELECT id INTO v_epi FROM cat.epigrafe_iae WHERE seccion = 2 AND codigo = 'DEMO-P';
  INSERT INTO core.empresa_actividad (empresa_id, epigrafe_id, nombre, es_principal, fecha_alta)
  VALUES (v_emp, v_epi, 'Servicios profesionales', true, '2026-01-01');

  -- Cuentas adicionales
  INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, naturaleza) VALUES
    (v_emp, '47300000', 'H.P. retenciones y pagos a cuenta', 4, 'DEUDORA'),
    (v_emp, '47510000', 'H.P. acreedora por retenciones practicadas', 4, 'ACREEDORA'),
    (v_emp, '70500000', 'Prestaciones de servicios', 4, 'ACREEDORA'),
    (v_emp, '43000001', 'Cliente Demo Cliente S.L.', 4, 'DEUDORA'),
    (v_emp, '40000001', 'Proveedor Demo Proveedor S.L.', 4, 'ACREEDORA');

  -- Cuentas automáticas por rol
  INSERT INTO conta.cuenta_automatica (empresa_id, rol, cuenta_id)
  SELECT v_emp, m.rol, c.id FROM (VALUES
    ('IVA_SOPORTADO','47200000'), ('IVA_REPERCUTIDO','47700000'),
    ('RETENCION_SOPORTADA','47300000'), ('RETENCION_PRACTICADA','47510000'),
    ('COMPRAS','60000000'), ('VENTAS','70500000')) m(rol, cod)
  JOIN conta.cuenta c ON c.empresa_id = v_emp AND c.codigo = m.cod;

  -- Terceros demo
  INSERT INTO core.nif (pais_id, tipo_documento, numero, razon_social) VALUES (v_pais,'CIF','B76000001','Demo Cliente S.L.') RETURNING id INTO v_nif;
  SELECT id INTO c_cli FROM conta.cuenta WHERE empresa_id = v_emp AND codigo = '43000001';
  INSERT INTO conta.tercero (empresa_id, nif_id, codigo, nombre, es_cliente, cuenta_cliente_id) VALUES (v_emp, v_nif, '43000001', 'Demo Cliente S.L.', true, c_cli);

  INSERT INTO core.nif (pais_id, tipo_documento, numero, razon_social) VALUES (v_pais,'CIF','B76000002','Demo Proveedor S.L.') RETURNING id INTO v_nif;
  SELECT id INTO c_pro FROM conta.cuenta WHERE empresa_id = v_emp AND codigo = '40000001';
  INSERT INTO conta.tercero (empresa_id, nif_id, codigo, nombre, es_proveedor, cuenta_proveedor_id) VALUES (v_emp, v_nif, '40000001', 'Demo Proveedor S.L.', true, c_pro);

  -- Prorrata provisional de ejemplo (año natural 2026, sin sector diferenciado)
  INSERT INTO fiscal.prorrata (empresa_id, anio, tipo, porcentaje_provisional, estado, observaciones)
  VALUES (v_emp, 2026, 'GENERAL', 80, 'PROVISIONAL', 'Demo');

  -- Plantillas globales (empresa_id NULL): se resuelven por rol con conta.cuenta_automatica
  INSERT INTO conta.plantilla_asiento (empresa_id, codigo, nombre, tipo_operacion, concepto_modelo)
  VALUES (NULL, 'VENTA_ESTANDAR', 'Venta de bienes o servicios', 'VENTA', 'Factura {factura} {tercero}')
  RETURNING id INTO v_pl;
  INSERT INTO conta.plantilla_asiento_linea (plantilla_id, orden, cuenta_rol, lado, origen_importe, usa_tercero) VALUES
    (v_pl, 1, 'CLIENTE',              'DEBE',  'TOTAL',     true),
    (v_pl, 2, 'RETENCION_SOPORTADA',  'DEBE',  'RETENCION', false),
    (v_pl, 3, 'VENTAS',               'HABER', 'BASE',      false),
    (v_pl, 4, 'IVA_REPERCUTIDO',      'HABER', 'CUOTA',     false),
    (v_pl, 5, 'IVA_REPERCUTIDO',      'HABER', 'RECARGO',   false);

  INSERT INTO conta.plantilla_asiento (empresa_id, codigo, nombre, tipo_operacion, concepto_modelo)
  VALUES (NULL, 'COMPRA_ESTANDAR', 'Compra de bienes o servicios', 'COMPRA', 'Factura {factura} {tercero}')
  RETURNING id INTO v_pl;
  INSERT INTO conta.plantilla_asiento_linea (plantilla_id, orden, cuenta_rol, lado, origen_importe, usa_tercero) VALUES
    (v_pl, 1, 'PROVEEDOR',            'HABER', 'TOTAL',              true),
    (v_pl, 2, 'RETENCION_PRACTICADA', 'HABER', 'RETENCION',          false),
    (v_pl, 3, 'COMPRAS',              'DEBE',  'BASE',               false),
    (v_pl, 4, 'IVA_SOPORTADO',        'DEBE',  'CUOTA',              false),
    (v_pl, 5, 'COMPRAS',              'DEBE',  'CUOTA_NO_DEDUCIBLE', false),
    (v_pl, 6, 'COMPRAS',              'DEBE',  'RECARGO',            false);
END $$;
