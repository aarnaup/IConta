using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IAplicacionResultadoRepositorio
{
    /// <summary>Resultado pendiente de aplicar por canal, reserva legal mínima y aplicaciones ya contabilizadas.</summary>
    Task<PropuestaAplicacionDto> PropuestaAsync(long empresaId, long ejercicioId, CancellationToken ct);
    Task<AplicacionResultadoVista> PrevisualizarAsync(long empresaId, long ejercicioId, AplicarResultadoCmd cmd, CancellationToken ct);
    Task<AplicacionResultadoVista> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, AplicarResultadoCmd cmd, CancellationToken ct);
    Task AnularAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct);
}

/// <summary>
/// Aplicación del resultado del ejercicio anterior según el acuerdo de la junta: reparte el saldo de la 129 (que llega con el
/// asiento de apertura) entre reservas, remanente, compensación de pérdidas y dividendos. Canal a canal, como el resto.
/// </summary>
public sealed class AplicacionResultadoRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : IAplicacionResultadoRepositorio
{
    private sealed class EjercicioFila
    {
        public long Id { get; set; }
        public string Codigo { get; set; } = "";
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
        public string Estado { get; set; } = "";
    }
    private sealed class CuentaFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public string Nombre { get; set; } = ""; }

    private sealed record Situacion(EjercicioFila Ejercicio, CuentasAplicacion Cuentas, List<SaldosAplicacion> Saldos,
        Dictionary<long, (decimal Capital, decimal ReservaLegal)> Legal, Dictionary<long, string> Canales);

    // ------------------------------------------------------------------ propuesta
    public async Task<PropuestaAplicacionDto> PropuestaAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var ej = await EjercicioAsync(cn, null, empresaId, ejercicioId, ct);
        var sit = await SituacionAsync(cn, null, empresaId, ej, ej.FechaFin, null, ct);
        var avisos = new List<string>();

        var anterior = await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition("""
            SELECT id, codigo, fecha_inicio, fecha_fin, estado FROM core.ejercicio
            WHERE empresa_id = @empresaId AND fecha_fin < @inicio ORDER BY fecha_fin DESC LIMIT 1
            """, new { empresaId, inicio = ej.FechaInicio }, cancellationToken: ct));
        if (anterior is { Estado: "ABIERTO" })
            avisos.Add($"El ejercicio anterior ({anterior.Codigo}) sigue abierto: ciérralo para que su resultado llegue a este con la apertura.");

        var aplicaciones = (await cn.QueryAsync<AsientoAplicacionDto>(new CommandDefinition("""
            SELECT id AS asiento_id, numero, fecha, concepto FROM conta.asiento
            WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND origen = 'APLICACION' AND estado = 'CONTABILIZADO'
            ORDER BY fecha, id
            """, new { empresaId, ejercicioId }, cancellationToken: ct))).AsList();
        if (sit.Saldos.All(s => s.Resultado == 0))
            avisos.Add(aplicaciones.Count > 0 ? "El resultado ya está aplicado." : "La cuenta 129 no tiene saldo: no hay resultado pendiente de aplicar.");

        var canales = sit.Saldos.Select(s =>
        {
            var (capital, reserva) = sit.Legal.GetValueOrDefault(s.CanalBaseId);
            return new SituacionCanalDto(s.CanalBaseId, s.Canal, s.Resultado, capital, reserva,
                GeneradorAplicacionResultado.ReservaLegalMinima(s.Resultado, capital, reserva), s.PerdidasAnteriores, s.DividendoACuenta);
        }).ToList();
        return new PropuestaAplicacionDto(ej.Codigo, anterior?.Codigo, canales, aplicaciones, avisos);
    }

    // ------------------------------------------------------------------ previsualizar / contabilizar / anular
    public async Task<AplicacionResultadoVista> PrevisualizarAsync(long empresaId, long ejercicioId, AplicarResultadoCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await CalcularAsync(cn, null, empresaId, ejercicioId, cmd, ct)).Vista;
    }

    public async Task<AplicacionResultadoVista> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, AplicarResultadoCmd cmd,
        CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        // Dos aplicaciones simultáneas no pueden repartir el mismo saldo
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"aplicacion|{empresaId}|{ejercicioId}" }, tx, cancellationToken: ct));

        var (vista, asiento) = await CalcularAsync(cn, tx, empresaId, ejercicioId, cmd, ct);
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, asiento, "APLICACION", null,
            "APLICACION RESULTADO", ct);
        await tx.CommitAsync(ct);
        return vista with { AsientoId = a.Id, AsientoNumero = a.Numero };
    }

    public async Task AnularAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE conta.asiento SET estado = 'ANULADO'
            WHERE id = @asientoId AND empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND origen = 'APLICACION' AND estado = 'CONTABILIZADO'
            """, new { asientoId, empresaId, ejercicioId }, tx, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("No hay una aplicación del resultado contabilizada con ese asiento en este ejercicio.");
        await tx.CommitAsync(ct);
    }

    private static async Task<(AplicacionResultadoVista Vista, CrearAsientoCmd Asiento)> CalcularAsync(NpgsqlConnection cn, NpgsqlTransaction? tx,
        long empresaId, long ejercicioId, AplicarResultadoCmd cmd, CancellationToken ct)
    {
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        var fecha = cmd.Fecha ?? DateOnly.FromDateTime(DateTime.Today);
        if (fecha < ej.FechaInicio || fecha > ej.FechaFin)
            throw new ReglaContableException($"La fecha del acuerdo debe estar dentro del ejercicio ({ej.FechaInicio:dd/MM/yyyy} – {ej.FechaFin:dd/MM/yyyy}).");

        var sit = await SituacionAsync(cn, tx, empresaId, ej, fecha, cmd.CanalId, ct);
        var lineas = cmd.Lineas.Select(l => new LineaAplicacion((l.Destino ?? "").Trim().ToUpperInvariant(), (l.Modo ?? "").Trim().ToUpperInvariant(),
            l.Valor, l.CuentaId)).ToList();

        // Una cuenta elegida a mano debe ser una subcuenta de la empresa del grupo 1 (y no la propia 129)
        var manuales = lineas.Where(l => l.Destino == DestinoResultado.Cuenta && l.CuentaId is not null).Select(l => l.CuentaId!.Value).Distinct().ToArray();
        if (manuales.Length > 0)
        {
            var validas = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(*) FROM conta.cuenta WHERE empresa_id = @empresaId AND id = ANY(@ids) AND es_imputable AND activa
                  AND codigo LIKE '1%' AND codigo NOT LIKE '129%'
                """, new { empresaId, ids = manuales }, tx, cancellationToken: ct));
            if (validas != manuales.Length)
                throw new ReglaContableException("La cuenta elegida debe ser una subcuenta del grupo 1 de la empresa (reservas), distinta de la 129.");
        }

        var (apuntes, canales) = GeneradorAplicacionResultado.Generar(sit.Saldos, lineas, sit.Cuentas);

        // Avisos legales (no bloquean: la junta decide y el asesor revisa)
        var avisos = new List<string>();
        foreach (var c in canales)
        {
            var s = sit.Saldos.First(x => x.CanalBaseId == c.CanalBaseId);
            var etiqueta = canales.Count > 1 ? $"Canal {s.Canal}: " : "";
            var porDestino = c.Reparto.GroupBy(r => r.Destino).ToDictionary(g => g.Key, g => g.Sum(r => r.Importe));
            if (c.Resultado > 0)
            {
                var (capital, reserva) = sit.Legal.GetValueOrDefault(c.CanalBaseId);
                var minima = GeneradorAplicacionResultado.ReservaLegalMinima(c.Resultado, capital, reserva);
                var dotada = porDestino.GetValueOrDefault(DestinoResultado.ReservaLegal);
                if (dotada < minima)
                    avisos.Add($"{etiqueta}la reserva legal debe recibir al menos {minima:N2} (10 % del beneficio hasta llegar al 20 % del capital, art. 274 LSC); se dotan {dotada:N2}.");
                var perdidasTras = s.PerdidasAnteriores - porDestino.GetValueOrDefault(DestinoResultado.CompensarPerdidas);
                if (porDestino.GetValueOrDefault(DestinoResultado.Dividendos) > 0 && perdidasTras > 0)
                    avisos.Add($"{etiqueta}quedan {perdidasTras:N2} de pérdidas de ejercicios anteriores sin compensar: la ley limita repartir dividendos en ese caso (art. 273 LSC).");
            }
            else if (porDestino.ContainsKey(DestinoResultado.ReservaLegal))
                avisos.Add($"{etiqueta}la reserva legal solo puede compensar pérdidas si no hay otras reservas disponibles (art. 274 LSC).");
        }
        if (cmd.CanalId is null && sit.Saldos.Count(x => x.Resultado != 0) > 1 && lineas.Any(l => l.Modo == "IMPORTE"))
            avisos.Add("Los importes fijos se aplican igual en cada canal; si el resultado de cada canal es distinto, usa porcentajes o aplica canal a canal.");

        var acuerdo = string.IsNullOrWhiteSpace(cmd.Acuerdo) ? $"acuerdo de {fecha:dd/MM/yyyy}" : cmd.Acuerdo.Trim();
        var anterior = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT codigo FROM core.ejercicio WHERE empresa_id = @empresaId AND fecha_fin < @inicio ORDER BY fecha_fin DESC LIMIT 1",
            new { empresaId, inicio = ej.FechaInicio }, tx, cancellationToken: ct));
        var concepto = $"Aplicación del resultado{(anterior is null ? "" : " del ejercicio " + anterior)} ({acuerdo})";
        if (concepto.Length > 250) concepto = concepto[..250];
        var diarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, cmd.DiarioId, ct);

        var cuentas = (await cn.QueryAsync<CuentaFila>(new CommandDefinition(
            "SELECT id, codigo, nombre FROM conta.cuenta WHERE empresa_id = @empresaId AND id = ANY(@ids)",
            new { empresaId, ids = apuntes.Select(a => a.CuentaId).Distinct().ToArray() }, tx, cancellationToken: ct))).ToDictionary(c => c.Id);

        var vista = new AplicacionResultadoVista(fecha, diarioId, concepto,
            canales.Select(c => new AplicacionCanalDto(c.CanalBaseId, sit.Canales.GetValueOrDefault(c.CanalBaseId, "?"), c.Resultado,
                c.Reparto.Select(r => new RepartoDto(r.Destino, GeneradorAplicacionResultado.Texto(r.Destino), cuentas[r.CuentaId].Codigo, r.Importe)).ToList()))
                .ToList(),
            apuntes.Select(a => new ApuntePrevioDto(a.CuentaId, cuentas[a.CuentaId].Codigo, cuentas[a.CuentaId].Nombre, a.CanalId, a.Debe, a.Haber, concepto)).ToList(),
            avisos, null, null);
        var asiento = new CrearAsientoCmd { Fecha = fecha, DiarioId = diarioId, Concepto = concepto, Apuntes = ConsultasContables.AApuntes(apuntes, concepto) };
        return (vista, asiento);
    }

    // ------------------------------------------------------------------ consultas
    private static async Task<EjercicioFila> EjercicioAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
               "SELECT id, codigo, fecha_inicio, fecha_fin, estado FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
               new { ejercicioId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");

    private static async Task<Situacion> SituacionAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EjercicioFila ej, DateOnly hasta,
        long? canalId, CancellationToken ct)
    {
        var resultado = await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "RESULTADO_EJERCICIO", "129", ct)
            ?? throw new ReglaContableException("La empresa no tiene la cuenta de resultado del ejercicio (129).");
        var cuentas = new CuentasAplicacion(resultado,
            await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "RESERVA_LEGAL", "112", ct),
            await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "RESERVA_VOLUNTARIA", "113", ct),
            await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "REMANENTE", "120", ct),
            await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "RESULTADOS_NEGATIVOS", "121", ct),
            await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "DIVIDENDO_A_PAGAR", "526", ct),
            await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "DIVIDENDO_A_CUENTA", "557", ct));

        // Saldos antes de cerrar (sin regularización ni cierre de este ejercicio, por si se consulta ya cerrado)
        var extra = new[] { cuentas.Resultado, cuentas.ResultadosNegativos ?? 0, cuentas.DividendoACuenta ?? 0 };
        var saldos = await ConsultasContables.SaldosAsync(cn, tx, empresaId, ej.Id, hasta, ["100", "101", "102", "112"], extra, ct,
            sinAsientosDeCierre: true);

        var canales = await ConsultasContables.CodigosCanalAsync(cn, tx, empresaId, ct);
        var base_ = (await cn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM canal.canal WHERE empresa_id = @empresaId AND tipo = 'BASE' AND activo ORDER BY codigo",
            new { empresaId }, tx, cancellationToken: ct))).ToList();
        if (canalId is not null && !base_.Contains(canalId.Value))
            throw new ReglaContableException("El canal no es un canal base de la empresa.");

        decimal Saldo(long canal, Func<SaldoCuenta, bool> filtro) => saldos.Where(s => s.CanalBaseId == canal && filtro(s)).Sum(s => s.Saldo);
        var lista = base_.Where(c => canalId is null || c == canalId).Select(c => new SaldosAplicacion(c, canales.GetValueOrDefault(c, "?"),
            -Saldo(c, s => s.CuentaId == cuentas.Resultado),
            Math.Max(0, Saldo(c, s => s.CuentaId == cuentas.DividendoACuenta)),
            Math.Max(0, Saldo(c, s => s.CuentaId == cuentas.ResultadosNegativos)))).ToList();
        var legal = base_.ToDictionary(c => c, c => (
            Capital: -Saldo(c, s => s.Codigo.StartsWith("100") || s.Codigo.StartsWith("101") || s.Codigo.StartsWith("102")),
            ReservaLegal: -Saldo(c, s => s.Codigo.StartsWith("112"))));
        return new Situacion(ej, cuentas, lista, legal, canales);
    }
}
