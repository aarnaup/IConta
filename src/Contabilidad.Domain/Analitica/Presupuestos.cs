namespace Contabilidad.Domain.Analitica;

/// <summary>Reglas de los presupuestos: reparto en periodos, signo del real y desviaciones.</summary>
public static class Presupuestos
{
    /// <summary>Importe anual repartido a partes iguales entre los periodos; el redondeo va al último.</summary>
    public static decimal[] RepartirLineal(decimal total, int periodos)
    {
        if (periodos <= 0) throw new ReglaContableException("El ejercicio no tiene periodos.");
        var cuota = Math.Round(total / periodos, 2, MidpointRounding.AwayFromZero);
        var r = Enumerable.Repeat(cuota, periodos).ToArray();
        r[^1] = total - cuota * (periodos - 1);
        return r;
    }

    /// <summary>
    /// Signo con el que se compara lo real: los ingresos (grupo 7), el patrimonio y la financiación (grupo 1) y los ingresos
    /// imputados al patrimonio (grupo 9) se presupuestan en positivo con saldo acreedor; el resto, con saldo deudor.
    /// </summary>
    public static int Signo(string cuentaPatron) => cuentaPatron.Length > 0 && cuentaPatron[0] is '1' or '7' or '9' ? -1 : 1;

    /// <summary>Real con el signo del presupuesto a partir de debe y haber.</summary>
    public static decimal Real(string cuentaPatron, decimal debe, decimal haber) => Signo(cuentaPatron) * (debe - haber);

    /// <summary>Porcentaje de ejecución (real / presupuesto), sin decimales de más; nulo si no hay presupuesto.</summary>
    public static decimal? Ejecucion(decimal presupuesto, decimal real)
        => presupuesto == 0 ? null : Math.Round(real / presupuesto * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Desviación favorable o desfavorable: en gastos, gastar menos de lo previsto es favorable; en ingresos, ingresar más.
    /// </summary>
    public static bool Favorable(string cuentaPatron, decimal presupuesto, decimal real)
        => Signo(cuentaPatron) < 0 ? real >= presupuesto : real <= presupuesto;

    /// <summary>
    /// Líneas que incluyen a otras (prefijo de su cuenta con el mismo elemento, o sin elemento): en los totales lo real se
    /// sumaría dos veces.
    /// </summary>
    public static IReadOnlyList<string> Solapados(IEnumerable<(string Patron, long? ElementoId)> lineas)
    {
        var l = lineas.Distinct().ToList();
        return l.SelectMany(a => l.Where(b => b != a && (a.ElementoId is null || a.ElementoId == b.ElementoId)
                                         && b.Patron.StartsWith(a.Patron, StringComparison.Ordinal))
                                  .Select(b => $"{a.Patron} incluye {b.Patron}"))
            .Distinct().ToList();
    }

    /// <summary>Patrón válido: dígitos, de 1 a 15.</summary>
    public static string ValidarPatron(string? patron)
    {
        var p = (patron ?? "").Trim();
        if (p.Length is 0 or > 15 || !p.All(char.IsAsciiDigit))
            throw new ReglaContableException($"La cuenta del presupuesto «{patron}» no es válida: indica la cuenta o su prefijo (solo dígitos).");
        return p;
    }
}
