namespace Contabilidad.Domain.Cierre;

/// <summary>Destinos del resultado que se pueden acordar.</summary>
public static class DestinoResultado
{
    public const string ReservaLegal = "RESERVA_LEGAL";            // 112
    public const string ReservaVoluntaria = "RESERVA_VOLUNTARIA";  // 113
    public const string Remanente = "REMANENTE";                    // 120
    public const string CompensarPerdidas = "COMPENSAR_PERDIDAS";  // 121 (abono): compensa pérdidas de ejercicios anteriores
    public const string Dividendos = "DIVIDENDOS";                  // 557 (lo repartido a cuenta) y 526 (el resto)
    public const string ResultadosNegativos = "RESULTADOS_NEGATIVOS"; // 121 (cargo): la pérdida queda pendiente de compensar
    public const string Cuenta = "CUENTA";                          // otra cuenta del grupo 1 (p. ej. reservas estatutarias)

    public static readonly string[] ConBeneficio = [ReservaLegal, ReservaVoluntaria, Remanente, CompensarPerdidas, Dividendos, Cuenta];
    public static readonly string[] ConPerdida = [ResultadosNegativos, ReservaVoluntaria, Remanente, ReservaLegal, Cuenta];
}

/// <param name="Modo">IMPORTE (valor en euros), PORCENTAJE (del resultado) o RESTO (lo que quede; como mucho una línea).</param>
public sealed record LineaAplicacion(string Destino, string Modo, decimal Valor, long? CuentaId = null);

/// <summary>Cuentas de la empresa para cada destino; null si no existe (solo es un error si se usa).</summary>
public sealed record CuentasAplicacion(long Resultado, long? ReservaLegal, long? ReservaVoluntaria, long? Remanente,
    long? ResultadosNegativos, long? DividendoAPagar, long? DividendoACuenta);

/// <param name="Resultado">Resultado pendiente de aplicar (saldo acreedor de la 129): positivo, beneficio; negativo, pérdida.</param>
/// <param name="DividendoACuenta">Saldo deudor de la 557: lo ya repartido a cuenta, que se cancela con el dividendo acordado.</param>
/// <param name="PerdidasAnteriores">Saldo deudor de la 121.</param>
public sealed record SaldosAplicacion(long CanalBaseId, string Canal, decimal Resultado, decimal DividendoACuenta, decimal PerdidasAnteriores);

public sealed record RepartoDestino(string Destino, long CuentaId, decimal Importe);

public sealed record AplicacionCanal(long CanalBaseId, decimal Resultado, IReadOnlyList<RepartoDestino> Reparto);

/// <summary>
/// Asiento de aplicación del resultado, canal a canal.
/// Beneficio: 129 (D) a reservas, remanente, 121 y dividendos (H). Pérdida: 121 o reservas (D) a 129 (H).
/// </summary>
public static class GeneradorAplicacionResultado
{
    public static (IReadOnlyList<ApunteGenerado> Apuntes, IReadOnlyList<AplicacionCanal> Canales) Generar(
        IReadOnlyList<SaldosAplicacion> saldos, IReadOnlyList<LineaAplicacion> lineas, CuentasAplicacion cuentas)
    {
        Validar(lineas);
        var apuntes = new List<ApunteGenerado>();
        var canales = new List<AplicacionCanal>();

        foreach (var s in saldos.Where(x => x.Resultado != 0).OrderBy(x => x.CanalBaseId))
        {
            var beneficio = s.Resultado > 0;
            var total = Math.Abs(s.Resultado);
            var permitidos = beneficio ? DestinoResultado.ConBeneficio : DestinoResultado.ConPerdida;
            foreach (var l in lineas.Where(l => !permitidos.Contains(l.Destino)))
                throw new ReglaContableException(beneficio
                    ? $"Con beneficio no se puede aplicar a {Texto(l.Destino)}."
                    : $"Con pérdida no se puede aplicar a {Texto(l.Destino)}: se lleva a resultados negativos o se compensa con reservas.");

            // Importes: fijos, porcentajes y, por último, el resto
            var importes = lineas.Select(l => l.Modo switch
            {
                "IMPORTE" => Math.Round(l.Valor, 2),
                "PORCENTAJE" => Math.Round(total * l.Valor / 100m, 2, MidpointRounding.AwayFromZero),
                _ => 0m
            }).ToArray();
            var asignado = importes.Sum();
            var iResto = lineas.ToList().FindIndex(l => l.Modo == "RESTO");
            if (asignado > total)
                throw new ReglaContableException($"Lo repartido ({asignado:N2}) supera el resultado a aplicar ({total:N2}) en el canal {s.Canal}.");
            if (iResto >= 0) importes[iResto] = total - asignado;
            else if (asignado != total)
                throw new ReglaContableException($"Lo repartido ({asignado:N2}) no coincide con el resultado a aplicar ({total:N2}) en el canal {s.Canal}.",
                    "Añade una línea con «el resto» o ajusta los importes.");

            var reparto = new List<RepartoDestino>();
            for (var i = 0; i < lineas.Count; i++)
            {
                if (importes[i] == 0) continue;
                var l = lineas[i];
                if (l.Destino == DestinoResultado.Dividendos)
                {
                    // Lo repartido a cuenta (557) se cancela primero; el resto queda a pagar (526)
                    if (s.DividendoACuenta > importes[i])
                        throw new ReglaContableException($"El dividendo a cuenta ya repartido ({s.DividendoACuenta:N2}) supera el dividendo acordado ({importes[i]:N2}).");
                    if (s.DividendoACuenta > 0)
                        reparto.Add(new(l.Destino, Requerida(cuentas.DividendoACuenta, "dividendo activo a cuenta (557)"), s.DividendoACuenta));
                    if (importes[i] - s.DividendoACuenta > 0)
                        reparto.Add(new(l.Destino, Requerida(cuentas.DividendoAPagar, "dividendo activo a pagar (526)"), importes[i] - s.DividendoACuenta));
                    continue;
                }
                if (l.Destino == DestinoResultado.CompensarPerdidas && importes[i] > s.PerdidasAnteriores)
                    throw new ReglaContableException($"Se compensan {importes[i]:N2} de pérdidas anteriores y solo quedan {s.PerdidasAnteriores:N2} en la 121.");
                reparto.Add(new(l.Destino, CuentaDe(l, cuentas), importes[i]));
            }

            // Beneficio: 129 al debe, destinos al haber. Pérdida: al revés.
            apuntes.Add(beneficio ? new(cuentas.Resultado, s.CanalBaseId, total, 0) : new(cuentas.Resultado, s.CanalBaseId, 0, total));
            apuntes.AddRange(reparto.Select(r => beneficio ? new ApunteGenerado(r.CuentaId, s.CanalBaseId, 0, r.Importe)
                                                           : new ApunteGenerado(r.CuentaId, s.CanalBaseId, r.Importe, 0)));
            canales.Add(new AplicacionCanal(s.CanalBaseId, s.Resultado, reparto));
        }

        if (canales.Count == 0) throw new ReglaContableException("No hay resultado pendiente de aplicar: la cuenta 129 no tiene saldo.");
        return (apuntes, canales);
    }

