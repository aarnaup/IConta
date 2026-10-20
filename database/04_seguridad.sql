-- Seguridad: usuarios, permisos por módulo, revisión de borradores y auditoría.
-- Ejecutar DESPUÉS de 01_schema.sql (y de las demos, si las usas).
-- El primer usuario (superadmin) se crea con:  dotnet run --project src/Contabilidad.Api -- crear-superadmin <email> "<nombre>"

ALTER TABLE core.usuario
  ADD COLUMN tipo                  varchar(10) NOT NULL DEFAULT 'EMPRESA' CHECK (tipo IN ('GESTOR','EMPRESA')),
  ADD COLUMN es_superadmin         boolean     NOT NULL DEFAULT false,
  ADD COLUMN debe_cambiar_password boolean     NOT NULL DEFAULT false,
  ADD COLUMN intentos_fallidos     smallint    NOT NULL DEFAULT 0,
  ADD COLUMN bloqueado_hasta       timestamptz,
  ADD COLUMN ultimo_acceso         timestamptz,
  ADD CONSTRAINT ck_superadmin_es_gestor CHECK (NOT es_superadmin OR tipo = 'GESTOR');

-- El email no distingue mayúsculas
ALTER TABLE core.usuario DROP CONSTRAINT IF EXISTS usuario_email_key;
CREATE UNIQUE INDEX ux_usuario_email ON core.usuario (lower(email));

-- Un usuario de empresa con este indicador contabiliza directamente; sin él, todo queda en BORRADOR
ALTER TABLE core.usuario_empresa ADD COLUMN salta_revision boolean NOT NULL DEFAULT false;

-- Permisos por módulo y empresa. Sin fila => nivel por defecto según el rol (ADMIN/CONTABLE: escritura, LECTURA: lectura)
CREATE TABLE core.usuario_empresa_permiso (
  usuario_id bigint      NOT NULL,
  empresa_id bigint      NOT NULL,
  modulo     varchar(20) NOT NULL CHECK (modulo IN
    ('CONTABILIDAD','FACTURACION','TESORERIA','INMOVILIZADO','ANALITICA','PRESUPUESTOS','BALANCES','FISCAL')),
  nivel      varchar(10) NOT NULL CHECK (nivel IN ('NINGUNO','LECTURA','ESCRITURA')),
  PRIMARY KEY (usuario_id, empresa_id, modulo),
  FOREIGN KEY (usuario_id, empresa_id) REFERENCES core.usuario_empresa(usuario_id, empresa_id) ON DELETE CASCADE
);

-- Quién creó cada factura y el comando original (permite contabilizarla después al aprobar el borrador)
ALTER TABLE fiscal.factura
  ADD COLUMN creado_por bigint REFERENCES core.usuario(id),
  ADD COLUMN comando    jsonb;

CREATE TABLE core.auditoria (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  fecha      timestamptz NOT NULL DEFAULT now(),
  usuario_id bigint REFERENCES core.usuario(id),
  empresa_id bigint REFERENCES core.empresa(id),
  accion     varchar(40)  NOT NULL,      -- USUARIO_CREADO, PERMISOS_ASIGNADOS, ASIENTO_APROBADO, FACTURA_RECHAZADA...
  entidad    varchar(40),
  entidad_id bigint,
  detalle    jsonb
);
CREATE INDEX ix_auditoria_empresa ON core.auditoria(empresa_id, fecha DESC);
CREATE INDEX ix_auditoria_usuario ON core.auditoria(usuario_id, fecha DESC);
