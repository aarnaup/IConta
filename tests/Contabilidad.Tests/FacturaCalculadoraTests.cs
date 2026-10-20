using Contabilidad.Domain;
using Contabilidad.Domain.Facturacion;

namespace Contabilidad.Tests;

public class FacturaCalculadoraTests
{
    private static LineaIvaEntrada L(decimal b, decimal pct, string regla = "TOTAL", decimal rec = 0, decimal? afect = null, long tipo = 1)
        => new(tipo, 1, "INTERIOR", b, pct, rec, regla, afect, false);

    [Fact]
    public void Emitida_con_IGIC_7()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(1000m, 7m)], null, null);
        Assert.Equal(70m, f.Totales.Cuota);
        Assert.Equal(1070m, f.Totales.Total);
        Assert.Equal(0m, f.Totales.CuotaDeducible);   // solo aplica a recibidas
    }

    [Fact]
    public void Emitida_con_retencion_irpf_resta_del_total()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(1000m, 7m)], null, new RetencionEntrada(1, 1, 15m));
        Assert.Equal(150m, f.Totales.Retencion);
        Assert.Equal(920m, f.Totales.Total);
    }

    [Fact]
    public void Recibida_con_prorrata_80_separa_deducible_y_no_deducible()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Recibidas, [L(1000m, 21m, "PRORRATA")], 80m, null);
        Assert.Equal(210m, f.Totales.Cuota);
        Assert.Equal(168m, f.Totales.CuotaDeducible);
        Assert.Equal(42m, f.Totales.CuotaNoDeducible);
        Assert.Equal(1210m, f.Totales.Total);
    }

    [Fact]
    public void Recibida_con_porcentaje_de_afectacion()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Recibidas, [L(1000m, 21m, "PORCENTAJE_AFECTACION", afect: 50m)], null, null);
        Assert.Equal(105m, f.Totales.CuotaDeducible);
    }

    [Fact]
    public void Recargo_de_equivalencia_se_suma_al_total()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(1000m, 21m, rec: 5.2m)], null, null);
        Assert.Equal(52m, f.Totales.Recargo);
        Assert.Equal(1262m, f.Totales.Total);
    }

    [Fact]
    public void Varios_tipos_se_suman()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(100m, 7m, tipo: 1), L(200m, 3m, tipo: 2)], null, null);
        Assert.Equal(300m, f.Totales.Base);
        Assert.Equal(13m, f.Totales.Cuota);   // 7 + 6
    }

    [Fact]
    public void Errores_de_regla()
    {
        Assert.Throws<ReglaContableException>(() => FacturaCalculadora.Calcular(LibroFactura.Emitidas, [], null, null));
        Assert.Throws<ReglaContableException>(() => FacturaCalculadora.Calcular(LibroFactura.Recibidas, [L(100m, 21m, "PRORRATA")], null, null));
        Assert.Throws<ReglaContableException>(() => FacturaCalculadora.Calcular(LibroFactura.Recibidas, [L(100m, 21m, "PORCENTAJE_AFECTACION", afect: 120m)], null, null));
        Assert.Throws<ReglaContableException>(() => FacturaCalculadora.Calcular(LibroFactura.Recibidas, [L(100m, 21m, "RARA")], null, null));
    }
}
