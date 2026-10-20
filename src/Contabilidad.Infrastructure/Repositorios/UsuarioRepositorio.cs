using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IUsuarioRepositorio
{
    // ---- autenticación ----
    Task<UsuarioLogin?> ObtenerLoginPorEmailAsync(string email, CancellationToken ct);
    Task<UsuarioLogin?> ObtenerLoginPorIdAsync(long id, CancellationToken ct);
    Task RegistrarFalloAsync(long id, CancellationToken ct);
    Task RegistrarAccesoAsync(long id, CancellationToken ct);
    Task CambiarPasswordAsync(long id, string hash, bool debeCambiar, CancellationToken ct);
    Task<UsuarioDto?> ObtenerAsync(long id, CancellationToken ct);

    // ---- administración ----
    Task<IReadOnlyList<UsuarioAdminDto>> ListarAsync(CancellationToken ct);
    Task<long> CrearAsync(string email, string nombre, string tipo, bool esSuperadmin, string hash, bool debeCambiar, CancellationToken ct);
    Task SetActivoAsync(long id, bool activo, CancellationToken ct);
    Task<IReadOnlyList<AsignacionDto>> ListarAsignacionesAsync(long usuarioId, CancellationToken ct);
    Task AsignarEmpresaAsync(long usuarioId, long empresaId, AsignarEmpresaCmd cmd, CancellationToken ct);
    Task DesasignarEmpresaAsync(long usuarioId, long empresaId, CancellationToken ct);
}

public sealed class UsuarioRepositorio(IConexionFactory conexiones) : IUsuarioRepositorio
{
    private const string ColumnasLogin = """
        id, email, nombre, tipo, es_superadmin, activo, debe_cambiar_password, password_hash, bloqueado_hasta
        """;

    public async Task<UsuarioLogin?> ObtenerLoginPorEmailAsync(string email, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<UsuarioLogin>(new CommandDefinition(
            $"SELECT {ColumnasLogin} FROM core.usuario WHERE lower(email) = lower(@email)", new { email }, cancellationToken: ct));
    }

