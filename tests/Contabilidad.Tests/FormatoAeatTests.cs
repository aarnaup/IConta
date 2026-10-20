using ClosedXML.Excel;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Infrastructure.Fiscal;

namespace Contabilidad.Tests;

public class FormatoAeatTests
{
    private static LineaLibroDto L(decimal pct, decimal @base, decimal cuota, bool primera = true, string op = "INTERIOR", string pais = "ES",
        bool ue = false, string doc = "NIF", string tipo = "ORDINARIA", int? registro = null, bool inversion = false, decimal? retencion = null)
        => new()
        {
            FacturaId = 1, NumeroRegistro = registro, FechaExpedicion = new DateOnly(2026, 5, 10), FechaOperacion = new DateOnly(2026, 4, 30),
            FechaContable = new DateOnly(2026, 5, 12), Serie = "A", Numero = "0007", TipoFactura = tipo, Nif = "B12345678",
            Nombre = "Un nombre de cliente bastante largo que supera los cuarenta", TipoOperacion = op, PaisIso2 = pais, PaisUe = ue,
            TipoDocumento = doc, Base = @base, Porcentaje = pct, Cuota = cuota, CuotaDeducible = cuota, PrimeraLinea = primera,
            TotalFactura = primera ? 1331 : null, Retencion = primera ? retencion : null, BienInversion = inversion
        };

    private static object? Col(IReadOnlyList<ColumnaAeat> cols, object?[] fila, string grupo, string titulo)
        => fila[cols.Select((c, i) => (c, i)).Single(x => x.c.Grupo == grupo && x.c.Titulo == titulo).i];
    private static object? Col(IReadOnlyList<ColumnaAeat> cols, object?[] fila, string titulo)
        => fila[cols.Select((c, i) => (c, i)).Single(x => x.c.Grupo is null && x.c.Titulo == titulo).i];

    [Fact]
    public void Columnas_del_modelo_de_la_AEAT()
    {
        Assert.Equal(32, FormatoAeat.Expedidas.Count);   // A..AF
        Assert.Equal(39, FormatoAeat.Recibidas.Count);   // A..AM
        Assert.Equal("Importe Retenido del IRPF", FormatoAeat.Expedidas[^1].Titulo);
        Assert.Equal("Registro Acuerdo Facturación", FormatoAeat.Recibidas[^1].Titulo);
        Assert.Equal("Cuota IVA Repercutida", FormatoAeat.Expedidas[23].Titulo);                // X
        Assert.Equal("Cuota Deducible", FormatoAeat.Recibidas[29].Titulo);                      // AD
    }

