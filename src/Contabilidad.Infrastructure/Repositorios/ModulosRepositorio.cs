using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public sealed class ModulosDto
{
    public long? ActividadId { get; set; }
    public string Descripcion { get; set; } = "";
    public decimal RendimientoNetoIrpf { get; set; }
    public decimal PersonasAsalariadas { get; set; }
    public bool Agricola { get; set; }
    public decimal CuotaIvaDevengada { get; set; }
    public decimal CuotaIvaSoportada { get; set; }
    public decimal PorcentajeIngresoIva { get; set; }
    public decimal PorcentajeCuotaMinima { get; set; }
}

public interface IModulosRepositorio
{
    Task<IReadOnlyList<ModulosDto>> ListarAsync(long empresaId, int anio, CancellationToken ct);
    Task<IReadOnlyList<ModulosDto>> GuardarAsync(long empresaId, int anio, IReadOnlyList<ModulosDto> actividades, CancellationToken ct);
}

/// <summary>Módulos (estimación objetiva y régimen simplificado) de cada actividad y año, con los importes de la orden de módulos.</summary>
public sealed class ModulosRepositorio(IConexionFactory conexiones) : IModulosRepositorio
{
    private const string Select = """
        SELECT actividad_id, descripcion, rendimiento_neto_irpf, personas_asalariadas, agricola, cuota_iva_devengada, cuota_iva_soportada,
               porcentaje_ingreso_iva, porcentaje_cuota_minima
        FROM fiscal.modulos WHERE empresa_id = @empresaId AND anio = @anio ORDER BY id
        """;

    public async Task<IReadOnlyList<ModulosDto>> ListarAsync(long empresaId, int anio, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<ModulosDto>(new CommandDefinition(Select, new { empresaId, anio = (short)anio }, cancellationToken: ct))).AsList();
    }

    internal static async Task<IReadOnlyList<ModulosActividad>> DelAnioAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, int anio,
        CancellationToken ct)
        => (await cn.QueryAsync<ModulosDto>(new CommandDefinition(Select, new { empresaId, anio = (short)anio }, tx, cancellationToken: ct)))
            .Select(m => new ModulosActividad(m.ActividadId, m.Descripcion, m.RendimientoNetoIrpf, m.PersonasAsalariadas, m.Agricola,
                m.CuotaIvaDevengada, m.CuotaIvaSoportada, m.PorcentajeIngresoIva, m.PorcentajeCuotaMinima)).ToList();

    public async Task<IReadOnlyList<ModulosDto>> GuardarAsync(long empresaId, int anio, IReadOnlyList<ModulosDto> actividades, CancellationToken ct)
    {
        if (anio is < 2000 or > 2100) throw new ReglaContableException("El año no es válido.");
        foreach (var a in actividades)
        {
            a.Descripcion = (a.Descripcion ?? "").Trim();
            if (a.Descripcion.Length is 0 or > 100) throw new ReglaContableException("Cada actividad necesita una descripción (hasta 100 caracteres).");
            if (a.RendimientoNetoIrpf < 0 || a.PersonasAsalariadas < 0 || a.CuotaIvaDevengada < 0 || a.CuotaIvaSoportada < 0)
                throw new ReglaContableException($"{a.Descripcion}: los importes de los módulos no pueden ser negativos.");
            if (a.PorcentajeIngresoIva is < 0 or > 100 || a.PorcentajeCuotaMinima is < 0 or > 100)
                throw new ReglaContableException($"{a.Descripcion}: los porcentajes van de 0 a 100.");
        }
        if (actividades.GroupBy(a => a.Descripcion, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ReglaContableException("Hay dos actividades con la misma descripción.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var ids = actividades.Where(a => a.ActividadId is not null).Select(a => a.ActividadId!.Value).Distinct().ToArray();
        if (ids.Length > 0 && await cn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM core.empresa_actividad WHERE empresa_id = @empresaId AND id = ANY(@ids)", new { empresaId, ids }, tx,
                cancellationToken: ct)) != ids.Length)
            throw new ReglaContableException("Alguna actividad no es de la empresa.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM fiscal.modulos WHERE empresa_id = @empresaId AND anio = @anio",
            new { empresaId, anio = (short)anio }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.modulos (empresa_id, anio, actividad_id, descripcion, rendimiento_neto_irpf, personas_asalariadas, agricola,
                                        cuota_iva_devengada, cuota_iva_soportada, porcentaje_ingreso_iva, porcentaje_cuota_minima)
            VALUES (@empresaId, @anio, @ActividadId, @Descripcion, @RendimientoNetoIrpf, @PersonasAsalariadas, @Agricola,
                    @CuotaIvaDevengada, @CuotaIvaSoportada, @PorcentajeIngresoIva, @PorcentajeCuotaMinima)
            """, actividades.Select(a => new { empresaId, anio = (short)anio, a.ActividadId, a.Descripcion, a.RendimientoNetoIrpf,
                a.PersonasAsalariadas, a.Agricola, a.CuotaIvaDevengada, a.CuotaIvaSoportada, a.PorcentajeIngresoIva, a.PorcentajeCuotaMinima }),
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return await ListarAsync(empresaId, anio, ct);
    }
}