    public async Task<UsuarioLogin?> ObtenerLoginPorIdAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<UsuarioLogin>(new CommandDefinition(
            $"SELECT {ColumnasLogin} FROM core.usuario WHERE id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Tras 5 fallos seguidos bloquea la cuenta 15 minutos. Pasado el bloqueo, el contador vuelve a empezar.</summary>
    public async Task RegistrarFalloAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE core.usuario SET
              intentos_fallidos = CASE WHEN bloqueado_hasta <= now() THEN 1 ELSE intentos_fallidos + 1 END,
              bloqueado_hasta   = CASE WHEN bloqueado_hasta <= now() THEN NULL
                                       WHEN intentos_fallidos + 1 >= 5 THEN now() + interval '15 minutes'
                                       ELSE bloqueado_hasta END
            WHERE id = @id
            """, new { id }, cancellationToken: ct));
    }

    public async Task RegistrarAccesoAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE core.usuario SET intentos_fallidos = 0, bloqueado_hasta = NULL, ultimo_acceso = now() WHERE id = @id",
            new { id }, cancellationToken: ct));
    }

    public async Task CambiarPasswordAsync(long id, string hash, bool debeCambiar, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE core.usuario SET password_hash = @hash, debe_cambiar_password = @debeCambiar,
                                    intentos_fallidos = 0, bloqueado_hasta = NULL
            WHERE id = @id
            """, new { id, hash, debeCambiar }, cancellationToken: ct));
    }

    public async Task<UsuarioDto?> ObtenerAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<UsuarioDto>(new CommandDefinition(
            "SELECT id, email, nombre, tipo, es_superadmin, activo, debe_cambiar_password FROM core.usuario WHERE id = @id",
            new { id }, cancellationToken: ct));
    }

    // ------------------------------------------------------------------ administración
    public async Task<IReadOnlyList<UsuarioAdminDto>> ListarAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT u.id, u.email, u.nombre, u.tipo, u.es_superadmin, u.activo, u.ultimo_acceso,
                   (SELECT count(*) FROM core.usuario_empresa ue WHERE ue.usuario_id = u.id)::int AS num_empresas
            FROM core.usuario u ORDER BY u.nombre
            """;
        return (await cn.QueryAsync<UsuarioAdminDto>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
    }

    public async Task<long> CrearAsync(string email, string nombre, string tipo, bool esSuperadmin, string hash, bool debeCambiar, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        try
        {
            return await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.usuario (email, nombre, tipo, es_superadmin, password_hash, debe_cambiar_password)
                VALUES (@email, @nombre, @tipo, @esSuperadmin, @hash, @debeCambiar) RETURNING id
                """, new { email, nombre, tipo, esSuperadmin, hash, debeCambiar }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Ya existe un usuario con ese email.");
        }
    }

    public async Task SetActivoAsync(long id, bool activo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("UPDATE core.usuario SET activo = @activo WHERE id = @id",
            new { id, activo }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("El usuario no existe.");
    }

    private sealed class AsignacionFila
    {
        public long EmpresaId { get; set; }
        public string EmpresaNombre { get; set; } = "";
        public string Rol { get; set; } = "";
        public bool SaltaRevision { get; set; }
    }
    private sealed class PermisoFila { public long EmpresaId { get; set; } public string Modulo { get; set; } = ""; public string Nivel { get; set; } = ""; }

    public async Task<IReadOnlyList<AsignacionDto>> ListarAsignacionesAsync(long usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var filas = (await cn.QueryAsync<AsignacionFila>(new CommandDefinition("""
            SELECT ue.empresa_id, e.nombre AS empresa_nombre, ue.rol, ue.salta_revision
            FROM core.usuario_empresa ue JOIN core.empresa e ON e.id = ue.empresa_id
            WHERE ue.usuario_id = @usuarioId ORDER BY e.nombre
            """, new { usuarioId }, cancellationToken: ct))).AsList();
        var permisos = (await cn.QueryAsync<PermisoFila>(new CommandDefinition(
            "SELECT empresa_id, modulo, nivel FROM core.usuario_empresa_permiso WHERE usuario_id = @usuarioId",
            new { usuarioId }, cancellationToken: ct))).ToLookup(p => p.EmpresaId);

        return filas.Select(f => new AsignacionDto
        {
            EmpresaId = f.EmpresaId, EmpresaNombre = f.EmpresaNombre, Rol = f.Rol, SaltaRevision = f.SaltaRevision,
            Permisos = permisos[f.EmpresaId].ToDictionary(p => p.Modulo, p => p.Nivel)
        }).ToList();
    }

    public async Task AsignarEmpresaAsync(long usuarioId, long empresaId, AsignarEmpresaCmd cmd, CancellationToken ct)
    {
        var rol = (cmd.Rol ?? "").ToUpperInvariant();
        if (!AccesoEmpresa.RolesValidos.Contains(rol))
            throw new ReglaContableException($"Rol no válido. Usa: {string.Join(", ", AccesoEmpresa.RolesValidos)}.");

        var permisos = new Dictionary<string, string>();
        foreach (var (modulo, nivel) in cmd.Permisos ?? [])
        {
            if (string.IsNullOrWhiteSpace(nivel)) continue;     // vacío = según el rol
            if (!Enum.TryParse<Modulo>(modulo, true, out var m)) throw new ReglaContableException($"Módulo no válido: {modulo}.");
            if (!Enum.TryParse<Nivel>(nivel, true, out var n)) throw new ReglaContableException($"Nivel no válido: {nivel}.");
            permisos[m.ToString().ToUpperInvariant()] = n.ToString().ToUpperInvariant();
        }

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var ok = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM core.usuario WHERE id = @usuarioId) AND EXISTS (SELECT 1 FROM core.empresa WHERE id = @empresaId)
            """, new { usuarioId, empresaId }, tx, cancellationToken: ct));
        if (!ok) throw new ReglaContableException("El usuario o la empresa no existen.");

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO core.usuario_empresa (usuario_id, empresa_id, rol, salta_revision)
            VALUES (@usuarioId, @empresaId, @rol, @saltaRevision)
            ON CONFLICT (usuario_id, empresa_id) DO UPDATE SET rol = EXCLUDED.rol, salta_revision = EXCLUDED.salta_revision
            """, new { usuarioId, empresaId, rol, saltaRevision = cmd.SaltaRevision }, tx, cancellationToken: ct));

        await cn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM core.usuario_empresa_permiso WHERE usuario_id = @usuarioId AND empresa_id = @empresaId",
            new { usuarioId, empresaId }, tx, cancellationToken: ct));

        if (permisos.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO core.usuario_empresa_permiso (usuario_id, empresa_id, modulo, nivel) VALUES (@usuarioId, @empresaId, @modulo, @nivel)
                """, permisos.Select(p => new { usuarioId, empresaId, modulo = p.Key, nivel = p.Value }), tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task DesasignarEmpresaAsync(long usuarioId, long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM core.usuario_empresa WHERE usuario_id = @usuarioId AND empresa_id = @empresaId",
            new { usuarioId, empresaId }, cancellationToken: ct));   // los permisos caen por ON DELETE CASCADE
    }
}
