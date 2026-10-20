using Npgsql;

namespace Contabilidad.Infrastructure;

public interface IConexionFactory
{
    Task<NpgsqlConnection> AbrirAsync(CancellationToken ct = default);
}

public sealed class ConexionFactory(NpgsqlDataSource dataSource) : IConexionFactory
{
    public async Task<NpgsqlConnection> AbrirAsync(CancellationToken ct = default)
        => await dataSource.OpenConnectionAsync(ct);
}
