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
/// Casillas del diseño de registro (versión 1.4): régimen general en cuatro tipos ([01]-[03], [25]-[27], [04]-[06], [07]-[09]), recargo de
/// equivalencia en otros cuatro ([10]-[21]), total [22], a deducir [23] y resultado [24]; «HI» es el hecho imponible (1 adquisición
/// intracomunitaria de bienes, 3 inversión del sujeto pasivo).
/// </summary>
public static class Calculador309
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static readonly (string B, string T, string C)[] General = [("01", "02", "03"), ("25", "26", "27"), ("04", "05", "06"), ("07", "08", "09")];
    private static readonly (string B, string T, string C)[] Recargo = [("10", "11", "12"), ("13", "14", "15"), ("16", "17", "18"), ("19", "20", "21")];

    public static void Calcular(IReadOnlyList<Linea309> lineas, bool recargoEquivalencia, DeclaracionCalculada d)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        decimal total = 0;
        var aib = lineas.Any(l => l.Clase == "ADQUISICION");
        var isp = lineas.Any(l => l.Clase == "ISP");
        Add("HI", "Hecho imponible: 1 adquisición intracomunitaria de bienes, 3 inversión del sujeto pasivo", aib || !isp ? 1 : 3);
        if (aib && isp)
            d.Avisos.Add("Hay adquisiciones intracomunitarias e inversión del sujeto pasivo en el periodo: el 309 se presenta por cada hecho imponible. " +
                         "El fichero los lleva juntos como adquisición intracomunitaria (1): sepáralos en el formulario si hace falta.");
        var tipos = lineas.GroupBy(l => l.Porcentaje).OrderByDescending(g => g.Key).ToList();
        if (tipos.Count > General.Length)
            throw new ReglaContableException($"Hay {tipos.Count} tipos de IVA distintos y el 309 admite {General.Length}.");
        foreach (var (g, c) in tipos.Zip(General))
        {
            var b = g.Sum(l => l.Base);
            var cuota = R(b * g.Key / 100m);
            Add(c.B, $"Régimen general al {g.Key:0.##} %: base imponible", b);
            Add(c.T, "Tipo %", g.Key);
            Add(c.C, $"Régimen general al {g.Key:0.##} %: cuota", cuota);
            total += cuota;
        }
        if (recargoEquivalencia)
        {
            var recargos = lineas.Where(l => l.Clase == "ADQUISICION" && l.PorcentajeRecargo != 0).GroupBy(l => l.PorcentajeRecargo)
                .OrderByDescending(g => g.Key).ToList();
            if (recargos.Count > Recargo.Length)
                throw new ReglaContableException($"Hay {recargos.Count} tipos de recargo distintos y el 309 admite {Recargo.Length}.");
            foreach (var (g, c) in recargos.Zip(Recargo))
            {
                var b = g.Sum(l => l.Base);
                var cuota = R(b * g.Key / 100m);
                Add(c.B, $"Recargo de equivalencia al {g.Key:0.##} %: base imponible", b);
                Add(c.T, "Tipo %", g.Key);
                Add(c.C, $"Recargo de equivalencia al {g.Key:0.##} %: cuota", cuota);
                total += cuota;
            }
        }
        Add("22", "Total cuota devengada", total);
        Add("23", "A deducir (solo autoliquidación complementaria)", 0);
        Add("24", "Resultado a ingresar", total);
        d.Resultado = R(total);
        d.AIngresar = Math.Max(0, d.Resultado);
        if (lineas.Count == 0) d.Avisos.Add("No hay adquisiciones intracomunitarias ni operaciones con inversión del sujeto pasivo en el periodo: no hay que presentar el 309.");
        if (recargoEquivalencia && aib)
            d.Avisos.Add("El IVA y el recargo de equivalencia de las adquisiciones intracomunitarias están en el asiento de cada factura (más coste " +
                         "contra el IVA repercutido): al pagar el 309 se salda esa cuenta.");
    }
}
