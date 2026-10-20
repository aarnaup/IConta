using Contabilidad.Domain;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ICatalogoRepositorio
{
    Task<IReadOnlyList<EjercicioDto>> ListarEjerciciosAsync(long empresaId, CancellationToken ct);
    Task<IReadOnlyList<CuentaDto>> ListarCuentasAsync(long empresaId, string? q, CancellationToken ct);
    Task<IReadOnlyList<DiarioDto>> ListarDiariosAsync(long empresaId, CancellationToken ct);
}

public sealed class CatalogoRepositorio(IConexionFactory conexiones) : ICatalogoRepositorio
{
    public async Task<IReadOnlyList<EjercicioDto>> ListarEjerciciosAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT id, codigo, fecha_inicio, fecha_fin, estado, es_partido
            FROM core.ejercicio WHERE empresa_id = @empresaId ORDER BY fecha_inicio DESC
            """;
        return (await cn.QueryAsync<EjercicioDto>(new CommandDefinition(sql, new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<CuentaDto>> ListarCuentasAsync(long empresaId, string? q, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT id, codigo, nombre FROM conta.cuenta
            WHERE empresa_id = @empresaId AND es_imputable AND activa
              AND (@q::text IS NULL OR codigo LIKE @q || '%' OR nombre ILIKE '%' || @q || '%')
            ORDER BY codigo LIMIT 2000
            """;
        return (await cn.QueryAsync<CuentaDto>(new CommandDefinition(sql, new { empresaId, q }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<DiarioDto>> ListarDiariosAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = "SELECT id, codigo, nombre, tipo FROM conta.diario WHERE empresa_id = @empresaId AND activo ORDER BY codigo";
        return (await cn.QueryAsync<DiarioDto>(new CommandDefinition(sql, new { empresaId }, cancellationToken: ct))).AsList();
    }
}
