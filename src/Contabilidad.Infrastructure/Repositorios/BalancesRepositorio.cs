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

    /// <param name="formato">PYMES (abreviado) o NORMAL; por defecto, NORMAL si la empresa usa el PGC normal.</param>
    Task<EstadoFinancieroDto> BalanceSituacionAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? hasta, CancellationToken ct,
        string? formato = null);

    Task<EstadoFinancieroDto> PerdidasGananciasAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta,
        CancellationToken ct, string? formato = null);

    Task<EcpnDto> PatrimonioNetoAsync(long empresaId, long ejercicioId, long? canalId, CancellationToken ct);
    Task<EfeDto> FlujosEfectivoAsync(long empresaId, long ejercicioId, long? canalId, CancellationToken ct);
    Task<MemoriaDto> MemoriaAsync(long empresaId, long ejercicioId, long? canalId, CancellationToken ct);
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
    public async Task<EstadoFinancieroDto> BalanceSituacionAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? hasta, CancellationToken ct,
        string? formato = null)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var (d, h) = Fechas(c.Ejercicio, null, hasta);
        var normal = await NormalAsync(cn, empresaId, formato, ct);
        var modelo = normal ? ModelosNormales.Balance : ModelosEstados.Balance;
        var e = await EstadoAsync(cn, empresaId, c, modelo, d, h, ct);

        var avisos = new List<string>();
        var ta = e.Actual["TA"]; var tp = e.Actual["TP"];
        if (ta != tp) avisos.Add($"El balance no cuadra: activo {ta:N2}, patrimonio neto y pasivo {tp:N2}. Revisa las cuentas sin clasificar.");
        // Sin la regularización, lo que haya en la 129 viene de antes: resultado de ejercicios anteriores sin aplicar
        var en129 = e.Actual.Partidas.First(p => p.Id == "PA1VII").Cuentas.Where(x => x.Codigo.StartsWith("129", StringComparison.Ordinal)).Sum(x => x.Importe);
        if (en129 != 0)
            avisos.Add($"«Resultado del ejercicio» incluye {en129:N2} de la cuenta 129: resultado del ejercicio anterior pendiente de aplicar " +
                       "(a reservas, remanente o dividendos).");
        if (e.Actual.Partidas.Where(p => p.Id.StartsWith("PA2") || p.Id == "PA3").SelectMany(p => p.Cuentas).Any(x => x.Codigo[0] is '8' or '9'))
            avisos.Add("Los grupos 8 y 9 tienen saldo: se muestran en el patrimonio neto (ajustes y subvenciones) hasta que se trasladen al subgrupo 13 en el cierre.");
        return Dto(modelo, c, d, h, e, avisos);
    }

    public async Task<EstadoFinancieroDto> PerdidasGananciasAsync(long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta,
        CancellationToken ct, string? formato = null)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var (d, h) = Fechas(c.Ejercicio, desde, hasta);
        var completo = d == c.Ejercicio.FechaInicio && h == c.Ejercicio.FechaFin;
        var modelo = await NormalAsync(cn, empresaId, formato, ct) ? ModelosNormales.PerdidasGanancias : ModelosEstados.PerdidasGanancias;
        var e = await EstadoAsync(cn, empresaId, c, modelo, d, h, ct);
        var avisos = new List<string>();
        if (!completo && e.Anterior is not null) avisos.Add("El ejercicio anterior se muestra completo; el actual, solo del periodo elegido.");
        return Dto(modelo, c, d, h, e, avisos);
    }

    /// <summary>Formato pedido o, si no se indica, el normal para las empresas con el PGC normal.</summary>
    private static async Task<bool> NormalAsync(NpgsqlConnection cn, long empresaId, string? formato, CancellationToken ct)
    {
        var f = (formato ?? "").Trim().ToUpperInvariant();
        if (f is "NORMAL") return true;
        if (f is "PYMES" or "ABREVIADO") return false;
        if (f.Length > 0) throw new ReglaContableException("Formato no válido: NORMAL o PYMES.");
        return await cn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT p.codigo FROM core.empresa e JOIN cat.plan_modelo p ON p.id = e.plan_modelo_id WHERE e.id = @empresaId
            """, new { empresaId }, cancellationToken: ct)) == "PGC_NORMAL";
    }

    // ------------------------------------------------------------------ estados complementarios
    private sealed class MovOrigenFila { public string Codigo { get; set; } = ""; public string Origen { get; set; } = ""; public decimal Debe { get; set; } public decimal Haber { get; set; } }

    /// <summary>Movimientos del ejercicio por cuenta y origen, sin apertura, regularización ni cierre.</summary>
    private static async Task<List<MovimientoOrigen>> MovimientosOrigenAsync(NpgsqlConnection cn, long empresaId, long ejercicioId, long canalId,
        CancellationToken ct)
        => (await cn.QueryAsync<MovOrigenFila>(new CommandDefinition("""
               SELECT c.codigo, s.origen, SUM(m.debe) AS debe, SUM(m.haber) AS haber
               FROM conta.v_mayor m JOIN conta.cuenta c ON c.id = m.cuenta_id JOIN conta.asiento s ON s.id = m.asiento_id
               WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND m.canal_base_id = @canalId
                 AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE')
               GROUP BY c.codigo, s.origen
               """, new { empresaId, ejercicioId, canalId }, cancellationToken: ct)))
           .Select(f => new MovimientoOrigen(f.Codigo, f.Origen, f.Debe, f.Haber)).ToList();

    /// <summary>Saldos del asiento de apertura del ejercicio (debe − haber).</summary>
    private static async Task<Dictionary<string, decimal>> AperturaAsync(NpgsqlConnection cn, long empresaId, long ejercicioId, long canalId, CancellationToken ct)
        => (await cn.QueryAsync<MovFila>(new CommandDefinition("""
               SELECT c.codigo, SUM(m.debe) AS debe, SUM(m.haber) AS haber
               FROM conta.v_mayor m JOIN conta.cuenta c ON c.id = m.cuenta_id JOIN conta.asiento s ON s.id = m.asiento_id
               WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND m.canal_base_id = @canalId AND s.origen = 'APERTURA'
               GROUP BY c.codigo
               """, new { empresaId, ejercicioId, canalId }, cancellationToken: ct))).ToDictionary(f => f.Codigo, f => f.Debe - f.Haber);

    public async Task<EcpnDto> PatrimonioNetoAsync(long empresaId, long ejercicioId, long? canalId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var ej = c.Ejercicio;
        var movs = await MovimientosOrigenAsync(cn, empresaId, ej.Id, c.Canal.Id, ct);
        var apertura = await AperturaAsync(cn, empresaId, ej.Id, c.Canal.Id, ct);
        var pyg = GeneradorEstados.Calcular(ModelosNormales.PerdidasGanancias, await SaldosAsync(cn, empresaId, c, ej.Id, ej.FechaInicio, ej.FechaFin, ct));
        var e = GeneradorEcpn.Calcular(apertura, movs, pyg["RE"]);
        var balance = GeneradorEstados.Calcular(ModelosNormales.Balance, await SaldosAsync(cn, empresaId, c, ej.Id, ej.FechaInicio, ej.FechaFin, ct));
        var avisos = e.Avisos.ToList();
        var finalEcpn = e.ParteB.Single(f => f.Id == "FIN").Importes["TOTAL"];
        if (finalEcpn != balance["PA"])
            avisos.Add($"El saldo final del estado ({finalEcpn:N2}) no coincide con el patrimonio neto del balance ({balance["PA"]:N2}): " +
                       "revisa movimientos de patrimonio neto contra cuentas de resultados o del ejercicio anterior sin apertura.");
        if (apertura.Count == 0) avisos.Add("El ejercicio no tiene asiento de apertura: el saldo inicial es cero.");
        return new EcpnDto(c.Canal, c.Canales, ej.Codigo, ej.FechaInicio, ej.FechaFin, e.ParteA, e.Columnas, e.ParteB, balance["PA"], avisos);
    }

    public async Task<EfeDto> FlujosEfectivoAsync(long empresaId, long ejercicioId, long? canalId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var ej = c.Ejercicio;
        var movs = await MovimientosOrigenAsync(cn, empresaId, ej.Id, c.Canal.Id, ct);
        var apertura = await AperturaAsync(cn, empresaId, ej.Id, c.Canal.Id, ct);
        var (lineas, avisos) = GeneradorEfe.Calcular(movs, apertura.Where(x => x.Key.StartsWith("57", StringComparison.Ordinal)).Sum(x => x.Value));
        var lista = avisos.ToList();
        if (!await NormalAsync(cn, empresaId, null, ct))
            lista.Add("El estado de flujos de efectivo no es obligatorio en el PGC de PYMES ni en las cuentas abreviadas.");
        return new EfeDto(c.Canal, c.Canales, ej.Codigo, ej.FechaInicio, ej.FechaFin, lineas, lista);
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

    // ------------------------------------------------------------------ memoria
    private sealed class EmpresaMemoriaFila
    {
        public string Nombre { get; set; } = "";
        public string Nif { get; set; } = "";
        public string FormaJuridica { get; set; } = "";
        public string ImpuestoIndirecto { get; set; } = "";
        public string? Direccion { get; set; }
        public string? Poblacion { get; set; }
    }

    public async Task<MemoriaDto> MemoriaAsync(long empresaId, long ejercicioId, long? canalId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var c = await ContextoAsync(cn, empresaId, ejercicioId, canalId, ct);
        var ej = c.Ejercicio;
        var emp = await cn.QuerySingleAsync<EmpresaMemoriaFila>(new CommandDefinition("""
            SELECT e.nombre, n.numero AS nif, e.forma_juridica, e.impuesto_indirecto, n.direccion, n.poblacion
            FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id WHERE e.id = @empresaId
            """, new { empresaId }, cancellationToken: ct));
        var normal = await NormalAsync(cn, empresaId, null, ct);
        var saldos = await SaldosAsync(cn, empresaId, c, ej.Id, ej.FechaInicio, ej.FechaFin, ct);
        var movs = await MovimientosOrigenAsync(cn, empresaId, ej.Id, c.Canal.Id, ct);
        var apertura = await AperturaAsync(cn, empresaId, ej.Id, c.Canal.Id, ct);
        var pyg = GeneradorEstados.Calcular(ModelosNormales.PerdidasGanancias, saldos);
        var bal = GeneradorEstados.Calcular(ModelosNormales.Balance, saldos);
        var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        string N(decimal v) => v.ToString("N2", es);
        decimal Saldo(string prefijos, bool acreedor = false)
            => saldos.Where(x => prefijos.Split(' ').Any(p => x.Codigo.StartsWith(p, StringComparison.Ordinal))).Sum(x => acreedor ? -x.Saldo : x.Saldo);
        decimal Ini(string prefijos, bool acreedor = false)
            => apertura.Where(x => prefijos.Split(' ').Any(p => x.Key.StartsWith(p, StringComparison.Ordinal))).Sum(x => acreedor ? -x.Value : x.Value);
        decimal Debe(string prefijos) => movs.Where(m => prefijos.Split(' ').Any(p => m.Codigo.StartsWith(p, StringComparison.Ordinal))).Sum(m => m.Debe);
        decimal Haber(string prefijos) => movs.Where(m => prefijos.Split(' ').Any(p => m.Codigo.StartsWith(p, StringComparison.Ordinal))).Sum(m => m.Haber);

        var secciones = new List<SeccionMemoria>();
        void Sec(string n, string t, IReadOnlyList<string> p, params TablaMemoria[] tablas) => secciones.Add(new SeccionMemoria(n, t, p, tablas));

        Sec("1", "Actividad de la empresa",
        [
            $"{emp.Nombre} (NIF {emp.Nif}), {FormaTexto(emp.FormaJuridica)}" + (emp.Direccion is null ? "." : $", con domicilio en {emp.Direccion}{(emp.Poblacion is null ? "" : ", " + emp.Poblacion)}."),
            "[Describir la actividad principal y, en su caso, el grupo al que pertenece.]"
        ]);
        Sec("2", "Bases de presentación de las cuentas anuales",
        [
            $"Las cuentas anuales del ejercicio {ej.Codigo} (del {ej.FechaInicio:dd/MM/yyyy} al {ej.FechaFin:dd/MM/yyyy}) se han obtenido de los registros contables " +
            $"de la sociedad y se presentan de acuerdo con el {(normal ? "Plan General de Contabilidad (RD 1514/2007)" : "Plan General de Contabilidad de Pequeñas y Medianas Empresas (RD 1515/2007)")}, " +
            "con sus modificaciones, de forma que muestran la imagen fiel del patrimonio, de la situación financiera y de los resultados.",
            "No se han aplicado principios contables no obligatorios. Las cifras se expresan en euros y se presentan comparadas con las del ejercicio anterior.",
            "[Indicar aspectos críticos de la valoración y estimación de la incertidumbre, cambios de criterio y corrección de errores, si los hay.]"
        ]);
        // Aplicación del resultado: la registrada en el ejercicio siguiente, si existe
        var aplicacion = (await cn.QueryAsync<(string Codigo, decimal Haber)>(new CommandDefinition("""
            SELECT c.codigo, SUM(m.haber - m.debe) FROM conta.v_mayor m JOIN conta.cuenta c ON c.id = m.cuenta_id JOIN conta.asiento s ON s.id = m.asiento_id
            JOIN core.ejercicio e ON e.id = m.ejercicio_id
            WHERE m.empresa_id = @empresaId AND m.canal_base_id = @canal AND s.origen = 'APLICACION' AND e.fecha_inicio > @fin
              AND c.codigo NOT LIKE '129%'
            GROUP BY c.codigo HAVING SUM(m.haber - m.debe) <> 0 ORDER BY c.codigo
            """, new { empresaId, canal = c.Canal.Id, fin = ej.FechaFin }, cancellationToken: ct))).ToList();
        Sec("3", "Aplicación de resultados",
        [
            $"El resultado del ejercicio es de {N(pyg["RE"])} euros ({(pyg["RE"] >= 0 ? "beneficio" : "pérdida")}).",
            aplicacion.Count > 0 ? "La aplicación aprobada es la siguiente:" : "[Incluir la propuesta de aplicación del resultado que formulan los administradores.]"
        ], aplicacion.Count > 0
            ? [new TablaMemoria("Distribución", ["Aplicación", "Importe"], aplicacion.Select(a => (IReadOnlyList<string>)[$"{a.Codigo} {c.Nombre(a.Codigo)}", N(a.Haber)]).ToList())]
            : []);
        Sec("4", "Normas de registro y valoración",
        [
            "Inmovilizado intangible y material: se valoran por su precio de adquisición o coste de producción, menos la amortización acumulada y las " +
            "pérdidas por deterioro. La amortización se calcula de forma sistemática en función de la vida útil de cada elemento.",
            "Instrumentos financieros: los créditos y débitos por operaciones comerciales se valoran por su valor nominal cuando el efecto de no actualizar " +
            "no es significativo; las deudas con entidades de crédito, por el importe recibido más los intereses devengados.",
            "Existencias: precio de adquisición o coste de producción; se dotan correcciones si el valor neto realizable es inferior.",
            $"Impuestos indirectos ({emp.ImpuestoIndirecto}): el soportado no deducible forma parte del precio de adquisición.",
            "Impuesto sobre beneficios: gasto o ingreso por impuesto corriente y diferido.",
            "Ingresos y gastos: se registran según el criterio del devengo.",
            "[Ampliar o suprimir según las operaciones de la sociedad.]"
        ]);
        TablaMemoria Movimiento(string titulo, (string Texto, string Bruto, string Amort)[] grupos)
            => new(titulo, ["Partida", "Saldo inicial", "Entradas", "Salidas", "Saldo final"],
                grupos.SelectMany(g => new IReadOnlyList<string>[]
                {
                    [g.Texto + " (coste)", N(Ini(g.Bruto)), N(Debe(g.Bruto)), N(Haber(g.Bruto)), N(Saldo(g.Bruto))],
                    [g.Texto + " (amortización acumulada)", N(Ini(g.Amort, true)), N(Haber(g.Amort)), N(Debe(g.Amort)), N(Saldo(g.Amort, true))],
                }).ToList());
        Sec("5", "Inmovilizado material, intangible e inversiones inmobiliarias",
            ["Movimientos del ejercicio:"],
            Movimiento("Movimientos", [("Inmovilizado intangible", "20", "280"), ("Inmovilizado material", "21 23", "281"), ("Inversiones inmobiliarias", "22", "282")]));
        Sec("6", "Activos financieros",
            ["Saldos al cierre por categorías:"],
            new TablaMemoria("Activos financieros", ["Categoría", "Largo plazo", "Corto plazo"],
            [
                ["Créditos, derivados y otros", N(Saldo("24 25 26")), N(Saldo("43 44 53 54 55 56", false) - Saldo("555 556 560 561", false))],
                ["Efectivo y otros activos líquidos", "", N(Saldo("57"))],
            ]));
        Sec("7", "Pasivos financieros",
            ["Saldos al cierre por categorías:"],
            new TablaMemoria("Pasivos financieros", ["Categoría", "Largo plazo", "Corto plazo"],
            [
                ["Deudas con entidades de crédito", N(Saldo("1605 170", true)), N(Saldo("5105 520 527", true))],
                ["Acreedores por arrendamiento financiero", N(Saldo("1625 174", true)), N(Saldo("5125 524", true))],
                ["Otros pasivos financieros y acreedores comerciales", N(Saldo("171 172 173 175 180 185", true)), N(Saldo("40 41 465 521 523 526", true))],
            ]));
        Sec("8", "Fondos propios",
        [
            $"El capital escriturado al cierre es de {N(Saldo("100 101 102", true))} euros.",
            $"Reservas: {N(Saldo("11", true))} euros, de las que {N(Saldo("112", true))} corresponden a la reserva legal."
        ]);
        Sec("9", "Situación fiscal",
            ["Saldos con las Administraciones Públicas al cierre:", $"Gasto por impuesto sobre beneficios del ejercicio: {N(Saldo("630 633 638"))} euros.",
             "[Conciliar el resultado contable con la base imponible e indicar los ejercicios abiertos a inspección.]"],
            new TablaMemoria("Administraciones Públicas", ["Concepto", "Deudor", "Acreedor"],
            [
                ["Hacienda Pública, impuestos indirectos", N(Saldo("470 472")), N(Saldo("475 477", true))],
                ["Retenciones y pagos a cuenta", N(Saldo("473")), N(Saldo("4751", true))],
                ["Organismos de la Seguridad Social", N(Saldo("471")), N(Saldo("476", true))],
            ]));
        Sec("10", "Ingresos y gastos",
            ["Desglose de las principales partidas de la cuenta de pérdidas y ganancias:"],
            new TablaMemoria("Ingresos y gastos", ["Concepto", "Importe"],
            [
                ["Importe neto de la cifra de negocios", N(pyg["1"])], ["  Ventas", N(pyg["1a"])], ["  Prestaciones de servicios", N(pyg["1b"])],
                ["Aprovisionamientos", N(pyg["4"])], ["Gastos de personal", N(pyg["6"])], ["  Cargas sociales", N(pyg["6b"])],
                ["Otros gastos de explotación", N(pyg["7"])], ["Amortización del inmovilizado", N(pyg["8"])],
            ]));
        var subvMovs = Haber("130 131 132 94");
        Sec("11", "Subvenciones, donaciones y legados",
            [subvMovs != 0 || Saldo("130 131 132", true) != 0
                ? $"Recibidas en el ejercicio: {N(subvMovs)} euros. Imputadas a resultados: {N(Saldo("746", true))} euros. Pendientes de imputar al cierre: {N(bal["PA3"])} euros."
                : "La sociedad no ha recibido subvenciones, donaciones ni legados."]);
        Sec("12", "Hechos posteriores al cierre", ["[Indicar los hechos posteriores relevantes o que no los ha habido.]"]);
        Sec("13", "Otra información", ["Número medio de personas empleadas: [indicar].", "[Honorarios de auditoría, operaciones con partes vinculadas, información medioambiental, aplazamiento de pago a proveedores…]"]);

        var avisos = new List<string> { "Borrador generado con las cifras de la contabilidad: completa los textos entre corchetes y revisa cada apartado antes de formular las cuentas." };
        if (bal["TA"] != bal["TP"]) avisos.Add("El balance no cuadra: revisa las cuentas sin clasificar antes de usar la memoria.");
        return new MemoriaDto(emp.Nombre, emp.Nif, ej.Codigo, ej.FechaInicio, ej.FechaFin, normal ? "NORMAL" : "PYMES", secciones, avisos);
    }

    private static string FormaTexto(string forma) => forma switch
    {
        "SL" => "sociedad de responsabilidad limitada", "SLU" => "sociedad limitada unipersonal", "SA" => "sociedad anónima",
        "COOPERATIVA" => "sociedad cooperativa", "AUTONOMO" => "empresario individual", "COMUNIDAD_BIENES" => "comunidad de bienes",
        "SOCIEDAD_CIVIL" => "sociedad civil", "ASOCIACION" => "asociación", _ => "entidad"
    };

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