    [Fact]
    public void Expedidas_interior_con_dos_tipos()
    {
        var cols = FormatoAeat.Expedidas;
        var f = FormatoAeat.Filas("EMITIDAS", 2026, "2T", [L(21, 1000, 210, retencion: 150), L(10, 100, 10, primera: false)]);
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.Equal(cols.Count, x.Length));
        Assert.Equal("2026", f[0][0]);
        Assert.Equal("2T", f[0][1]);
        Assert.Equal("F1", Col(cols, f[0], "Tipo de factura"));
        Assert.Equal("10/05/2026", Col(cols, f[0], "Fecha Expedición"));
        Assert.Equal("30/04/2026", Col(cols, f[0], "Fecha Operación"));
        Assert.Equal("A", Col(cols, f[0], "Identificación de la Factura", "Serie"));
        Assert.Equal("0007", Col(cols, f[0], "Identificación de la Factura", "Número"));
        Assert.Null(Col(cols, f[0], "NIF Destinatario", "Tipo"));        // español: solo el NIF
        Assert.Null(Col(cols, f[0], "NIF Destinatario", "Código País"));
        Assert.Equal("B12345678", Col(cols, f[0], "NIF Destinatario", "Identificación"));
        Assert.Equal(40, ((string)Col(cols, f[0], "Nombre Destinatario")!).Length);
        Assert.Equal("S1", Col(cols, f[0], "Calificación de la operación"));
        Assert.Equal(1331m, Col(cols, f[0], "Total Factura"));
        Assert.Null(Col(cols, f[1], "Total Factura"));                   // el total solo en la primera línea
        Assert.Equal(210m, Col(cols, f[0], "Cuota IVA Repercutida"));
        Assert.Equal(10m, Col(cols, f[1], "Tipo de IVA"));
        Assert.Null(Col(cols, f[0], "Tipo de Recargo Eq."));
        Assert.Equal(150m, Col(cols, f[0], "Importe Retenido del IRPF"));
        Assert.Null(Col(cols, f[1], "Importe Retenido del IRPF"));
    }

    [Theory]
    [InlineData("EXENTA", null, "E1")]
    [InlineData("EXPORTACION", null, "E2")]
    [InlineData("INTRACOM_ENTREGA", null, "E5")]
    [InlineData("INVERSION_SUJETO_PASIVO", "S2", null)]
    [InlineData("NO_SUJETA", "N1", null)]
    [InlineData("INTERIOR", "S1", null)]
    public void Calificacion_y_exencion(string op, string? calificacion, string? exencion)
    {
        Assert.Equal(calificacion, FormatoAeat.Calificacion(op));
        Assert.Equal(exencion, FormatoAeat.Exencion(op));
    }

    [Fact]
    public void Destinatarios_extranjeros_y_tipos_de_factura()
    {
        Assert.Equal(("02", "FR", "B12345678"), FormatoAeat.Identificacion(L(0, 100, 0, pais: "FR", ue: true)));
        Assert.Equal(("02", "FR", "B12345678"), FormatoAeat.Identificacion(L(0, 100, 0, pais: "FR", doc: "VIES")));
        Assert.Equal(("03", "US", "B12345678"), FormatoAeat.Identificacion(L(0, 100, 0, pais: "US", doc: "PASAPORTE")));
        Assert.Equal(("04", "US", "B12345678"), FormatoAeat.Identificacion(L(0, 100, 0, pais: "US")));
        Assert.Equal("F2", FormatoAeat.TipoFactura("SIMPLIFICADA", true));
        Assert.Equal("R1", FormatoAeat.TipoFactura("RECTIFICATIVA", false));
        Assert.Equal("F5", FormatoAeat.TipoFactura("IMPORTACION", false));
        Assert.Equal("F1", FormatoAeat.TipoFactura("IMPORTACION", true));
        Assert.Equal("05", FormatoAeat.Periodo("05", new DateOnly(2026, 5, 3)));
        Assert.Equal("4T", FormatoAeat.Periodo("0A", new DateOnly(2026, 11, 3)));
        Assert.Equal("1T", FormatoAeat.Periodo("1T", new DateOnly(2026, 2, 3)));
    }

    [Fact]
    public void Recibidas_con_registro_bien_de_inversion_e_inversion_del_sujeto_pasivo()
    {
        var cols = FormatoAeat.Recibidas;
        var f = FormatoAeat.Filas("RECIBIDAS", 2026, "05",
            [L(21, 1000, 210, registro: 14, inversion: true), L(21, 500, 105, op: "INVERSION_SUJETO_PASIVO", registro: 15)]);
        Assert.All(f, x => Assert.Equal(cols.Count, x.Length));
        Assert.Equal("05", f[0][1]);
        Assert.Equal("A0007", Col(cols, f[0], "Identificación Factura del Expedidor", "(Serie-Número)"));
        Assert.Equal("12/05/2026", Col(cols, f[0], "Fecha Recepción"));
        Assert.Equal("14", Col(cols, f[0], "Número Recepción"));
        Assert.Equal("S", Col(cols, f[0], "Bien de Inversión"));
        Assert.Equal("N", Col(cols, f[0], "Inversión del Sujeto Pasivo"));
        Assert.Equal("S", Col(cols, f[1], "Inversión del Sujeto Pasivo"));
        Assert.Equal("N", Col(cols, f[0], "Deducible en Periodo Posterior"));
        Assert.Equal("2026", Col(cols, f[0], "Periodo Deducción", "Ejercicio"));
        Assert.Equal("05", Col(cols, f[0], "Periodo Deducción", "Periodo"));
        Assert.Equal(210m, Col(cols, f[0], "Cuota IVA Soportado"));
        Assert.Equal(210m, Col(cols, f[0], "Cuota Deducible"));
    }

    [Fact]
    public void Xlsx_con_las_dos_hojas_y_las_cabeceras_en_su_sitio()
    {
        var exp = FormatoAeat.Filas("EMITIDAS", 2026, "2T", [L(21, 1000, 210)]);
        var rec = FormatoAeat.Filas("RECIBIDAS", 2026, "2T", [L(21, 1000, 210, registro: 3)]);
        using var ms = new MemoryStream(LibrosAeatXlsx.Generar("B99999999", "Demo S.L.", 2026, exp, rec));
        using var wb = new XLWorkbook(ms);
        Assert.Equal(["EXPEDIDAS", "RECIBIDAS"], wb.Worksheets.Select(w => w.Name));

        var e = wb.Worksheet("EXPEDIDAS");
        Assert.Equal("LIBRO REGISTRO FACTURAS EXPEDIDAS", e.Cell("B1").GetString());
        Assert.Equal("NIF: B99999999", e.Cell("A3").GetString());
        Assert.Equal("Autoliquidación", e.Cell("A6").GetString());
        Assert.True(e.Cell("A6").IsMerged() && e.Cell("B6").IsMerged());
        Assert.Equal("Identificación de la Factura", e.Cell("K6").GetString());
        Assert.Equal("Número-Final", e.Cell("M7").GetString());
        Assert.Equal("Tipo de factura", e.Cell("F6").GetString());
        Assert.Equal("Cobro (Operación Criterio de Caja)", e.Cell("AA6").GetString());
        Assert.Equal("Importe Retenido del IRPF", e.Cell("AF6").GetString());
        Assert.Equal("2T", e.Cell("B8").GetString());
        Assert.Equal("10/05/2026", e.Cell("I8").GetString());
        Assert.Equal(1000m, (decimal)e.Cell("V8").GetDouble());
        Assert.Equal("S1", e.Cell("S8").GetString());

        var r = wb.Worksheet("RECIBIDAS");
        Assert.Equal("NIF Expedidor", r.Cell("P6").GetString());
        Assert.Equal("Periodo Deducción", r.Cell("X6").GetString());
        Assert.Equal("Pago (Operación Criterio de Caja)", r.Cell("AG6").GetString());
        Assert.Equal("Registro Acuerdo Facturación", r.Cell("AM6").GetString());
        Assert.Equal("3", r.Cell("N8").GetString());
        Assert.Equal(210m, (decimal)r.Cell("AD8").GetDouble());
    }
}
