using Contabilidad.Domain;
using Contabilidad.Domain.Facturacion;

namespace Contabilidad.Tests;

public class GeneradorAsientoFacturaTests
{
    // Cuentas: 430 cliente, 400 proveedor, 477 IVA rep., 472 IVA sop., 700 ventas, 600 compras, 473 ret. soportada, 475 ret. practicada
    private static ResolutorCuentas Resolutor() => new(
    [
        new("IVA_REPERCUTIDO", null, null, 477), new("IVA_SOPORTADO", null, null, 472),
        new("VENTAS", null, null, 700), new("COMPRAS", null, null, 600),
        new("RETENCION_SOPORTADA", null, null, 473), new("RETENCION_PRACTICADA", null, null, 475),
    ], cuentaCliente: 430, cuentaProveedor: 400);

    private static readonly LineaPlantilla[] Venta =
    [
        new(1, null, "CLIENTE", "DEBE", "TOTAL", null, true, null, null),
        new(2, null, "RETENCION_SOPORTADA", "DEBE", "RETENCION", null, false, null, null),
        new(3, null, "VENTAS", "HABER", "BASE", null, false, null, null),
        new(4, null, "IVA_REPERCUTIDO", "HABER", "CUOTA", null, false, null, null),
        new(5, null, "IVA_REPERCUTIDO", "HABER", "RECARGO", null, false, null, null),
    ];

    private static readonly LineaPlantilla Compra0 = new(1, null, "PROVEEDOR", "HABER", "TOTAL", null, true, null, null);
    private static readonly LineaPlantilla[] Compra =
    [
        Compra0,
        new(2, null, "RETENCION_PRACTICADA", "HABER", "RETENCION", null, false, null, null),
        new(3, null, "COMPRAS", "DEBE", "BASE", null, false, null, null),
        new(4, null, "IVA_SOPORTADO", "DEBE", "CUOTA", null, false, null, null),
        new(5, null, "COMPRAS", "DEBE", "CUOTA_NO_DEDUCIBLE", null, false, null, null),
    ];

    private static LineaIvaEntrada L(decimal b, decimal pct, string regla = "TOTAL")
        => new(1, 1, "INTERIOR", b, pct, 0m, regla, null, false);

    private static IReadOnlyList<ApunteCmd> Gen(LibroFactura libro, FacturaCalculada f, LineaPlantilla[] p)
        => GeneradorAsientoFactura.Generar(libro, f, p, Resolutor(), terceroId: 9, actividadId: null, canalFactura: 5,
            "Cliente SL", "F-1", new DateOnly(2026, 10, 20), "Factura {factura} {tercero}");

    private static void Cuadra(IEnumerable<ApunteCmd> a)
        => Assert.Equal(a.Sum(x => x.Debe), a.Sum(x => x.Haber));

