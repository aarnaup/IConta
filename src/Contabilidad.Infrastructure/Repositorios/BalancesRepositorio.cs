using Contabilidad.Domain;
using Contabilidad.Domain.Balances;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IBalancesRepositorio
{
    /// <param name="incluir">MOVIMIENTOS (sin regularización ni cierre), REGULARIZACION (con la regularización) o CIERRE (todo).</param>
    Task<SumasSaldosDto> SumasSaldosAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta, int nivel,
        string incluir, CancellationToken ct);

    Task<EstadoFinancieroDto> BalanceSituacionAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? hasta, CancellationToken ct);

    Task<EstadoFinancieroDto> PerdidasGananciasAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta,
        CancellationToken ct);
}

/// <summary>
/// Balances por canal base: cada canal es una contabilidad completa (un asiento en el canal A reparte el importe entero a 1, 2 y 3),
/// así que no se pueden sumar canales entre sí. Por defecto, el primer canal base.
/// </summary>
public sealed class BalancesRepositorio(IConexionFactory conexiones) : IBalancesRepositorio
{
    private static readonly string[] OrigenesCierre = ["REGULARIZACION", "CIERRE"];

    private sealed class EjercicioFila
    {
        public long Id { get; set; }
        public string Codigo { get; set; } = "";
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
    }
    private sealed class MovFila { public string Codigo { get; set; } = ""; public decimal Debe { get; set; } public decimal Haber { get; set; } }

    private sealed record Contexto(EjercicioFila Ejercicio, CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, Func<string, string> Nombre);

