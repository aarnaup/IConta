using System.Text.Json;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public sealed class DatosSociedadesDto
{
    public long EjercicioId { get; set; }
    public List<AjusteIs> Ajustes { get; set; } = [];
    public decimal ReservaCapitalizacion { get; set; }
    public decimal Bonificaciones { get; set; }
    public decimal Deducciones { get; set; }
    /// <summary>Importe neto de la cifra de negocios (límite de compensación de bases negativas).</summary>
    public decimal Incn { get; set; }
    /// <summary>Bases negativas pendientes de antes de usar la aplicación (se indican una vez).</summary>
    public decimal BinsAnteriores { get; set; }
    /// <summary>Las mismas por año de origen (página 15 del 200); si se indican, su suma es <see cref="BinsAnteriores"/>.</summary>
    public List<BinAnio> BinsDetalle { get; set; } = [];
    /// <summary>Ajustes que se proponen desde la contabilidad (diferencias de amortización contable y fiscal…).</summary>
    public List<AjusteIs> Sugerencias { get; set; } = [];
    /// <summary>Clases de ajuste (casilla del modelo 200 en la que va cada uno).</summary>
    public IReadOnlyList<ClasesAjusteIs.Clase> Clases => ClasesAjusteIs.Todas;
}

public interface ISociedadesRepositorio
{
    Task<DatosSociedadesDto> DatosAsync(long empresaId, long ejercicioId, CancellationToken ct);
    Task<DatosSociedadesDto> GuardarAsync(long empresaId, long ejercicioId, DatosSociedadesDto datos, CancellationToken ct);
}

