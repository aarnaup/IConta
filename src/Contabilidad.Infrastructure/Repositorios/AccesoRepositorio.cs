using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IAccesoRepositorio
{
    /// <summary>Acceso del usuario a la empresa, o null si no lo tiene (usuario inactivo, empresa inactiva o sin asignar).</summary>
    Task<AccesoEmpresa?> ObtenerAsync(long usuarioId, long empresaId, CancellationToken ct);
    Task<IReadOnlyList<EmpresaDto>> ListarEmpresasAsync(long usuarioId, CancellationToken ct);
}

public sealed class AccesoRepositorio(IConexionFactory conexiones) : IAccesoRepositorio
{
    private sealed class BaseFila
    {
        public string Tipo { get; set; } = "";
        public bool EsSuperadmin { get; set; }
        public string? Rol { get; set; }
        public bool? SaltaRevision { get; set; }
    }
    private sealed class PermisoFila { public string Modulo { get; set; } = ""; public string Nivel { get; set; } = ""; }

    public async Task<AccesoEmpresa?> ObtenerAsync(long usuarioId, long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var b = await cn.QuerySingleOrDefaultAsync<BaseFila>(new CommandDefinition("""
            SELECT u.tipo, u.es_superadmin, ue.rol, ue.salta_revision
            FROM core.usuario u
            JOIN core.empresa e ON e.id = @empresaId AND e.activa
            LEFT JOIN core.usuario_empresa ue ON ue.usuario_id = u.id AND ue.empresa_id = e.id
            WHERE u.id = @usuarioId AND u.activo
            """, new { usuarioId, empresaId }, cancellationToken: ct));

        if (b is null) return null;
        if (b.EsSuperadmin) return AccesoEmpresa.Total();
        if (b.Rol is null) return null;

        var filas = await cn.QueryAsync<PermisoFila>(new CommandDefinition(
            "SELECT modulo, nivel FROM core.usuario_empresa_permiso WHERE usuario_id = @usuarioId AND empresa_id = @empresaId",
            new { usuarioId, empresaId }, cancellationToken: ct));

        var explicitos = new Dictionary<Modulo, Nivel>();
        foreach (var f in filas)
            if (Enum.TryParse<Modulo>(f.Modulo, true, out var m) && Enum.TryParse<Nivel>(f.Nivel, true, out var n))
                explicitos[m] = n;

        return new AccesoEmpresa(b.Rol, b.Tipo == "GESTOR", b.SaltaRevision ?? false, explicitos);
    }

    public async Task<IReadOnlyList<EmpresaDto>> ListarEmpresasAsync(long usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT e.id, e.codigo, e.nombre, n.numero AS nif, e.impuesto_indirecto,
                   CASE WHEN u.es_superadmin THEN 'ADMIN' ELSE ue.rol END AS rol_en_empresa
            FROM core.usuario u
            JOIN core.empresa e ON e.activa
            JOIN core.nif n ON n.id = e.nif_id
            LEFT JOIN core.usuario_empresa ue ON ue.usuario_id = u.id AND ue.empresa_id = e.id
            WHERE u.id = @usuarioId AND u.activo AND (u.es_superadmin OR ue.usuario_id IS NOT NULL)
            ORDER BY e.nombre
            """;
        return (await cn.QueryAsync<EmpresaDto>(new CommandDefinition(sql, new { usuarioId }, cancellationToken: ct))).AsList();
    }
}
