using ClosedXML.Excel;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Infrastructure.Fiscal;

/// <summary>Escribe el libro de cálculo con las hojas EXPEDIDAS y RECIBIDAS en la disposición de la AEAT.</summary>
public static class LibrosAeatXlsx
{
    public static byte[] Generar(string nif, string nombre, int anio, IReadOnlyList<object?[]> expedidas, IReadOnlyList<object?[]> recibidas)
    {
        using var wb = new XLWorkbook();
        Hoja(wb, "EXPEDIDAS", "LIBRO REGISTRO FACTURAS EXPEDIDAS", FormatoAeat.Expedidas, nif, nombre, anio, expedidas);
        Hoja(wb, "RECIBIDAS", "LIBRO REGISTRO FACTURAS RECIBIDAS", FormatoAeat.Recibidas, nif, nombre, anio, recibidas);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Hoja(XLWorkbook wb, string hoja, string titulo, IReadOnlyList<ColumnaAeat> cols, string nif, string nombre, int anio,
        IReadOnlyList<object?[]> filas)
    {
        var ws = wb.Worksheets.Add(hoja);
        var t = ws.Range(1, 2, 1, 17).Merge();
        t.Value = titulo;
        Estilo(t, false);
        ws.Cell(2, 1).Value = $"Ejercicio: {anio}";
        ws.Cell(3, 1).Value = $"NIF: {nif}";
        ws.Cell(4, 1).Value = $"NOMBRE/RAZÓN SOCIAL: {nombre}";

        const int g = FormatoAeat.FilaGrupos, s = FormatoAeat.FilaTitulos;
        for (var i = 0; i < cols.Count;)
        {
            var c = cols[i];
            var col = i + 1;
            if (c.Grupo is null)
            {
                var r = ws.Range(g, col, s, col).Merge();
                r.Value = c.Titulo;
                Estilo(r, true);
                i++;
                continue;
            }
            var fin = i;
            while (fin + 1 < cols.Count && cols[fin + 1].Grupo == c.Grupo) fin++;
            var grupo = ws.Range(g, col, g, fin + 1).Merge();
            grupo.Value = c.Grupo;
            Estilo(grupo, true);
            for (var k = i; k <= fin; k++)
            {
                ws.Cell(s, k + 1).Value = cols[k].Titulo;
                Estilo(ws.Cell(s, k + 1).AsRange(), false);
            }
            i = fin + 1;
        }

        var fila = FormatoAeat.PrimeraFilaDatos;
        foreach (var f in filas)
        {
            for (var i = 0; i < f.Length && i < cols.Count; i++)
            {
                var cell = ws.Cell(fila, i + 1);
                switch (f[i])
                {
                    case null: break;
                    case decimal d: cell.Value = d; break;
                    case string x: cell.SetValue(x); cell.Style.NumberFormat.Format = "@"; break;
                    default: cell.SetValue(f[i]!.ToString()); break;
                }
            }
            fila++;
        }
        for (var i = 0; i < cols.Count; i++)
        {
            var column = ws.Column(i + 1);
            column.Width = cols[i].Importe ? 14 : Math.Clamp(cols[i].Titulo.Length + 2, 10, 30);
            if (cols[i].Importe) column.Style.NumberFormat.Format = "0.00";
        }
        ws.Column(1).Width = 12;
        ws.SheetView.FreezeRows(s);
    }

    private static void Estilo(IXLRange r, bool fondo)
    {
        r.Style.Font.Bold = true;
        r.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        r.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        r.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        r.Style.Alignment.WrapText = true;
        if (fondo) r.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
    }
}