/// <summary>Datos del Impuesto sobre Sociedades de cada periodo impositivo que no salen de los asientos.</summary>
public sealed class SociedadesRepositorio(IConexionFactory conexiones, IInmovilizadoRepositorio inmovilizado) : ISociedadesRepositorio
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed class Fila
    {
        public string Ajustes { get; set; } = "[]";
        public decimal ReservaCapitalizacion { get; set; }
        public decimal Bonificaciones { get; set; }
        public decimal Deducciones { get; set; }
        public decimal Incn { get; set; }
        public decimal BinsAnteriores { get; set; }
        public string BinsDetalle { get; set; } = "[]";
        public List<BinAnio> Detalle => JsonSerializer.Deserialize<List<BinAnio>>(BinsDetalle, Json) ?? [];
        public List<AjusteIs> Lista => JsonSerializer.Deserialize<List<AjusteIs>>(Ajustes, Json) ?? [];
    }

    internal static async Task<Fila> LeerAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<Fila>(new CommandDefinition("""
               SELECT ajustes::text AS ajustes, reserva_capitalizacion, bonificaciones, deducciones, incn, bins_anteriores, bins_detalle::text AS bins_detalle
               FROM fiscal.impuesto_sociedades WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId
               """, new { empresaId, ejercicioId }, tx, cancellationToken: ct)) ?? new Fila();

    public async Task<DatosSociedadesDto> DatosAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await EjercicioAsync(cn, empresaId, ejercicioId, ct);
        var f = await LeerAsync(cn, null, empresaId, ejercicioId, ct);
        var sugerencias = new List<AjusteIs>();
        var dif = await inmovilizado.DiferenciasFiscalesAsync(empresaId, ejercicioId, 25m, ct);
        foreach (var x in dif.Elementos.Where(x => x.Ajuste != 0))
            sugerencias.Add(new AjusteIs($"Amortización contable/fiscal {x.Codigo} {x.Descripcion}", x.Ajuste, "AMORTIZACION"));
        var multas = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(a.debe - a.haber), 0) FROM conta.apunte a JOIN conta.asiento s ON s.id = a.asiento_id
            JOIN conta.cuenta c ON c.id = a.cuenta_id
            WHERE a.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId AND s.estado = 'CONTABILIZADO'
              AND s.origen NOT IN ('REGULARIZACION', 'CIERRE') AND c.codigo LIKE '678%'
            """, new { empresaId, ejercicioId }, cancellationToken: ct));
        if (multas != 0) sugerencias.Add(new AjusteIs("Gastos excepcionales (678): revisa si son multas, sanciones u otros no deducibles", multas, "MULTAS"));
        return new DatosSociedadesDto
        {
            EjercicioId = ejercicioId, Ajustes = f.Lista, ReservaCapitalizacion = f.ReservaCapitalizacion, Bonificaciones = f.Bonificaciones,
            Deducciones = f.Deducciones, Incn = f.Incn, BinsAnteriores = f.BinsAnteriores, BinsDetalle = f.Detalle, Sugerencias = sugerencias
        };
    }

    private static async Task EjercicioAsync(NpgsqlConnection cn, long empresaId, long ejercicioId, CancellationToken ct)
    {
        if (!await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId)",
                new { ejercicioId, empresaId }, cancellationToken: ct)))
            throw new ReglaContableException("El ejercicio no existe en la empresa.");
    }

    public async Task<DatosSociedadesDto> GuardarAsync(long empresaId, long ejercicioId, DatosSociedadesDto datos, CancellationToken ct)
    {
        if (datos.ReservaCapitalizacion < 0 || datos.Bonificaciones < 0 || datos.Deducciones < 0 || datos.Incn < 0 || datos.BinsAnteriores < 0)
            throw new ReglaContableException("Los importes no pueden ser negativos (los ajustes sí: negativo es una disminución).");
        var ajustes = (datos.Ajustes ?? []).Where(a => a.Importe != 0).Select(a => a with { Concepto = (a.Concepto ?? "").Trim(), Clase = string.IsNullOrWhiteSpace(a.Clase) ? null : a.Clase.Trim() }).ToList();
        foreach (var a in ajustes) ClasesAjusteIs.De(a.Clase);
        if (ajustes.Any(a => a.Concepto.Length is 0 or > 200)) throw new ReglaContableException("Cada ajuste necesita un concepto (hasta 200 caracteres).");
        await using var cn = await conexiones.AbrirAsync(ct);
        await EjercicioAsync(cn, empresaId, ejercicioId, ct);
        // Bases negativas anteriores por año: de años anteriores al del periodo, una por año; su suma es el total
        var detalle = (datos.BinsDetalle ?? []).Where(b => b.Importe != 0).ToList();
        var inicio = await cn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT fecha_inicio FROM core.ejercicio WHERE id = @ejercicioId", new { ejercicioId }, cancellationToken: ct));
        if (detalle.Any(b => b.Importe < 0 || b.Anio < 1990 || b.Anio >= inicio.Year))
            throw new ReglaContableException($"Cada base negativa anterior es un importe positivo de un año anterior a {inicio.Year}.");
        if (detalle.GroupBy(b => b.Anio).Any(g => g.Count() > 1)) throw new ReglaContableException("Indica una sola línea por año de origen.");
        var bins = detalle.Count > 0 ? detalle.Sum(b => b.Importe) : datos.BinsAnteriores;
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.impuesto_sociedades (empresa_id, ejercicio_id, ajustes, reserva_capitalizacion, bonificaciones, deducciones, incn,
                                                    bins_anteriores, bins_detalle)
            VALUES (@empresaId, @ejercicioId, @ajustes::jsonb, @r, @b, @d, @i, @bins, @detalle::jsonb)
            ON CONFLICT (empresa_id, ejercicio_id) DO UPDATE SET ajustes = EXCLUDED.ajustes, reserva_capitalizacion = EXCLUDED.reserva_capitalizacion,
                bonificaciones = EXCLUDED.bonificaciones, deducciones = EXCLUDED.deducciones, incn = EXCLUDED.incn, bins_anteriores = EXCLUDED.bins_anteriores,
                bins_detalle = EXCLUDED.bins_detalle
            """, new { empresaId, ejercicioId, ajustes = JsonSerializer.Serialize(ajustes, Json), r = datos.ReservaCapitalizacion, b = datos.Bonificaciones,
                d = datos.Deducciones, i = datos.Incn, bins, detalle = JsonSerializer.Serialize(detalle.OrderBy(b => b.Anio), Json) }, cancellationToken: ct));
        return await DatosAsync(empresaId, ejercicioId, ct);
    }
}
