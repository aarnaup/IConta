using Contabilidad.Domain;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ICatalogoFacturacionRepositorio
{
    Task<IReadOnlyList<TipoIvaDto>> ListarTiposIvaAsync(long empresaId, DateOnly fecha, CancellationToken ct);
    Task<IReadOnlyList<TipoRetencionDto>> ListarTiposRetencionAsync(DateOnly fecha, CancellationToken ct);
    Task<IReadOnlyList<PlantillaDto>> ListarPlantillasAsync(long empresaId, CancellationToken ct);
}

public sealed class CatalogoFacturacionRepositorio(IConexionFactory conexiones) : ICatalogoFacturacionRepositorio
{
    /// <summary>Tipos con % vigente en la fecha y del impuesto que usa la empresa (IVA o IGIC).</summary>
    public async Task<IReadOnlyList<TipoIvaDto>> ListarTiposIvaAsync(long empresaId, DateOnly fecha, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT t.id, t.codigo, t.nombre, t.impuesto, t.es_exento, v.porcentaje, v.recargo_equivalencia AS recargo
            FROM cat.tipo_iva t
            JOIN cat.tipo_iva_vigencia v ON v.tipo_iva_id = t.id
                 AND v.fecha_desde <= @fecha AND (v.fecha_hasta IS NULL OR v.fecha_hasta > @fecha)
            JOIN core.empresa e ON e.id = @empresaId AND (e.impuesto_indirecto = t.impuesto OR e.impuesto_indirecto = 'EXENTO')
            WHERE t.activo
            ORDER BY t.impuesto, v.porcentaje DESC
            """;
        return (await cn.QueryAsync<TipoIvaDto>(new CommandDefinition(sql, new { empresaId, fecha }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<TipoRetencionDto>> ListarTiposRetencionAsync(DateOnly fecha, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT t.id, t.codigo, t.nombre, t.naturaleza, v.porcentaje
            FROM cat.tipo_retencion_irpf t
            JOIN cat.tipo_retencion_irpf_vigencia v ON v.tipo_retencion_id = t.id
                 AND v.fecha_desde <= @fecha AND (v.fecha_hasta IS NULL OR v.fecha_hasta > @fecha)
            WHERE t.activo ORDER BY t.nombre
            """;
        return (await cn.QueryAsync<TipoRetencionDto>(new CommandDefinition(sql, new { fecha }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<PlantillaDto>> ListarPlantillasAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT id, codigo, nombre, tipo_operacion, (empresa_id IS NULL) AS es_global
            FROM conta.plantilla_asiento
            WHERE (empresa_id = @empresaId OR empresa_id IS NULL) AND activa
            ORDER BY tipo_operacion, nombre
            """;
        return (await cn.QueryAsync<PlantillaDto>(new CommandDefinition(sql, new { empresaId }, cancellationToken: ct))).AsList();
    }
}
