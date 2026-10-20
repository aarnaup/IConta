using System.Text.Json;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IAuditoriaRepositorio
{
    Task RegistrarAsync(long? usuarioId, long? empresaId, string accion, string? entidad, long? entidadId, object? detalle, CancellationToken ct);
}

public sealed class AuditoriaRepositorio(IConexionFactory conexiones) : IAuditoriaRepositorio
{
    public async Task RegistrarAsync(long? usuarioId, long? empresaId, string accion, string? entidad, long? entidadId, object? detalle, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO core.auditoria (usuario_id, empresa_id, accion, entidad, entidad_id, detalle)
            VALUES (@usuarioId, @empresaId, @accion, @entidad, @entidadId, @detalle::jsonb)
            """, new
        {
            usuarioId, empresaId, accion, entidad, entidadId,
            detalle = detalle is null ? null : JsonSerializer.Serialize(detalle)
        }, cancellationToken: ct));
    }
}
