using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ICierreEjercicioRepositorio
{
    /// <summary>Comprueba si el ejercicio se puede cerrar y calcula su resultado, sin cambiar nada.</summary>
    Task<EstadoCierreDto> EstadoAsync(long empresaId, long ejercicioId, CancellationToken ct);

    /// <summary>Regularización, cierre y apertura del ejercicio siguiente en una transacción; deja el ejercicio CERRADO.</summary>
    Task<CierreRealizadoDto> CerrarAsync(long empresaId, long ejercicioId, long usuarioId, CancellationToken ct);

    /// <summary>Anula los asientos del cierre (y la apertura del siguiente) y vuelve a abrir el ejercicio.</summary>
    Task ReabrirAsync(long empresaId, long ejercicioId, CancellationToken ct);
}

/// <summary>
/// Cierre del ejercicio contable:
///   1. Regularización (periodo 13): las cuentas 6 y 7 se saldan contra la 129.
///   2. Cierre (periodo 14): se saldan todas las cuentas con saldo.
///   3. Apertura (periodo 0 del ejercicio siguiente): se reabren esos mismos saldos.
/// Todo se calcula por canal base, como el resto de la contabilidad.
/// </summary>
public sealed class CierreEjercicioRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : ICierreEjercicioRepositorio
{
    private sealed class EjercicioFila
    {
        public long Id { get; set; }
        public string Codigo { get; set; } = "";
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
        public string Estado { get; set; } = "";
    }

    private sealed record Analisis(
        EjercicioFila Ejercicio, EjercicioFila? Siguiente, long? CuentaResultado, List<string> Bloqueos, List<string> Avisos,
        IReadOnlyList<ResultadoCanal> Resultados);

