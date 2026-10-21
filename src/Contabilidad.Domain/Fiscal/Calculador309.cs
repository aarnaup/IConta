namespace Contabilidad.Domain.Fiscal;

/// <summary>Línea de una compra que el comprador debe autoliquidar en el 309.</summary>
/// <param name="Clase">ADQUISICION (adquisición intracomunitaria de bienes) o ISP (inversión del sujeto pasivo, incluidos los servicios
/// recibidos de empresarios de otros países).</param>
/// <param name="PorcentajeRecargo">% de recargo de equivalencia del tipo (solo cuenta en las adquisiciones de bienes de un minorista en RE).</param>
public sealed record Linea309(string Clase, decimal Porcentaje, decimal PorcentajeRecargo, decimal Base);

/// <summary>
/// Modelo 309, declaración-liquidación no periódica del IVA: la presentan quienes no presentan el 303 (recargo de equivalencia,
/// régimen especial de la agricultura, ganadería y pesca) por las adquisiciones intracomunitarias de bienes y las operaciones en las que
/// son sujetos pasivos por inversión (art. 84.Uno.2º LIVA). No pueden deducir: el resultado es todo a ingresar. El minorista en recargo de
/// equivalencia liquida además el recargo de sus adquisiciones intracomunitarias de bienes (art. 156 LIVA).
/// Claves descriptivas: VERIFICA la numeración de casillas con el formulario del 309.
/// </summary>
public static class Calculador309
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static void Calcular(IReadOnlyList<Linea309> lineas, bool recargoEquivalencia, DeclaracionCalculada d)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        decimal total = 0;
        foreach (var (clase, nombre, k) in new[] { ("ADQUISICION", "Adquisiciones intracomunitarias de bienes", "AIB"),
                                                   ("ISP", "Operaciones con inversión del sujeto pasivo", "ISP") })
        {
            foreach (var g in lineas.Where(l => l.Clase == clase).GroupBy(l => l.Porcentaje).OrderBy(g => g.Key))
            {
                var b = g.Sum(l => l.Base);
                var c = R(b * g.Key / 100m);
                var pct = g.Key.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                Add($"{k}{pct}B", $"{nombre} al {g.Key:0.##} %: base", b);
                Add($"{k}{pct}C", $"{nombre} al {g.Key:0.##} %: cuota", c);
                total += c;
            }
        }
        if (recargoEquivalencia)
            foreach (var g in lineas.Where(l => l.Clase == "ADQUISICION" && l.PorcentajeRecargo != 0).GroupBy(l => l.PorcentajeRecargo).OrderBy(g => g.Key))
            {
                var b = g.Sum(l => l.Base);
                var c = R(b * g.Key / 100m);
                var pct = g.Key.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                Add($"RE{pct}B", $"Recargo de equivalencia al {g.Key:0.##} % de las adquisiciones intracomunitarias: base", b);
                Add($"RE{pct}C", $"Recargo de equivalencia al {g.Key:0.##} %: cuota", c);
                total += c;
            }
        Add("TOT", "Total cuotas devengadas", total);
        Add("DED", "A deducir (solo autoliquidación complementaria)", 0);
        Add("RES", "Resultado a ingresar", total);
        d.Resultado = R(total);
        d.AIngresar = Math.Max(0, d.Resultado);
        if (lineas.Count == 0) d.Avisos.Add("No hay adquisiciones intracomunitarias ni operaciones con inversión del sujeto pasivo en el periodo: no hay que presentar el 309.");
        if (recargoEquivalencia && lineas.Any(l => l.Clase == "ADQUISICION"))
            d.Avisos.Add("El recargo de equivalencia de las adquisiciones intracomunitarias no está en el asiento de la factura: contabilízalo como más coste (contra 4750) al pagar el 309.");
        d.Avisos.Add("Claves descriptivas: VERIFICA la numeración de casillas con el formulario del 309 antes de presentar.");
    }
}