    [Fact]
    public void Venta_con_IGIC_y_retencion_cuadra_y_asigna_cuentas()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(1000m, 7m)], null, new RetencionEntrada(1, 1, 15m));
        var a = Gen(LibroFactura.Emitidas, f, Venta);

        Cuadra(a);
        Assert.Equal(920m, a.Single(x => x.CuentaId == 430).Debe);
        Assert.Equal(150m, a.Single(x => x.CuentaId == 473).Debe);
        Assert.Equal(1000m, a.Single(x => x.CuentaId == 700).Haber);
        Assert.Equal(70m, a.Single(x => x.CuentaId == 477).Haber);
        Assert.All(a, x => Assert.Equal(5, x.CanalId));
        Assert.Equal(9, a.Single(x => x.CuentaId == 430).TerceroId);
        Assert.Equal("Factura F-1 Cliente SL", a[0].Concepto);
    }

    [Fact]
    public void Compra_con_prorrata_lleva_la_cuota_no_deducible_al_gasto()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Recibidas, [L(1000m, 21m, "PRORRATA")], 80m, null);
        var a = Gen(LibroFactura.Recibidas, f, Compra);

        Cuadra(a);
        Assert.Equal(1210m, a.Single(x => x.CuentaId == 400).Haber);
        Assert.Equal(168m, a.Single(x => x.CuentaId == 472).Debe);
        Assert.Equal(1042m, a.Where(x => x.CuentaId == 600).Sum(x => x.Debe));   // 1000 base + 42 no deducible
    }

    [Fact]
    public void Rectificativa_negativa_invierte_debe_y_haber()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(-1000m, 7m)], null, null);
        var a = Gen(LibroFactura.Emitidas, f, Venta);

        Cuadra(a);
        Assert.Equal(1070m, a.Single(x => x.CuentaId == 430).Haber);
        Assert.Equal(1000m, a.Single(x => x.CuentaId == 700).Debe);
        Assert.Equal(70m, a.Single(x => x.CuentaId == 477).Debe);
    }

    [Fact]
    public void Una_linea_por_cada_tipo_de_impuesto()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas,
            [new LineaIvaEntrada(1, 1, "INTERIOR", 100m, 7m, 0m, "TOTAL", null, false),
             new LineaIvaEntrada(2, 1, "INTERIOR", 200m, 3m, 0m, "TOTAL", null, false)], null, null);
        var a = Gen(LibroFactura.Emitidas, f, Venta);

        Cuadra(a);
        Assert.Equal(2, a.Count(x => x.CuentaId == 477));
    }

    [Fact]
    public void El_canal_de_la_linea_de_plantilla_manda_sobre_el_de_la_factura()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Emitidas, [L(100m, 7m)], null, null);
        LineaPlantilla[] p = [new(1, null, "CLIENTE", "DEBE", "TOTAL", null, true, 42, null),
                              new(2, null, "VENTAS", "HABER", "EXPRESION", "TOTAL", false, null, null)];
        var a = Gen(LibroFactura.Emitidas, f, p);
        Assert.Equal(42, a[0].CanalId);
        Assert.Equal(5, a[1].CanalId);
    }

    [Fact]
    public void Adquisicion_intracomunitaria_se_autorrepercute()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Recibidas,
            [new LineaIvaEntrada(1, 1, "INTRACOM_ADQUISICION", 1000m, 21m, 0m, "PRORRATA", null, false)], 80m, null);
        Assert.Equal((210m, 168m, 210m, 1000m), (f.Totales.Cuota, f.Totales.CuotaDeducible, f.Totales.CuotaAutorrepercutida, f.Totales.Total));
        var a = Gen(LibroFactura.Recibidas, f, Compra);
        Cuadra(a);
        Assert.Equal(1000m, a.Single(x => x.CuentaId == 400).Haber);       // al proveedor solo la base
        Assert.Equal(168m, a.Single(x => x.CuentaId == 472).Debe);
        Assert.Equal(210m, a.Single(x => x.CuentaId == 477).Haber);        // cuota devengada
        Assert.Equal(1042m, a.Where(x => x.CuentaId == 600).Sum(x => x.Debe));   // base + no deducible
    }

    [Fact]
    public void Importacion_lleva_la_cuota_de_aduana_y_no_se_autorrepercute()
    {
        var f = FacturaCalculadora.Calcular(LibroFactura.Recibidas,
            [new LineaIvaEntrada(1, 1, "IMPORTACION", 1000m, 21m, 0m, "TOTAL", null, false)], null, null);
        Assert.Equal((1210m, 0m), (f.Totales.Total, f.Totales.CuotaAutorrepercutida));
        var a = Gen(LibroFactura.Recibidas, f, Compra);
        Cuadra(a);
        Assert.DoesNotContain(a, x => x.CuentaId == 477);
    }

    [Theory]
    [InlineData(LibroFactura.Emitidas, "INTRACOM_ADQUISICION", "IVA")]
    [InlineData(LibroFactura.Recibidas, "EXPORTACION", "IVA")]
    [InlineData(LibroFactura.Emitidas, "INTRACOM_ENTREGA", "IGIC")]
    public void Operaciones_no_validas_para_el_libro_o_el_impuesto(LibroFactura libro, string tipo, string impuesto)
        => Assert.Throws<ReglaContableException>(() => OperacionesIva.Validar(libro, tipo, impuesto));
}
