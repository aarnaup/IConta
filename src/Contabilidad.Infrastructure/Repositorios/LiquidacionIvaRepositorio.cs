using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ILiquidacionIvaRepositorio
{
    /// <summary>Calcula el asiento de liquidación de una declaración 303/420 sin guardarlo.</summary>
    Task<LiquidacionIvaVista> PrevisualizarAsync(long empresaId, long ejercicioId, long declaracionId, LiquidacionIvaCmd cmd, CancellationToken ct);

    /// <summary>Contabiliza el asiento de liquidación y lo enlaza con la declaración.</summary>
    Task<LiquidacionIvaVista> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, long declaracionId, LiquidacionIvaCmd cmd, CancellationToken ct);

    /// <summary>Anula el asiento de liquidación (si es el último y el ejercicio está abierto) y lo desenlaza de la declaración.</summary>
    Task AnularAsync(long empresaId, long ejercicioId, long declaracionId, CancellationToken ct);
}

/// <summary>
/// Asiento de liquidación del IVA/IGIC. Se calcula con los saldos de la contabilidad (472 y 477 a la fecha de fin del periodo)
/// en cada canal base, y se contrasta con el resultado de la declaración presentada.
/// </summary>
public sealed class LiquidacionIvaRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : ILiquidacionIvaRepositorio
{
    private static readonly string[] ModelosLiquidables = ["303", "420"];