    public async Task<EstadoCierreDto> EstadoAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var a = await AnalizarAsync(cn, null, empresaId, ejercicioId, ct);
        var canales = await ConsultasContables.CodigosCanalAsync(cn, null, empresaId, ct);
        return new EstadoCierreDto(a.Ejercicio.Id, a.Ejercicio.Codigo, a.Ejercicio.Estado, a.Bloqueos.Count == 0, a.Bloqueos, a.Avisos,
            Resultados(a.Resultados, canales),
            a.Siguiente is null ? null : new EjercicioRefDto(a.Siguiente.Id, a.Siguiente.Codigo, a.Siguiente.Estado));
    }

    public async Task<CierreRealizadoDto> CerrarAsync(long empresaId, long ejercicioId, long usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var a = await AnalizarAsync(cn, tx, empresaId, ejercicioId, ct);
        if (a.Bloqueos.Count > 0)
            throw new ReglaContableException("El ejercicio no se puede cerrar todavía.", string.Join(" ", a.Bloqueos));

        var ej = a.Ejercicio;
        var sig = a.Siguiente!;
        var diarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct);
        await CrearPeriodosEspecialesAsync(cn, tx, ej, sig, ct);

        var generados = new List<AsientoGeneradoDto>();

        // 1) Regularización
        var saldos = await ConsultasContables.SaldosAsync(cn, tx, empresaId, ej.Id, ej.FechaFin, ["6", "7"], null, ct);
        var (apRegul, resultados) = GeneradorCierre.Regularizacion(saldos, a.CuentaResultado!.Value);
        if (apRegul.Count > 0)
        {
            var concepto = $"Regularización del ejercicio {ej.Codigo}";
            var r = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ej.Id, usuarioId,
                new CrearAsientoCmd { Fecha = ej.FechaFin, DiarioId = diarioId, Concepto = concepto, Apuntes = ConsultasContables.AApuntes(apRegul, concepto) },
                "REGULARIZACION", null, $"CIERRE {ej.Codigo}", ct, tipoPeriodo: "REGULARIZACION");
            generados.Add(new AsientoGeneradoDto("REGULARIZACION", r.Id, r.Numero, ej.Id, ej.FechaFin, apRegul.Count));
        }

        // 2) Cierre: todas las cuentas con saldo tras la regularización
        var saldosCierre = await ConsultasContables.SaldosAsync(cn, tx, empresaId, ej.Id, ej.FechaFin, null, null, ct);
        if (saldosCierre.Count > 0)
        {
            var apCierre = GeneradorCierre.Cierre(saldosCierre);
            var concepto = $"Cierre del ejercicio {ej.Codigo}";
            var c = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ej.Id, usuarioId,
                new CrearAsientoCmd { Fecha = ej.FechaFin, DiarioId = diarioId, Concepto = concepto, Apuntes = ConsultasContables.AApuntes(apCierre, concepto) },
                "CIERRE", null, $"CIERRE {ej.Codigo}", ct, tipoPeriodo: "CIERRE");
            generados.Add(new AsientoGeneradoDto("CIERRE", c.Id, c.Numero, ej.Id, ej.FechaFin, apCierre.Count));

            // 3) Apertura del ejercicio siguiente, enlazada con el cierre para poder deshacerla
            var apApertura = GeneradorCierre.Apertura(saldosCierre);
            var conceptoAp = $"Apertura del ejercicio {sig.Codigo}";
            var ap = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, sig.Id, usuarioId,
                new CrearAsientoCmd { Fecha = sig.FechaInicio, DiarioId = diarioId, Concepto = conceptoAp, Apuntes = ConsultasContables.AApuntes(apApertura, conceptoAp) },
                "APERTURA", null, $"APERTURA {sig.Codigo}", ct, tipoPeriodo: "APERTURA", asientoOrigenId: c.Id);
            generados.Add(new AsientoGeneradoDto("APERTURA", ap.Id, ap.Numero, sig.Id, sig.FechaInicio, apApertura.Count));
        }
        else
        {
            a.Avisos.Add("El ejercicio no tiene saldos: se cierra sin asientos.");
        }

        await cn.ExecuteAsync(new CommandDefinition("UPDATE core.ejercicio SET estado = 'CERRADO' WHERE id = @id",
            new { id = ej.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        var canales = await ConsultasContables.CodigosCanalAsync(cn, null, empresaId, ct);
        return new CierreRealizadoDto(generados, Resultados(resultados, canales), a.Avisos);
    }

    public async Task ReabrirAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, bloquear: true, ct);
        if (ej.Estado != "CERRADO") throw new ReglaContableException("El ejercicio no está cerrado.");

        var sig = await SiguienteAsync(cn, tx, empresaId, ej, ct);
        if (sig is { Estado: not "ABIERTO" })
            throw new ReglaContableException($"El ejercicio siguiente ({sig.Codigo}) está cerrado: reábrelo antes.");

        // Apertura del siguiente generada por este cierre, y regularización y cierre de este ejercicio
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE conta.asiento SET estado = 'ANULADO'
            WHERE empresa_id = @empresaId AND estado <> 'ANULADO'
              AND (   (ejercicio_id = @ejercicioId AND origen IN ('REGULARIZACION', 'CIERRE'))
                   OR (origen = 'APERTURA' AND asiento_origen_id IN (
                         SELECT id FROM conta.asiento WHERE ejercicio_id = @ejercicioId AND origen = 'CIERRE')))
            """, new { empresaId, ejercicioId }, tx, cancellationToken: ct));

        await cn.ExecuteAsync(new CommandDefinition("UPDATE core.ejercicio SET estado = 'ABIERTO' WHERE id = @ejercicioId",
            new { ejercicioId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------

    private static async Task<EjercicioFila> EjercicioAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId,
        bool bloquear, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition($"""
               SELECT id, codigo, fecha_inicio, fecha_fin, estado FROM core.ejercicio
               WHERE id = @ejercicioId AND empresa_id = @empresaId {(bloquear ? "FOR UPDATE" : "")}
               """, new { ejercicioId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");

    private static Task<EjercicioFila?> SiguienteAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EjercicioFila ej, CancellationToken ct)
        => cn.QuerySingleOrDefaultAsync<EjercicioFila?>(new CommandDefinition("""
               SELECT id, codigo, fecha_inicio, fecha_fin, estado FROM core.ejercicio
               WHERE empresa_id = @empresaId AND fecha_inicio > @fin ORDER BY fecha_inicio LIMIT 1
               """, new { empresaId, fin = ej.FechaFin }, tx, cancellationToken: ct));

    private static async Task<Analisis> AnalizarAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, CancellationToken ct)
    {
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, bloquear: tx is not null, ct);
        var bloqueos = new List<string>();
        var avisos = new List<string>();

        if (ej.Estado != "ABIERTO") bloqueos.Add($"El ejercicio ya está {ej.Estado.ToLowerInvariant()}.");

        var anteriorAbierto = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT codigo FROM core.ejercicio
            WHERE empresa_id = @empresaId AND fecha_fin < @inicio AND estado = 'ABIERTO' ORDER BY fecha_fin DESC LIMIT 1
            """, new { empresaId, inicio = ej.FechaInicio }, tx, cancellationToken: ct));
        if (anteriorAbierto is not null) bloqueos.Add($"Cierra antes el ejercicio anterior ({anteriorAbierto}).");

        var sig = await SiguienteAsync(cn, tx, empresaId, ej, ct);
        if (sig is null)
            bloqueos.Add("Crea antes el ejercicio siguiente: el asiento de apertura va en él.");
        else if (sig.FechaInicio != ej.FechaFin.AddDays(1))
            bloqueos.Add($"El ejercicio siguiente ({sig.Codigo}) no empieza el día después de que termine éste.");
        else if (sig.Estado != "ABIERTO")
            bloqueos.Add($"El ejercicio siguiente ({sig.Codigo}) no está abierto.");

        var borradores = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM conta.asiento WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND estado = 'BORRADOR'",
            new { empresaId, ejercicioId }, tx, cancellationToken: ct));
        if (borradores > 0) bloqueos.Add($"Hay {borradores} asiento(s) en borrador: apruébalos o recházalos.");

        var facturasBorrador = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM fiscal.factura WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND estado = 'BORRADOR'",
            new { empresaId, ejercicioId }, tx, cancellationToken: ct));
        if (facturasBorrador > 0) bloqueos.Add($"Hay {facturasBorrador} factura(s) en borrador: apruébalas o recházalas.");

        var cuentaResultado = await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "RESULTADO_EJERCICIO", "129", ct);
        if (cuentaResultado is null)
            bloqueos.Add("No se encuentra la cuenta de resultado del ejercicio (129): créala o configúrala con el rol RESULTADO_EJERCICIO.");

        // Saldos antes de cerrar: en un ejercicio ya cerrado se ignoran sus asientos de regularización y cierre
        var saldos = await ConsultasContables.SaldosAsync(cn, tx, empresaId, ejercicioId, ej.FechaFin, null, null, ct, sinAsientosDeCierre: true);

        var grupos89 = saldos.Where(s => GeneradorCierre.EsDePatrimonioNetoImputado(s.Codigo)).Select(s => s.Codigo).Distinct().ToList();
        if (grupos89.Count > 0)
            bloqueos.Add($"Las cuentas de los grupos 8 y 9 tienen saldo ({string.Join(", ", grupos89.Take(10))}): " +
                         "trasládalo a las del subgrupo 13 con un asiento manual antes de cerrar.");

        var fueraDeFecha = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM conta.asiento
            WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND estado = 'CONTABILIZADO' AND fecha > @fin
            """, new { empresaId, ejercicioId, fin = ej.FechaFin }, tx, cancellationToken: ct));
        if (fueraDeFecha > 0) bloqueos.Add($"Hay {fueraDeFecha} asiento(s) con fecha posterior al fin del ejercicio.");

        var ivaSinLiquidar = saldos.Where(s => s.Codigo.StartsWith("472", StringComparison.Ordinal) || s.Codigo.StartsWith("477", StringComparison.Ordinal))
                                   .Select(s => s.Codigo).Distinct().ToList();
        if (ivaSinLiquidar.Count > 0 && ej.Estado == "ABIERTO")
            avisos.Add($"Hay IVA/IGIC sin liquidar ({string.Join(", ", ivaSinLiquidar)}): su saldo pasará al ejercicio siguiente. " +
                       "Contabiliza antes la liquidación del último periodo si corresponde.");

        var (_, resultados) = GeneradorCierre.Regularizacion(saldos, cuentaResultado ?? 0);
        if (saldos.Count == 0) avisos.Add("El ejercicio no tiene movimientos.");

        return new Analisis(ej, sig, cuentaResultado, bloqueos, avisos, resultados);
    }

    /// <summary>Periodos 13 (regularización) y 14 (cierre) del ejercicio y 0 (apertura) del siguiente, si no existen.</summary>
    private static Task CrearPeriodosEspecialesAsync(NpgsqlConnection cn, NpgsqlTransaction tx, EjercicioFila ej, EjercicioFila sig, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO core.periodo (ejercicio_id, numero, tipo, fecha_inicio, fecha_fin) VALUES
              (@ej, 13, 'REGULARIZACION', @fin, @fin),
              (@ej, 14, 'CIERRE',         @fin, @fin),
              (@sig, 0, 'APERTURA',       @inicioSig, @inicioSig)
            ON CONFLICT (ejercicio_id, numero) DO UPDATE SET estado = 'ABIERTO'
            """, new { ej = ej.Id, fin = ej.FechaFin, sig = sig.Id, inicioSig = sig.FechaInicio }, tx, cancellationToken: ct));

    private static IReadOnlyList<ResultadoCanalDto> Resultados(IEnumerable<ResultadoCanal> resultados, Dictionary<long, string> canales)
        => resultados.Select(r => new ResultadoCanalDto(r.CanalBaseId, canales.GetValueOrDefault(r.CanalBaseId, "?"),
                                                        r.Ingresos, r.Gastos, r.Resultado)).ToList();
}
