using System.Globalization;
using System.Net;
using System.Text;

namespace Contabilidad.Domain.Facturacion;

public sealed class ParteImpreso
{
    public string Nombre { get; set; } = "";
    public string Nif { get; set; } = "";
    public string? Direccion { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Poblacion { get; set; }
    public string? Provincia { get; set; }
    public string? Pais { get; set; }
}

public sealed class LineaIvaImpreso
{
    public string Impuesto { get; set; } = "IVA";
    public string TipoOperacion { get; set; } = "INTERIOR";
    public decimal Base { get; set; }
    public decimal Porcentaje { get; set; }
    public decimal Cuota { get; set; }
    public decimal PorcentajeRecargo { get; set; }
    public decimal CuotaRecargo { get; set; }
}

public sealed class RetencionImpreso
{
    public decimal Base { get; set; }
    public decimal Porcentaje { get; set; }
    public decimal Importe { get; set; }
}

public sealed class VencimientoImpreso
{
    public DateOnly Fecha { get; set; }
    public decimal Importe { get; set; }
}

public sealed class FacturaImpreso
{
    public string TipoFactura { get; set; } = "ORDINARIA";
    public string? Serie { get; set; }
    public string Numero { get; set; } = "";
    public DateOnly FechaExpedicion { get; set; }
    public DateOnly? FechaOperacion { get; set; }
    public string? Descripcion { get; set; }
    public string Estado { get; set; } = "";
    public bool CriterioCaja { get; set; }
    public string? Rectificada { get; set; }
    public DateOnly? FechaRectificada { get; set; }
    public decimal BaseTotal { get; set; }
    public decimal CuotaTotal { get; set; }
    public decimal RecargoTotal { get; set; }
    public decimal RetencionTotal { get; set; }
    public decimal Total { get; set; }
    public string? FormaPago { get; set; }
    public string? Iban { get; set; }
    public ParteImpreso Emisor { get; set; } = new();
    public ParteImpreso Destinatario { get; set; } = new();
    public List<LineaIvaImpreso> Lineas { get; set; } = [];
    public List<RetencionImpreso> Retenciones { get; set; } = [];
    public List<VencimientoImpreso> Vencimientos { get; set; } = [];
}

/// <summary>
/// Factura emitida en HTML listo para imprimir (o guardar en PDF desde el navegador), con el contenido del art. 6 del
/// Reglamento de facturación (RD 1619/2012): número y serie, fechas, datos de emisor y destinatario, desglose por tipo,
/// y las menciones obligatorias (criterio de caja, exenciones, inversión del sujeto pasivo, entregas intracomunitarias,
/// rectificativa).
/// </summary>
public static class ImpresoFactura
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(decimal v) => v.ToString("N2", Es);
    private static string P(decimal v) => v.ToString("0.##", Es) + " %";
    private static string F(DateOnly d) => d.ToString("dd/MM/yyyy", Es);

    /// <summary>Menciones que exige el reglamento según las operaciones de la factura.</summary>
    public static IReadOnlyList<string> Menciones(FacturaImpreso f)
    {
        var m = new List<string>();
        var ops = f.Lineas.Select(l => l.TipoOperacion).ToHashSet();
        if (f.CriterioCaja) m.Add("Régimen especial del criterio de caja.");
        if (ops.Contains("INVERSION_SUJETO_PASIVO")) m.Add("Inversión del sujeto pasivo (art. 84.Uno.2º de la Ley 37/1992).");
        if (ops.Contains("INTRACOM_ENTREGA")) m.Add("Entrega intracomunitaria exenta (art. 25 de la Ley 37/1992).");
        if (ops.Contains("EXPORTACION")) m.Add("Exportación exenta (art. 21 de la Ley 37/1992).");
        if (ops.Contains("EXENTA")) m.Add("Operación exenta (art. 20 de la Ley 37/1992).");
        if (ops.Contains("NO_SUJETA")) m.Add("Operación no sujeta al impuesto.");
        return m;
    }