    private sealed class DeclaracionFila
    {
        public long Id { get; set; }
        public string Modelo { get; set; } = "";
        public int Anio { get; set; }
        public string Periodo { get; set; } = "";
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
        public string Estado { get; set; } = "";
        public long? AsientoId { get; set; }
        public string? AsientoEstado { get; set; }
        public int? AsientoNumero { get; set; }
        public long? AsientoEjercicioId { get; set; }
        public decimal ResultadoAntes { get; set; }
        public decimal Compensacion { get; set; }
        public decimal RegularizacionProrrata { get; set; }
        public decimal ProrrataContabilizada { get; set; }
        public decimal RegularizacionBienes { get; set; }
        public decimal BienesContabilizado { get; set; }
    }
    private sealed class EjercicioFila { public DateOnly FechaInicio { get; set; } public DateOnly FechaFin { get; set; } public string Estado { get; set; } = ""; }
    private sealed class CuentaFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public string Nombre { get; set; } = ""; }

    private sealed record Calculo(DeclaracionFila Declaracion, CrearAsientoCmd Asiento, LiquidacionIvaVista Vista);

    public async Task<LiquidacionIvaVista> PrevisualizarAsync(long empresaId, long ejercicioId, long declaracionId, LiquidacionIvaCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await CalcularAsync(cn, null, empresaId, ejercicioId, declaracionId, cmd, ct)).Vista;
    }

    public async Task<LiquidacionIvaVista> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, long declaracionId,
        LiquidacionIvaCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var c = await CalcularAsync(cn, tx, empresaId, ejercicioId, declaracionId, cmd, ct);
        var d = c.Declaracion;
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, c.Asiento, "IVA", null,
            $"{d.Modelo} {d.Anio} {d.Periodo}", ct);

        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE fiscal.declaracion SET asiento_id = @asientoId WHERE id = @declaracionId",
            new { asientoId = a.Id, declaracionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return c.Vista with { AsientoId = a.Id, AsientoNumero = a.Numero };
    }

    public async Task AnularAsync(long empresaId, long ejercicioId, long declaracionId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var d = await CargarAsync(cn, tx, empresaId, declaracionId, ct);
        if (d.AsientoId is null || d.AsientoEstado == "ANULADO")
            throw new ReglaContableException("La declaración no tiene asiento de liquidación.");
        if (d.AsientoEjercicioId != ejercicioId)
            throw new ReglaContableException("El asiento de liquidación pertenece a otro ejercicio: selecciónalo para anularlo.");

        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");

        var posteriores = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM conta.asiento s, conta.asiento yo
            WHERE yo.id = @asientoId AND s.empresa_id = yo.empresa_id AND s.ejercicio_id = yo.ejercicio_id
              AND s.origen = 'IVA' AND s.estado = 'CONTABILIZADO' AND s.id <> yo.id
              AND (s.fecha > yo.fecha OR (s.fecha = yo.fecha AND s.id > yo.id))
            """, new { asientoId = d.AsientoId }, tx, cancellationToken: ct));
        if (posteriores > 0)
            throw new ReglaContableException("Hay liquidaciones posteriores contabilizadas: anúlalas antes, empezando por la más reciente.");

        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'ANULADO' WHERE id = @asientoId",
            new { asientoId = d.AsientoId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("UPDATE fiscal.declaracion SET asiento_id = NULL WHERE id = @declaracionId",
            new { declaracionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------

    private static async Task<DeclaracionFila> CargarAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long declaracionId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<DeclaracionFila>(new CommandDefinition($"""
               SELECT d.id, m.codigo AS modelo, d.anio, d.periodo, d.fecha_inicio, d.fecha_fin, d.estado,
                      d.asiento_id, s.estado AS asiento_estado, s.numero AS asiento_numero, s.ejercicio_id AS asiento_ejercicio_id,
                      COALESCE((SELECT importe FROM fiscal.declaracion_casilla WHERE declaracion_id = d.id
                                AND casilla = CASE m.codigo WHEN '420' THEN 'RES' ELSE '64' END), 0) AS resultado_antes,
                      COALESCE((SELECT importe FROM fiscal.declaracion_casilla WHERE declaracion_id = d.id
                                AND casilla = CASE m.codigo WHEN '420' THEN 'COMP' ELSE '68' END), 0) AS compensacion,
                      COALESCE((SELECT importe FROM fiscal.declaracion_casilla WHERE declaracion_id = d.id
                                AND casilla = CASE m.codigo WHEN '420' THEN 'REG' ELSE '44' END), 0) AS regularizacion_prorrata,
                      COALESCE((SELECT SUM(r.importe) FROM fiscal.regularizacion_prorrata r
                                JOIN conta.asiento a ON a.id = r.asiento_id AND a.estado <> 'ANULADO'
                                WHERE r.empresa_id = d.empresa_id AND r.fecha BETWEEN d.fecha_inicio AND d.fecha_fin), 0) AS prorrata_contabilizada,
                      COALESCE((SELECT importe FROM fiscal.declaracion_casilla WHERE declaracion_id = d.id
                                AND casilla = CASE m.codigo WHEN '420' THEN 'RBI' ELSE '43' END), 0) AS regularizacion_bienes,
                      COALESCE((SELECT SUM(r.importe_regularizacion) FROM inmov.bien_inversion_regularizacion r
                                JOIN inmov.bien_inversion b ON b.id = r.bien_inversion_id
                                JOIN inmov.inmovilizado i ON i.id = b.inmovilizado_id
                                JOIN conta.asiento a ON a.id = r.asiento_id AND a.estado <> 'ANULADO'
                                WHERE i.empresa_id = d.empresa_id AND r.fecha BETWEEN d.fecha_inicio AND d.fecha_fin), 0) AS bienes_contabilizado
               FROM fiscal.declaracion d
               JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id
               LEFT JOIN conta.asiento s ON s.id = d.asiento_id
               WHERE d.id = @declaracionId AND d.empresa_id = @empresaId
               {(tx is null ? "" : "FOR UPDATE OF d")}
               """, new { declaracionId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("La declaración no existe.");

    private static async Task<EjercicioFila> EjercicioAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
               "SELECT fecha_inicio, fecha_fin, estado FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
               new { ejercicioId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");

    private static async Task<Calculo> CalcularAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, long declaracionId,
        LiquidacionIvaCmd cmd, CancellationToken ct)
    {
        var d = await CargarAsync(cn, tx, empresaId, declaracionId, ct);
        var avisos = new List<string>();

        if (!ModelosLiquidables.Contains(d.Modelo))
            throw new ReglaContableException($"El modelo {d.Modelo} no genera asiento de liquidación (solo 303 y 420).");
        if (PeriodoFiscal.Resolver(d.Anio, d.Periodo).EsAnual)
            throw new ReglaContableException("Los resúmenes anuales no generan asiento de liquidación.");
        if (d.Estado != "PRESENTADA")
            throw new ReglaContableException("Presenta la declaración antes de contabilizar su liquidación.",
                "Así las cifras del asiento corresponden a las de la declaración presentada.");
        if (d.AsientoId is not null && d.AsientoEstado != "ANULADO")
            throw new ReglaContableException($"La liquidación ya está contabilizada (asiento nº {d.AsientoNumero}).");

        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (d.FechaFin < ej.FechaInicio || d.FechaFin > ej.FechaFin)
            throw new ReglaContableException("El periodo de la declaración termina fuera del ejercicio seleccionado.",
                $"Fin del periodo: {d.FechaFin:dd/MM/yyyy}. Selecciona el ejercicio que contiene esa fecha.");
        if (d.FechaInicio < ej.FechaInicio)
            avisos.Add("El periodo empieza antes del ejercicio: lo que quedara sin liquidar del ejercicio anterior llega con el asiento de apertura y se incluye aquí.");

        var fecha = cmd.Fecha ?? d.FechaFin;
        if (fecha < d.FechaFin) throw new ReglaContableException("La fecha del asiento no puede ser anterior al fin del periodo.");

        // Orden: una liquidación posterior ya contabilizada habría arrastrado el saldo de este periodo
        var posterior = await cn.ExecuteScalarAsync<DateOnly?>(new CommandDefinition("""
            SELECT max(fecha) FROM conta.asiento
            WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND origen = 'IVA' AND estado = 'CONTABILIZADO' AND fecha > @fin
            """, new { empresaId, ejercicioId, fin = d.FechaFin }, tx, cancellationToken: ct));
        if (posterior is not null)
            throw new ReglaContableException("Ya hay una liquidación posterior contabilizada en este ejercicio.",
                $"Del {posterior:dd/MM/yyyy}. Las liquidaciones se contabilizan en orden; anula la posterior si necesitas rehacerlas.");

        var sinAsiento = (await cn.QueryAsync<string>(new CommandDefinition("""
            SELECT d.periodo FROM fiscal.declaracion d
            LEFT JOIN conta.asiento s ON s.id = d.asiento_id AND s.estado <> 'ANULADO'
            WHERE d.empresa_id = @empresaId AND d.modelo_id = (SELECT modelo_id FROM fiscal.declaracion WHERE id = @declaracionId)
              AND d.estado = 'PRESENTADA' AND s.id IS NULL AND d.id <> @declaracionId
              AND d.fecha_fin < @fin AND d.fecha_fin >= @inicioEj AND d.periodo NOT IN ('0A')
            ORDER BY d.fecha_fin
            """, new { empresaId, declaracionId, fin = d.FechaFin, inicioEj = ej.FechaInicio }, tx, cancellationToken: ct))).AsList();
        if (sinAsiento.Count > 0)
            avisos.Add($"Periodos anteriores presentados sin asiento de liquidación ({string.Join(", ", sinAsiento)}): sus saldos se incluyen en éste.");

        // Cuentas: 472/477 del PGC más las configuradas para IVA soportado/repercutido
        var extraSop = await ConsultasContables.CuentasDeRolAsync(cn, tx, empresaId, "IVA_SOPORTADO", ct);
        var extraRep = await ConsultasContables.CuentasDeRolAsync(cn, tx, empresaId, "IVA_REPERCUTIDO", ct);
        // El impuesto pendiente del criterio de caja (4779 / 4729) no se liquida hasta que se traspasa
        var pendientesCaja = (await ConsultasContables.CuentasDeRolAsync(cn, tx, empresaId, "IVA_REPERCUTIDO_CAJA", ct))
            .Concat(await ConsultasContables.CuentasDeRolAsync(cn, tx, empresaId, "IVA_SOPORTADO_CAJA", ct)).ToHashSet();
        var saldos = (await ConsultasContables.SaldosAsync(cn, tx, empresaId, ejercicioId, d.FechaFin, ["472", "477"],
            [.. extraSop, .. extraRep], ct)).Where(s => !pendientesCaja.Contains(s.CuentaId)).ToList();
        var repercutido = saldos.Where(s => s.Codigo.StartsWith("477", StringComparison.Ordinal)
                                         || (extraRep.Contains(s.CuentaId) && !s.Codigo.StartsWith("472", StringComparison.Ordinal))).ToList();
        var soportado = saldos.Except(repercutido).ToList();
        var pendienteCaja = await CriterioCajaRepositorio.PendienteAsync(cn, tx, empresaId, d.Modelo == "420" ? "IGIC" : "IVA",
            PeriodoFiscal.Resolver(d.Anio, d.Periodo), ct);
        if (saldos.Count == 0)
            throw new ReglaContableException("Las cuentas de IVA/IGIC soportado y repercutido no tienen saldo a esa fecha: no hay nada que liquidar.",
                pendienteCaja != 0 ? $"Hay {pendienteCaja:N2} de criterio de caja sin traspasar: contabiliza antes el traspaso del periodo." : null);

        var acreedora = await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "HP_ACREEDORA_IVA", "4750", ct)
            ?? throw new ReglaContableException("No se encuentra la cuenta de Hacienda Pública acreedora por IVA (4750).",
                "Créala en el plan de la empresa o configúrala en cuentas automáticas con el rol HP_ACREEDORA_IVA.");
        var deudora = await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "HP_DEUDORA_IVA", "4700", ct)
            ?? throw new ReglaContableException("No se encuentra la cuenta de Hacienda Pública deudora por IVA (4700).",
                "Créala en el plan de la empresa o configúrala en cuentas automáticas con el rol HP_DEUDORA_IVA.");

        var saldoDeudora = (await ConsultasContables.SaldosAsync(cn, tx, empresaId, ejercicioId, d.FechaFin, [], [deudora], ct))
            .ToDictionary(s => s.CanalBaseId, s => s.Saldo);

        var (apuntes, canales) = GeneradorLiquidacionIva.Generar(new EntradaLiquidacionIva(
            soportado, repercutido, saldoDeudora, d.Compensacion, acreedora, deudora));

        // Contraste con la declaración: la regularización de prorrata no está en la contabilidad
        // La de bienes de inversión sí, si se ha contabilizado su asiento (472 contra 6342/6392) dentro del periodo
        var esperado = d.ResultadoAntes + d.RegularizacionProrrata - d.ProrrataContabilizada + d.RegularizacionBienes - d.BienesContabilizado;
        if (d.RegularizacionBienes != d.BienesContabilizado)
            avisos.Add($"La declaración regulariza {d.RegularizacionBienes:N2} por bienes de inversión y hay contabilizados {d.BienesContabilizado:N2} en el periodo: " +
                       "contabiliza la regularización (Inmovilizado → Bienes de inversión) antes de la liquidación.");
        if (d.RegularizacionProrrata != d.ProrrataContabilizada)
            avisos.Add($"La declaración regulariza {d.RegularizacionProrrata:N2} por prorrata definitiva y hay contabilizados {d.ProrrataContabilizada:N2} " +
                       "en el periodo: contabiliza la regularización (Modelos fiscales → Prorrata) antes de la liquidación.");
        if (pendienteCaja != 0)
            avisos.Add($"Hay {pendienteCaja:N2} de impuesto en criterio de caja devengado en el periodo sin traspasar a las cuentas 477/472: " +
                       "contabiliza antes el traspaso (Modelos fiscales → Criterio de caja).");
        if (!canales.Any(c => c.Resultado == esperado))
            avisos.Add($"El saldo contable no coincide con la declaración: resultado según la declaración {d.ResultadoAntes:N2}" +
                       $"; según la contabilidad {string.Join(", ", canales.Select(c => c.Resultado.ToString("N2")))}. " +
                       "Revisa facturas contabilizadas después de presentar, apuntes manuales en 472/477 o periodos anteriores sin liquidar.");
        if (d.Compensacion > 0 && canales.Any(c => c.Resultado > 0 && c.Compensado < Math.Min(c.Resultado, d.Compensacion)))
            avisos.Add($"La declaración compensa {d.Compensacion:N2} de periodos anteriores, pero la cuenta 4700 no tiene saldo suficiente: el resto se lleva a la 4750.");

        var modelo = ModelosFiscales.Obtener(d.Modelo);
        var concepto = $"Liquidación {(d.Modelo == "420" ? "IGIC" : "IVA")} modelo {d.Modelo} {d.Periodo} {d.Anio}";
        var diarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, cmd.DiarioId, ct);
        var cmdAsiento = new CrearAsientoCmd
        {
            Fecha = fecha, DiarioId = diarioId, Concepto = concepto,
            Apuntes = ConsultasContables.AApuntes(apuntes, concepto)
        };

        var cuentas = (await cn.QueryAsync<CuentaFila>(new CommandDefinition(
            "SELECT id, codigo, nombre FROM conta.cuenta WHERE empresa_id = @empresaId AND id = ANY(@ids)",
            new { empresaId, ids = apuntes.Select(a => a.CuentaId).Distinct().ToArray() }, tx, cancellationToken: ct)))
            .ToDictionary(c => c.Id);
        var codigosCanal = await ConsultasContables.CodigosCanalAsync(cn, tx, empresaId, ct);

        var vista = new LiquidacionIvaVista(
            d.Id, modelo.Codigo, d.Anio, d.Periodo, fecha, diarioId, concepto, d.ResultadoAntes,
            canales.Select(c => new LiquidacionCanalDto(c.CanalBaseId, codigosCanal.GetValueOrDefault(c.CanalBaseId, "?"),
                c.Repercutido, c.Soportado, c.Compensado, c.AIngresar, c.AFavor)).ToList(),
            apuntes.Select(a => new ApuntePrevioDto(a.CuentaId, cuentas[a.CuentaId].Codigo, cuentas[a.CuentaId].Nombre, a.CanalId,
                a.Debe, a.Haber, concepto)).ToList(),
            avisos, null, null);

        return new Calculo(d, cmdAsiento, vista);
    }
}
