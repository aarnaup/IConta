using Contabilidad.Domain.Facturacion;

namespace Contabilidad.Tests;

public class ImpresoFacturaTests
{
    private static FacturaImpreso Factura() => new()
    {
        Serie = "A", Numero = "15", FechaExpedicion = new DateOnly(2026, 3, 10), FechaOperacion = new DateOnly(2026, 3, 1), Estado = "CONTABILIZADA",
        BaseTotal = 1200, CuotaTotal = 230, RecargoTotal = 0, RetencionTotal = 150, Total = 1280,
        Emisor = new ParteImpreso { Nombre = "Mi Empresa <S.L.>", Nif = "B12345678", Direccion = "Calle Mayor 1", CodigoPostal = "28001", Poblacion = "Madrid", Provincia = "Madrid", Pais = "ES" },
        Destinatario = new ParteImpreso { Nombre = "Cliente", Nif = "A11111111", Pais = "ES" },
        Lineas = [new() { Base = 1000, Porcentaje = 21, Cuota = 210 }, new() { Base = 200, Porcentaje = 10, Cuota = 20 }],
        Retenciones = [new() { Base = 1000, Porcentaje = 15, Importe = 150 }],
        Vencimientos = [new() { Fecha = new DateOnly(2026, 4, 10), Importe = 1280 }], FormaPago = "Transferencia", Iban = "ES9121000418450200051332",
    };

    [Fact]
    public void Lleva_los_datos_del_reglamento_y_escapa_el_html()
    {
        var html = ImpresoFactura.Html(Factura());
        Assert.Contains("<title>Factura A15</title>", html);
        Assert.Contains("Fecha de expedición: 10/03/2026 · Fecha de la operación: 01/03/2026", html);
        Assert.Contains("Mi Empresa &lt;S.L.&gt;", html);
        Assert.DoesNotContain("<S.L.>", html);
        Assert.Contains("28001 Madrid (Madrid)", html);
        Assert.Contains("1.000,00", html);
        Assert.Contains("Retención IRPF 15 % s/ 1.000,00", html);
        Assert.Contains("1.280,00 €", html);
        Assert.Contains("Transferencia · IBAN ES9121000418450200051332 · Vencimientos: 10/04/2026 (1.280,00 €)", html);
        Assert.DoesNotContain("Recargo eq.", html);
        Assert.DoesNotContain("BORRADOR", html);
        Assert.Empty(ImpresoFactura.Menciones(Factura()));
    }

    [Fact]
    public void Menciones_obligatorias_segun_las_operaciones()
    {
        var f = Factura();
        f.CriterioCaja = true;
        f.Lineas = [new() { TipoOperacion = "INTRACOM_ENTREGA", Base = 500 }, new() { TipoOperacion = "INVERSION_SUJETO_PASIVO", Base = 100 }];
        var m = ImpresoFactura.Menciones(f);
        Assert.Equal(3, m.Count);
        Assert.Contains("Régimen especial del criterio de caja.", m);
        Assert.Contains(m, x => x.StartsWith("Inversión del sujeto pasivo"));
        Assert.Contains(m, x => x.StartsWith("Entrega intracomunitaria exenta"));
    }

    [Fact]
    public void Rectificativa_borrador_y_recargo()
    {
        var f = Factura();
        f.TipoFactura = "RECTIFICATIVA"; f.Estado = "BORRADOR"; f.Rectificada = "A10"; f.FechaRectificada = new DateOnly(2026, 1, 5);
        f.Lineas = [new() { Base = -100, Porcentaje = 21, Cuota = -21, PorcentajeRecargo = 5.2m, CuotaRecargo = -5.2m }];
        f.RecargoTotal = -5.2m; f.FormaPago = null; f.Iban = null; f.Vencimientos = [];
        var html = ImpresoFactura.Html(f);
        Assert.Contains("Factura rectificativa A15", html);
        Assert.Contains("Rectifica la factura A10 de 05/01/2026.", html);
        Assert.Contains("BORRADOR", html);
        Assert.Contains("Recargo eq.", html);
        Assert.Contains("5,2 %", html);
        Assert.DoesNotContain("<strong>Pago:</strong>", html);
    }
}
