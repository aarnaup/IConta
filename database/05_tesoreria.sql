-- Tesorería: vencimientos generados desde facturas, cobros y pagos con su asiento, cuentas bancarias.
-- Ejecutar DESPUÉS de 04_seguridad.sql.

-- Plazos de un mismo documento (1/3, 2/3, 3/3)
ALTER TABLE tes.vencimiento
  ADD COLUMN num_plazo    smallint NOT NULL DEFAULT 1,
  ADD COLUMN total_plazos smallint NOT NULL DEFAULT 1,
  ADD CONSTRAINT ck_venc_importes CHECK (importe > 0 AND importe_pendiente >= 0 AND importe_pendiente <= importe),
  ADD CONSTRAINT ck_venc_liquidado CHECK (estado <> 'LIQUIDADO' OR importe_pendiente = 0),
  ADD CONSTRAINT ck_venc_plazos CHECK (num_plazo BETWEEN 1 AND total_plazos);
CREATE INDEX ix_vencimiento_factura ON tes.vencimiento(factura_id);
CREATE INDEX ix_vencimiento_tercero ON tes.vencimiento(empresa_id, tercero_id);

ALTER TABLE tes.cobro_pago
  ADD COLUMN creado_por bigint REFERENCES core.usuario(id),
  ADD CONSTRAINT ck_cobro_importe CHECK (importe > 0);
CREATE INDEX ix_cobro_pago_venc ON tes.cobro_pago(vencimiento_id);

CREATE INDEX ix_mov_banc_cuenta ON tes.movimiento_bancario(cuenta_bancaria_id, fecha);

-- Formas de pago habituales para las empresas que ya existen (las nuevas las crea el alta de empresa)
INSERT INTO core.forma_pago (empresa_id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos)
SELECT e.id, v.codigo, v.nombre, v.tipo, v.plazos, v.d1, v.d2
FROM core.empresa e
CROSS JOIN (VALUES
  ('CONT',  'Contado',                'EFECTIVO',      1,  0,  0),
  ('TRF',   'Transferencia',          'TRANSFERENCIA', 1,  0,  0),
  ('TRF30', 'Transferencia a 30 días','TRANSFERENCIA', 1, 30,  0),
  ('REC30', 'Recibo a 30 días',       'RECIBO',        1, 30,  0),
  ('REC3060','Recibo 30/60 días',     'RECIBO',        2, 30, 30)
) AS v(codigo, nombre, tipo, plazos, d1, d2)
ON CONFLICT (empresa_id, codigo) DO NOTHING;
