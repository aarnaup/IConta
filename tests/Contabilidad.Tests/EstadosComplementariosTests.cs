using Contabilidad.Domain.Balances;

namespace Contabilidad.Tests;

public class EstadosComplementariosTests
{
    // Ejercicio de ejemplo: capital 10.000 en caja; venta cobrada; compra de maquinaria; amortización; préstamo; intereses; subvención (PYMES)
    private static readonly MovimientoOrigen[] Movs =
    [
        new("43000000", "FACTURA", 1210, 1210), new("70000000", "FACTURA", 0, 1000), new("47700000", "FACTURA", 0, 210),
        new("57200000", "TESORERIA", 1210 + 3000 + 2000, 5000 + 100),
        new("21300000", "MANUAL", 5000, 0), new("68100000", "AMORTIZACION", 500, 0), new("28130000", "AMORTIZACION", 0, 500),
        new("17000000", "MANUAL", 0, 3000), new("66200000", "MANUAL", 100, 0),
        new("13000000", "MANUAL", 200, 2000), new("74600000", "MANUAL", 0, 200),
    ];

    [Fact]
    public void Flujos_de_efectivo_cuadran_con_la_tesoreria()
    {
        var (l, avisos) = GeneradorEfe.Calcular(Movs, 10000);
        decimal V(string id) => l.Single(x => x.Id == id).Importe;
        Assert.Equal(600m, V("A1"));
        Assert.Equal(400m, V("A2"));             // amortización 500 − subvención 200 + gastos financieros 100
        Assert.Equal(210m, V("A3"));             // el IVA repercutido pendiente
        Assert.Equal(-100m, V("A4a"));
        Assert.Equal(1110m, V("A5"));
        Assert.Equal(-5000m, V("B8"));
        Assert.Equal(5000m, V("C12"));           // préstamo 3.000 + subvención recibida 2.000
        Assert.Equal(1110m, V("E"));
        Assert.Equal(11110m, V("EF"));
        Assert.Equal(0m, V("A4e"));
        Assert.DoesNotContain(avisos, a => a.Contains("no encajan"));
    }

    [Fact]
    public void Patrimonio_neto_ingresos_reconocidos_y_cambios()
    {
        var e = GeneradorEcpn.Calcular(new Dictionary<string, decimal> { ["57200000"] = 10000, ["10000000"] = -10000 }, Movs, 600);
        decimal A(string id) => e.ParteA.Single(x => x.Id == id).Importe;
        Assert.Equal((600m, 2000m, -200m, 2400m), (A("A"), A("B"), A("C"), A("T")));
        var fin = e.ParteB.Single(f => f.Id == "FIN").Importes;
        Assert.Equal((10000m, 600m, 1800m, 12400m), (fin["CAP"], fin["RDO"], fin["SUBV"], fin["TOTAL"]));
        Assert.Equal(10000m, e.ParteB.Single(f => f.Id == "INI").Importes["TOTAL"]);
    }

    [Fact]
    public void Aplicacion_del_resultado_con_dividendos()
    {
        MovimientoOrigen[] m = [new("12900000", "APLICACION", 1000, 0), new("11200000", "APLICACION", 0, 100), new("11300000", "APLICACION", 0, 600),
                                new("52600000", "APLICACION", 0, 300), new("52600000", "TESORERIA", 300, 0), new("57200000", "TESORERIA", 0, 300)];
        var e = GeneradorEcpn.Calcular(new Dictionary<string, decimal> { ["12900000"] = -1000, ["10000000"] = -3000, ["57200000"] = 4000 }, m, 0);
        var div = e.ParteB.Single(f => f.Id == "II4").Importes; var otras = e.ParteB.Single(f => f.Id == "III").Importes;
        Assert.Equal(-300m, div["RDO"]);
        Assert.Equal((700m, -700m), (otras["RES"], otras["RDO"]));
        Assert.Equal(3700m, e.ParteB.Single(f => f.Id == "FIN").Importes["TOTAL"]);
        var (l, _) = GeneradorEfe.Calcular(m, 4000);
        Assert.Equal(-300m, l.Single(x => x.Id == "C11").Importe);
        Assert.Equal(0m, l.Single(x => x.Id == "A4e").Importe);
    }

    [Fact]
    public void Modelo_normal_de_balance_cuadra_y_desglosa()
    {
        var saldos = new[] { new SaldoBalance("57200000", "Banco", 11110), new SaldoBalance("10000000", "Capital", -10000), new SaldoBalance("21300000", "Maquinaria", 5000),
            new SaldoBalance("28130000", "AA maquinaria", -500), new SaldoBalance("17000000", "Préstamo", -3000), new SaldoBalance("47700000", "IVA", -210),
            new SaldoBalance("13000000", "Subvención", -1800), new SaldoBalance("70000000", "Ventas", -1000), new SaldoBalance("68100000", "Amortización", 500),
            new SaldoBalance("66200000", "Intereses", 100), new SaldoBalance("74600000", "Subv.", -200) };
        var b = GeneradorEstados.Calcular(ModelosNormales.Balance, saldos);
        Assert.Equal(b["TA"], b["TP"]);
        Assert.Equal(4500m, b["AII2"]);
        Assert.Equal(210m, b["PCV6"]);
        Assert.Empty(b.SinClasificar);
        var p = GeneradorEstados.Calcular(ModelosNormales.PerdidasGanancias, saldos);
        Assert.Equal((1000m, 600m), (p["1b"] + p["1a"], p["RE"]));
    }
}
