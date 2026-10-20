using System.Globalization;
using System.Text;

namespace Contabilidad.Domain.Fiscal;

/// <summary>
/// Una línea del libro registro: una factura y uno de sus tipos de IVA/IGIC. Una factura con varios tipos ocupa varias líneas;
/// el total y la retención solo van en la primera para no sumarlos dos veces.
/// </summary>
public sealed class LineaLibroDto
{
    public long FacturaId { get; set; }
    public int? NumeroRegistro { get; set; }
    public DateOnly FechaExpedicion { get; set; }
    public DateOnly? FechaOperacion { get; set; }
    public DateOnly FechaContable { get; set; }
    public string? Serie { get; set; }
    public string Numero { get; set; } = "";
    public string TipoFactura { get; set; } = "";
    public string Nif { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string TipoOperacion { get; set; } = "";
    public decimal Base { get; set; }
    public decimal Porcentaje { get; set; }
    public decimal Cuota { get; set; }
    public decimal PorcentajeRecargo { get; set; }
    public decimal CuotaRecargo { get; set; }
    public decimal CuotaDeducible { get; set; }
    public bool BienInversion { get; set; }
    public decimal? Retencion { get; set; }
    public decimal? TotalFactura { get; set; }
    public string? FacturaRectificada { get; set; }
    public int? AsientoNumero { get; set; }
    public string? Descripcion { get; set; }
    public bool PrimeraLinea { get; set; }
}

public sealed record TotalLibro(string TipoOperacion, decimal Porcentaje, decimal PorcentajeRecargo, int Lineas, decimal Base, decimal Cuota,
    decimal CuotaRecargo, decimal CuotaDeducible);

public static class LibrosRegistro
{
    public static readonly string[] Libros = ["EMITIDAS", "RECIBIDAS"];

    /// <summary>Totales por tipo de operación y porcentaje: lo que se contrasta con el 303/420.</summary>
    public static IReadOnlyList<TotalLibro> Totales(IEnumerable<LineaLibroDto> lineas)
        => lineas.GroupBy(l => (l.TipoOperacion, l.Porcentaje, l.PorcentajeRecargo))
            .OrderBy(g => g.Key.TipoOperacion, StringComparer.Ordinal).ThenByDescending(g => g.Key.Porcentaje).ThenBy(g => g.Key.PorcentajeRecargo)
            .Select(g => new TotalLibro(g.Key.TipoOperacion, g.Key.Porcentaje, g.Key.PorcentajeRecargo, g.Count(), g.Sum(l => l.Base), g.Sum(l => l.Cuota),
                g.Sum(l => l.CuotaRecargo), g.Sum(l => l.CuotaDeducible)))
            .ToList();

    /// <summary>
    /// CSV para hojas de cálculo en español: separador «;», coma decimal, fechas dd/mm/aaaa, UTF-8 con BOM
    /// (para que Excel reconozca los acentos) y campos entrecomillados cuando hace falta.
    /// </summary>
    public static byte[] Csv(string libro, string impuesto, IEnumerable<LineaLibroDto> lineas)
    {
        var emitidas = libro == "EMITIDAS";
        var cab = emitidas
            ? new[] { "Fecha expedición", "Fecha operación", "Serie", "Número", "Tipo de factura", "NIF destinatario", "Nombre destinatario",
                      "Tipo de operación", "Base imponible", $"Tipo {impuesto} %", $"Cuota {impuesto}", "Tipo recargo %", "Cuota recargo",
                      "Retención IRPF", "Total factura", "Factura rectificada", "Asiento", "Descripción" }
            : new[] { "Nº registro", "Fecha registro", "Fecha expedición", "Fecha operación", "Serie", "Número", "Tipo de factura", "NIF emisor",
                      "Nombre emisor", "Tipo de operación", "Base imponible", $"Tipo {impuesto} %", $"Cuota {impuesto} soportada", "Tipo recargo %",
                      "Cuota recargo", "Cuota deducible", "Bien de inversión", "Retención IRPF", "Total factura", "Factura rectificada", "Asiento", "Descripción" };

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', cab.Select(Campo)));
        foreach (var l in lineas)
        {
            var comun = new[] { Fecha(l.FechaExpedicion), Fecha(l.FechaOperacion), l.Serie ?? "", l.Numero, Texto(l.TipoFactura), l.Nif, l.Nombre,
                                Texto(l.TipoOperacion), Importe(l.Base), Importe(l.Porcentaje) };
            string[] fila = emitidas
                ? [.. comun, Importe(l.Cuota), Importe(l.PorcentajeRecargo), Importe(l.CuotaRecargo), Importe(l.Retencion), Importe(l.TotalFactura),
                   l.FacturaRectificada ?? "", l.AsientoNumero?.ToString(CultureInfo.InvariantCulture) ?? "", l.Descripcion ?? ""]
                : [l.NumeroRegistro?.ToString(CultureInfo.InvariantCulture) ?? "", Fecha(l.FechaContable), .. comun, Importe(l.Cuota),
                   Importe(l.PorcentajeRecargo), Importe(l.CuotaRecargo), Importe(l.CuotaDeducible), l.BienInversion ? "Sí" : "No",
                   Importe(l.Retencion), Importe(l.TotalFactura), l.FacturaRectificada ?? "",
                   l.AsientoNumero?.ToString(CultureInfo.InvariantCulture) ?? "", l.Descripcion ?? ""];
            sb.AppendLine(string.Join(';', fila.Select(Campo)));
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string Texto(string clave) => clave switch
    {
        "ORDINARIA" => "Ordinaria", "SIMPLIFICADA" => "Simplificada", "RECTIFICATIVA" => "Rectificativa", "AUTOFACTURA" => "Autofactura",
        "INTRACOMUNITARIA" => "Intracomunitaria", "IMPORTACION" => "Importación",
        "INTERIOR" => "Interior", "EXENTA" => "Exenta", "NO_SUJETA" => "No sujeta", "INTRACOM_ADQUISICION" => "Adquisición intracomunitaria",
        "INTRACOM_ENTREGA" => "Entrega intracomunitaria", "EXPORTACION" => "Exportación", "INVERSION_SUJETO_PASIVO" => "Inversión del sujeto pasivo",
        _ => clave
    };

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static string Fecha(DateOnly? d) => d?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";
    private static string Importe(decimal? v) => v?.ToString("0.00", Es) ?? "";

    private static string Campo(string v)
        => v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