    /// <summary>
    /// Reserva legal mínima (art. 274 LSC): el 10 % del beneficio hasta que la reserva alcance el 20 % del capital social.
    /// </summary>
    public static decimal ReservaLegalMinima(decimal beneficio, decimal capital, decimal reservaLegalActual)
        => beneficio <= 0 || capital <= 0 ? 0
           : Math.Min(Math.Round(beneficio * 0.10m, 2, MidpointRounding.AwayFromZero), Math.Max(0, capital * 0.20m - reservaLegalActual));

    public static string Texto(string destino) => destino switch
    {
        DestinoResultado.ReservaLegal => "reserva legal",
        DestinoResultado.ReservaVoluntaria => "reservas voluntarias",
        DestinoResultado.Remanente => "remanente",
        DestinoResultado.CompensarPerdidas => "compensar pérdidas de ejercicios anteriores",
        DestinoResultado.Dividendos => "dividendos",
        DestinoResultado.ResultadosNegativos => "resultados negativos de ejercicios anteriores",
        DestinoResultado.Cuenta => "otra cuenta",
        _ => destino
    };

    private static void Validar(IReadOnlyList<LineaAplicacion> lineas)
    {
        if (lineas.Count == 0) throw new ReglaContableException("Indica al menos un destino para el resultado.");
        var todos = DestinoResultado.ConBeneficio.Concat(DestinoResultado.ConPerdida).ToHashSet();
        foreach (var l in lineas)
        {
            if (!todos.Contains(l.Destino)) throw new ReglaContableException($"Destino no válido: {l.Destino}.");
            if (l.Modo is not ("IMPORTE" or "PORCENTAJE" or "RESTO")) throw new ReglaContableException($"Modo no válido: {l.Modo} (IMPORTE, PORCENTAJE o RESTO).");
            if (l.Modo == "IMPORTE" && l.Valor <= 0) throw new ReglaContableException("Los importes deben ser mayores que cero.");
            if (l.Modo == "PORCENTAJE" && l.Valor is <= 0 or > 100) throw new ReglaContableException("Los porcentajes deben estar entre 0 y 100.");
            if (l.Destino == DestinoResultado.Cuenta && l.CuentaId is null) throw new ReglaContableException("Indica la cuenta de destino.");
        }
        if (lineas.Count(l => l.Modo == "RESTO") > 1) throw new ReglaContableException("Solo una línea puede llevar «el resto».");
        if (lineas.Where(l => l.Modo == "PORCENTAJE").Sum(l => l.Valor) > 100) throw new ReglaContableException("Los porcentajes suman más del 100 %.");
    }

    private static long CuentaDe(LineaAplicacion l, CuentasAplicacion c) => l.Destino switch
    {
        DestinoResultado.ReservaLegal => Requerida(c.ReservaLegal, "reserva legal (112)"),
        DestinoResultado.ReservaVoluntaria => Requerida(c.ReservaVoluntaria, "reservas voluntarias (113)"),
        DestinoResultado.Remanente => Requerida(c.Remanente, "remanente (120)"),
        DestinoResultado.CompensarPerdidas or DestinoResultado.ResultadosNegativos =>
            Requerida(c.ResultadosNegativos, "resultados negativos de ejercicios anteriores (121)"),
        _ => l.CuentaId!.Value
    };

    private static long Requerida(long? id, string nombre)
        => id ?? throw new ReglaContableException($"La empresa no tiene la cuenta de {nombre}: créala en el plan contable.");
}