    // ------------------------------------------------------------------ sumas y saldos
    public async Task<SumasSaldosDto> SumasSaldosAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta,
        int nivel, string incluir, CancellationToken ct)
    {
        incluir = (incluir ?? "MOVIMIENTOS").ToUpperInvariant();
        string[] excluir = incluir switch
        {
            "MOVIMIENTOS" => OrigenesCierre,
            "REGULARIZACION" => ["CIERRE"],
            "CIERRE" => [],
            _ => throw new ReglaContableException("«incluir» debe ser MOVIMIENTOS, REGULARIZACION o CIERRE.")
        };

        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var (d, h) = Fechas(c.Ejercicio, desde, hasta);

        var movs = await MovimientosAsync(cn, empresaId, ejercicioId, c.Canal.Id, d, h, excluir, ct);
        var filas = SumasSaldos.Agrupar(movs, nivel, c.Nombre);
        var (td, th) = (filas.Sum(f => f.Debe), filas.Sum(f => f.Haber));
        return new SumasSaldosDto(c.Canal, c.Canales, d, h, nivel, incluir, filas, td, th,
            filas.Sum(f => f.SaldoDeudor), filas.Sum(f => f.SaldoAcreedor), td == th);
    }

    // ------------------------------------------------------------------ balance y pérdidas y ganancias
    public async Task<EstadoFinancieroDto> BalanceSituacionAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var (d, h) = Fechas(c.Ejercicio, null, hasta);
        var e = await EstadoAsync(cn, empresaId, c, ModelosEstados.Balance, d, h, ct);

        var avisos = new List<string>();
        var ta = e.Actual["TA"]; var tp = e.Actual["TP"];
        if (ta != tp) avisos.Add($"El balance no cuadra: activo {ta:N2}, patrimonio neto y pasivo {tp:N2}. Revisa las cuentas sin clasificar.");
        // Sin la regularización, lo que haya en la 129 viene de antes: resultado de ejercicios anteriores sin aplicar
        var en129 = e.Actual.Partidas.First(p => p.Id == "PA1VII").Cuentas.Where(x => x.Codigo.StartsWith("129", StringComparison.Ordinal)).Sum(x => x.Importe);
        if (en129 != 0)
            avisos.Add($"«Resultado del ejercicio» incluye {en129:N2} de la cuenta 129: resultado del ejercicio anterior pendiente de aplicar " +
                       "(a reservas, remanente o dividendos).");
        if (e.Actual.Partidas.First(p => p.Id == "PA2").Cuentas.Any(x => x.Codigo[0] is '8' or '9'))
            avisos.Add("Los grupos 8 y 9 tienen saldo: se muestran en «Ajustes en patrimonio neto» hasta que se trasladen al subgrupo 13.");
        return Dto(ModelosEstados.Balance, c, d, h, e, avisos);
    }

    public async Task<EstadoFinancieroDto> PerdidasGananciasAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta,
        CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var (d, h) = Fechas(c.Ejercicio, desde, hasta);
        var completo = d == c.Ejercicio.FechaInicio && h == c.Ejercicio.FechaFin;
        var e = await EstadoAsync(cn, empresaId, c, ModelosEstados.PerdidasGanancias, d, h, ct);
        var avisos = new List<string>();
        if (!completo && e.Anterior is not null) avisos.Add("El ejercicio anterior se muestra completo; el actual, solo del periodo elegido.");
        return Dto(ModelosEstados.PerdidasGanancias, c, d, h, e, avisos);
    }

    private sealed record Estados(EstadoCalculado Actual, EstadoCalculado? Anterior, string? CodigoAnterior);

    private static async Task<Estados> EstadoAsync(NpgsqlConnection cn, long empresaId, Contexto c, ModeloEstado modelo, DateOnly desde, DateOnly hasta,
        CancellationToken ct)
    {
        var actual = GeneradorEstados.Calcular(modelo, await SaldosAsync(cn, empresaId, c, c.Ejercicio.Id, desde, hasta, ct));

        // Ejercicio anterior completo, sin sus asientos de regularización y cierre
        var ant = await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition("""
            SELECT id, codigo, fecha_inicio, fecha_fin FROM core.ejercicio
            WHERE empresa_id = @empresaId AND fecha_fin < @inicio ORDER BY fecha_fin DESC LIMIT 1
            """, new { empresaId, inicio = c.Ejercicio.FechaInicio }, cancellationToken: ct));
        if (ant is null) return new Estados(actual, null, null);
        var anterior = GeneradorEstados.Calcular(modelo, await SaldosAsync(cn, empresaId, c, ant.Id, ant.FechaInicio, ant.FechaFin, ct));
        return new Estados(actual, anterior, ant.Codigo);
    }

    private static async Task<List<SaldoBalance>> SaldosAsync(NpgsqlConnection cn, long empresaId, Contexto c, long ejercicioId,
        DateOnly desde, DateOnly hasta, CancellationToken ct)
        => (await MovimientosAsync(cn, empresaId, ejercicioId, c.Canal.Id, desde, hasta, OrigenesCierre, ct))
           .Select(m => new SaldoBalance(m.Codigo, c.Nombre(m.Codigo), m.Debe - m.Haber)).ToList();

    private static EstadoFinancieroDto Dto(ModeloEstado modelo, Contexto c, DateOnly desde, DateOnly hasta, Estados e, List<string> avisos)
    {
        if (e.Actual.SinClasificar.Count > 0)
            avisos.Add($"{e.Actual.SinClasificar.Count} cuenta(s) con saldo no encajan en ninguna partida del modelo: revísalas.");
        var anteriores = e.Anterior?.Partidas.ToDictionary(p => p.Id, p => p.Importe);
        return new EstadoFinancieroDto(modelo.Codigo, modelo.Nombre, c.Canal, c.Canales, desde, hasta, c.Ejercicio.Codigo, e.CodigoAnterior,
            e.Actual.Partidas.Select(p => new PartidaDto(p.Id, p.Texto, p.Nivel, p.EsTotal, p.Importe,
                anteriores?.GetValueOrDefault(p.Id), p.Cuentas)).ToList(),
            e.Actual.SinClasificar, avisos);
    }

    // ------------------------------------------------------------------ consultas comunes
    private static (DateOnly, DateOnly) Fechas(EjercicioFila ej, DateOnly? desde, DateOnly? hasta)
    {
        var d = desde ?? ej.FechaInicio;
        var h = hasta ?? ej.FechaFin;
        if (d < ej.FechaInicio || h > ej.FechaFin || d > h)
            throw new ReglaContableException($"Las fechas deben estar dentro del ejercicio ({ej.FechaInicio:dd/MM/yyyy} – {ej.FechaFin:dd/MM/yyyy}) y en orden.");
        return (d, h);
    }

    private static async Task<List<MovimientoCuenta>> MovimientosAsync(NpgsqlConnection cn, long empresaId, long ejercicioId, long canalId,
        DateOnly desde, DateOnly hasta, string[] excluirOrigenes, CancellationToken ct)
        => (await cn.QueryAsync<MovFila>(new CommandDefinition("""
               SELECT c.codigo, SUM(m.debe) AS debe, SUM(m.haber) AS haber
               FROM conta.v_mayor m
               JOIN conta.cuenta c  ON c.id = m.cuenta_id
               JOIN conta.asiento s ON s.id = m.asiento_id
               WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND m.canal_base_id = @canalId
                 AND m.fecha BETWEEN @desde AND @hasta AND s.origen <> ALL(@excluir::text[])
               GROUP BY c.codigo
               """, new { empresaId, ejercicioId, canalId, desde, hasta, excluir = excluirOrigenes }, cancellationToken: ct)))
           .Select(f => new MovimientoCuenta(f.Codigo, f.Debe, f.Haber)).ToList();

    private static async Task<Contexto> ContextoAsync(NpgsqlConnection cn, long empresaId, long ejercicioId, long? canalId, CancellationToken ct)
    {
        var ej = await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
            "SELECT id, codigo, fecha_inicio, fecha_fin FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
            new { ejercicioId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");

        var canales = (await cn.QueryAsync<(long Id, string Codigo)>(new CommandDefinition(
            "SELECT id, codigo FROM canal.canal WHERE empresa_id = @empresaId AND tipo = 'BASE' AND activo ORDER BY codigo",
            new { empresaId }, cancellationToken: ct))).Select(x => new CanalRefDto(x.Id, x.Codigo)).ToList();
        if (canales.Count == 0) throw new ReglaContableException("La empresa no tiene canales base.");
        var canal = canalId is null ? canales[0]
            : canales.FirstOrDefault(x => x.Id == canalId) ?? throw new ReglaContableException("El canal no es un canal base de la empresa.");

        // Nombres: los del plan de la empresa y, para grupos y subgrupos que no tenga, los del plan modelo
        var nombres = (await cn.QueryAsync<(string Codigo, string Nombre)>(new CommandDefinition("""
            SELECT DISTINCT ON (codigo) codigo, nombre FROM (
              SELECT codigo, nombre, 1 AS prioridad FROM conta.cuenta WHERE empresa_id = @empresaId
              UNION ALL
              SELECT m.codigo, m.nombre, 2 FROM cat.cuenta_modelo m JOIN core.empresa e ON e.plan_modelo_id = m.plan_modelo_id WHERE e.id = @empresaId
            ) x ORDER BY codigo, prioridad
            """, new { empresaId }, cancellationToken: ct)))
            .ToDictionary(x => x.Codigo, x => x.Nombre);
        string Nombre(string codigo) => nombres.TryGetValue(codigo, out var n) ? n : "";

        return new Contexto(ej, canal, canales, Nombre);
    }
}