    public static string Html(FacturaImpreso f)
    {
        var numero = (f.Serie ?? "") + f.Numero;
        var titulo = f.TipoFactura switch
        {
            "RECTIFICATIVA" => "Factura rectificativa",
            "SIMPLIFICADA" => "Factura simplificada",
            _ => "Factura",
        };
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><title>{{E(titulo)}} {{E(numero)}}</title>
            <style>
              body { font: 13px/1.45 system-ui, sans-serif; color: #111; margin: 24px auto; max-width: 820px; padding: 0 16px; }
              h1 { font-size: 22px; margin: 0 0 4px; } .borrador { color: #b00; font-weight: 600; }
              .cab { display: flex; justify-content: space-between; gap: 24px; margin: 18px 0; }
              .parte { flex: 1; border: 1px solid #ccc; border-radius: 6px; padding: 10px 12px; }
              .parte h2 { font-size: 11px; text-transform: uppercase; letter-spacing: .05em; color: #555; margin: 0 0 4px; }
              table { width: 100%; border-collapse: collapse; margin: 12px 0; }
              th, td { padding: 6px 8px; border-bottom: 1px solid #ddd; text-align: left; }
              th { background: #f3f3f3; font-weight: 600; } .num { text-align: right; font-variant-numeric: tabular-nums; }
              .totales { width: 50%; margin-left: auto; } .totales tr.total td { font-weight: 700; font-size: 15px; border-top: 2px solid #111; }
              .menciones { font-size: 12px; color: #333; }
              @media print { body { margin: 0; } }
            </style></head><body>
            <h1>{{E(titulo)}} {{E(numero)}}</h1>
            """);
        if (f.Estado == "BORRADOR") sb.Append("<p class=\"borrador\">BORRADOR: factura pendiente de contabilizar.</p>");
        sb.Append($"<div>Fecha de expedición: {F(f.FechaExpedicion)}");
        if (f.FechaOperacion is { } fo && fo != f.FechaExpedicion) sb.Append($" · Fecha de la operación: {F(fo)}");
        sb.Append("</div>");
        if (f.Rectificada is not null)
            sb.Append($"<div>Rectifica la factura {E(f.Rectificada)}{(f.FechaRectificada is { } fr ? " de " + F(fr) : "")}.</div>");

        sb.Append("<div class=\"cab\">");
        Parte(sb, "Emisor", f.Emisor);
        Parte(sb, "Destinatario", f.Destinatario);
        sb.Append("</div>");
        if (!string.IsNullOrWhiteSpace(f.Descripcion)) sb.Append($"<p><strong>Concepto:</strong> {E(f.Descripcion)}</p>");

        var recargo = f.Lineas.Any(l => l.CuotaRecargo != 0);
        sb.Append("<table><thead><tr><th>Impuesto</th><th class=\"num\">Base imponible</th><th class=\"num\">Tipo</th><th class=\"num\">Cuota</th>");
        if (recargo) sb.Append("<th class=\"num\">Recargo eq.</th><th class=\"num\">Cuota recargo</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var l in f.Lineas)
        {
            sb.Append($"<tr><td>{E(l.Impuesto)}{(l.TipoOperacion == "INTERIOR" ? "" : " · " + E(Operacion(l.TipoOperacion)))}</td>")
              .Append($"<td class=\"num\">{N(l.Base)}</td><td class=\"num\">{P(l.Porcentaje)}</td><td class=\"num\">{N(l.Cuota)}</td>");
            if (recargo) sb.Append($"<td class=\"num\">{P(l.PorcentajeRecargo)}</td><td class=\"num\">{N(l.CuotaRecargo)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<table class=\"totales\"><tbody>");
        sb.Append($"<tr><td>Base imponible</td><td class=\"num\">{N(f.BaseTotal)}</td></tr>");
        sb.Append($"<tr><td>Cuota</td><td class=\"num\">{N(f.CuotaTotal)}</td></tr>");
        if (f.RecargoTotal != 0) sb.Append($"<tr><td>Recargo de equivalencia</td><td class=\"num\">{N(f.RecargoTotal)}</td></tr>");
        foreach (var r in f.Retenciones)
            sb.Append($"<tr><td>Retención IRPF {P(r.Porcentaje)} s/ {N(r.Base)}</td><td class=\"num\">−{N(r.Importe)}</td></tr>");
        sb.Append($"<tr class=\"total\"><td>Total factura</td><td class=\"num\">{N(f.Total)} €</td></tr>");
        sb.Append("</tbody></table>");

        var pago = new List<string>();
        if (f.FormaPago is not null) pago.Add(E(f.FormaPago));
        if (f.Iban is not null) pago.Add("IBAN " + E(f.Iban));
        if (f.Vencimientos.Count > 0)
            pago.Add("Vencimientos: " + string.Join(", ", f.Vencimientos.Select(v => $"{F(v.Fecha)} ({N(v.Importe)} €)")));
        if (pago.Count > 0) sb.Append("<p><strong>Pago:</strong> ").Append(string.Join(" · ", pago)).Append("</p>");
        var menciones = Menciones(f);
        if (menciones.Count > 0)
            sb.Append("<div class=\"menciones\">").Append(string.Concat(menciones.Select(x => $"<div>{E(x)}</div>"))).Append("</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Operacion(string t) => t switch
    {
        "EXENTA" => "exenta",
        "NO_SUJETA" => "no sujeta",
        "INTRACOM_ENTREGA" => "entrega intracomunitaria",
        "EXPORTACION" => "exportación",
        "INVERSION_SUJETO_PASIVO" => "inversión del sujeto pasivo",
        _ => t.ToLowerInvariant().Replace('_', ' '),
    };

    private static void Parte(StringBuilder sb, string titulo, ParteImpreso p)
    {
        sb.Append($"<div class=\"parte\"><h2>{titulo}</h2><div><strong>{E(p.Nombre)}</strong></div><div>NIF: {E(p.Nif)}</div>");
        if (!string.IsNullOrWhiteSpace(p.Direccion)) sb.Append($"<div>{E(p.Direccion)}</div>");
        var linea = string.Join(" ", new[] { p.CodigoPostal, p.Poblacion }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(p.Provincia)) linea += (linea.Length > 0 ? " (" + p.Provincia + ")" : p.Provincia);
        if (linea.Length > 0) sb.Append($"<div>{E(linea)}</div>");
        if (!string.IsNullOrWhiteSpace(p.Pais) && p.Pais != "ES") sb.Append($"<div>{E(p.Pais)}</div>");
        sb.Append("</div>");
    }
}
