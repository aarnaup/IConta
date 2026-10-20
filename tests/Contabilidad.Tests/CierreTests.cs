using Contabilidad.Domain.Cierre;

namespace Contabilidad.Tests;

public class GeneradorCierreTests
{
    private const long C1 = 1, C2 = 2;          // canales base
    private const long R129 = 129;

    private static SaldoCuenta S(long id, string codigo, decimal saldo, long canal = C1) => new(id, codigo, canal, saldo);

    private static void Cuadra(IEnumerable<ApunteGenerado> apuntes)
    {
        foreach (var g in apuntes.GroupBy(a => a.CanalId))
            Assert.Equal(g.Sum(a => a.Debe), g.Sum(a => a.Haber));
    }

    [Fact]
    public void Saldar_abona_un_saldo_deudor_y_carga_uno_acreedor()
    {
        Assert.Equal(new ApunteGenerado(5, C1, 0, 100m), GeneradorCierre.Saldar(S(5, "57200000", 100m)));
        Assert.Equal(new ApunteGenerado(6, C1, 40m, 0), GeneradorCierre.Saldar(S(6, "40000001", -40m)));
    }

    [Fact]
    public void Regularizacion_con_beneficio_abona_la_129()
    {
        var saldos = new[] { S(60, "60000000", 400m), S(62, "62900000", 100m), S(70, "70000000", -1000m), S(57, "57200000", 500m) };
        var (apuntes, resultados) = GeneradorCierre.Regularizacion(saldos, R129);

        Assert.DoesNotContain(apuntes, a => a.CuentaId == 57);                    // solo 6 y 7
        Assert.Contains(new ApunteGenerado(60, C1, 0, 400m), apuntes);
        Assert.Contains(new ApunteGenerado(70, C1, 1000m, 0), apuntes);
        Assert.Contains(new ApunteGenerado(R129, C1, 0, 500m), apuntes);
        var r = Assert.Single(resultados);
        Assert.Equal((1000m, 500m, 500m), (r.Ingresos, r.Gastos, r.Resultado));
        Cuadra(apuntes);
    }

    [Fact]
    public void Regularizacion_con_perdida_carga_la_129()
    {
        var (apuntes, resultados) = GeneradorCierre.Regularizacion([S(60, "60000000", 900m), S(70, "70000000", -300m)], R129);
        Assert.Contains(new ApunteGenerado(R129, C1, 600m, 0), apuntes);
        Assert.Equal(-600m, Assert.Single(resultados).Resultado);
        Cuadra(apuntes);
    }

    [Fact]
    public void Regularizacion_se_hace_canal_a_canal()
    {
        var saldos = new[]
        {
            S(60, "60000000", 100m, C1), S(70, "70000000", -300m, C1),
            S(60, "60000000", 500m, C2), S(70, "70000000", -200m, C2)
        };
        var (apuntes, resultados) = GeneradorCierre.Regularizacion(saldos, R129);
        Assert.Equal([200m, -300m], resultados.Select(r => r.Resultado));
        Assert.Contains(new ApunteGenerado(R129, C1, 0, 200m), apuntes);
        Assert.Contains(new ApunteGenerado(R129, C2, 300m, 0), apuntes);
        Cuadra(apuntes);
    }

    [Fact]
    public void Ingresos_y_gastos_iguales_no_mueven_la_129()
    {
        var (apuntes, _) = GeneradorCierre.Regularizacion([S(60, "60000000", 50m), S(70, "70000000", -50m)], R129);
        Assert.Equal(2, apuntes.Count);
        Assert.DoesNotContain(apuntes, a => a.CuentaId == R129);
    }

    [Fact]
    public void Sin_cuentas_de_resultados_no_hay_regularizacion()
    {
        var (apuntes, resultados) = GeneradorCierre.Regularizacion([S(57, "57200000", 10m), S(10, "10000000", -10m)], R129);
        Assert.Empty(apuntes);
        Assert.Empty(resultados);
    }

    [Fact]
    public void Cierre_y_apertura_son_simetricos_y_cuadran()
    {
        var saldos = new[] { S(57, "57200000", 1500m), S(43, "43000001", 300m), S(10, "10000000", -1000m), S(129, "12900000", -800m), S(0, "47500000", 0m) };
        var cierre = GeneradorCierre.Cierre(saldos);
        var apertura = GeneradorCierre.Apertura(saldos);

        Assert.Equal(4, cierre.Count);                                             // el saldo cero no genera apunte
        Cuadra(cierre);
        Cuadra(apertura);
        foreach (var c in cierre)
            Assert.Contains(new ApunteGenerado(c.CuentaId, c.CanalId, c.Haber, c.Debe), apertura);
        Assert.Contains(new ApunteGenerado(57, C1, 0, 1500m), cierre);
        Assert.Contains(new ApunteGenerado(57, C1, 1500m, 0), apertura);
    }

    [Theory]
    [InlineData("60000000", true)]
    [InlineData("70500000", true)]
    [InlineData("12900000", false)]
    [InlineData("80000000", false)]
    public void Cuentas_de_resultados_son_las_de_los_grupos_6_y_7(string codigo, bool esperado)
        => Assert.Equal(esperado, GeneradorCierre.EsDeResultados(codigo));
}

public class GeneradorLiquidacionIvaTests
{
    private const long C1 = 1, C2 = 2;
    private const long Acreedora = 4750, Deudora = 4700;
    private static readonly Dictionary<long, decimal> SinSaldo = [];

    private static SaldoCuenta S(long id, string codigo, decimal saldo, long canal = C1) => new(id, codigo, canal, saldo);

    private static EntradaLiquidacionIva E(SaldoCuenta[] sop, SaldoCuenta[] rep, Dictionary<long, decimal>? deudora = null, decimal comp = 0)
        => new(sop, rep, deudora ?? SinSaldo, comp, Acreedora, Deudora);

    private static void Cuadra(IEnumerable<ApunteGenerado> apuntes)
    {
        foreach (var g in apuntes.GroupBy(a => a.CanalId))
            Assert.Equal(g.Sum(a => a.Debe), g.Sum(a => a.Haber));
    }

    [Fact]
    public void A_ingresar_va_a_la_4750()
    {
        var (apuntes, canales) = GeneradorLiquidacionIva.Generar(E([S(472, "47200000", 28m)], [S(477, "47700000", -70m)]));
        Assert.Equal(
            [new ApunteGenerado(477, C1, 70m, 0), new ApunteGenerado(472, C1, 0, 28m), new ApunteGenerado(Acreedora, C1, 0, 42m)],
            apuntes);
        var c = Assert.Single(canales);
        Assert.Equal((70m, 28m, 42m, 42m, 0m), (c.Repercutido, c.Soportado, c.Resultado, c.AIngresar, c.AFavor));
    }

    [Fact]
    public void A_compensar_va_a_la_4700()
    {
        var (apuntes, canales) = GeneradorLiquidacionIva.Generar(E([S(472, "47200000", 300m)], [S(477, "47700000", -100m)]));
        Assert.Contains(new ApunteGenerado(Deudora, C1, 200m, 0), apuntes);
        Assert.DoesNotContain(apuntes, a => a.CuentaId == Acreedora);
        Assert.Equal(200m, Assert.Single(canales).AFavor);
        Cuadra(apuntes);
    }

    [Fact]
    public void La_compensacion_de_periodos_anteriores_sale_de_la_4700()
    {
        var (apuntes, canales) = GeneradorLiquidacionIva.Generar(
            E([S(472, "47200000", 100m)], [S(477, "47700000", -400m)], new() { [C1] = 120m }, comp: 120m));
        Assert.Contains(new ApunteGenerado(Deudora, C1, 0, 120m), apuntes);
        Assert.Contains(new ApunteGenerado(Acreedora, C1, 0, 180m), apuntes);
        Assert.Equal(180m, Assert.Single(canales).AIngresar);
        Cuadra(apuntes);
    }

    [Fact]
    public void La_compensacion_no_supera_el_resultado_ni_el_saldo_ni_lo_que_aplica_la_declaracion()
    {
        // Resultado 50, saldo 4700 de 80, la declaración aplica 50 → todo compensado, nada a la 4750
        var (a1, _) = GeneradorLiquidacionIva.Generar(E([], [S(477, "47700000", -50m)], new() { [C1] = 80m }, comp: 50m));
        Assert.Contains(new ApunteGenerado(Deudora, C1, 0, 50m), a1);
        Assert.DoesNotContain(a1, a => a.CuentaId == Acreedora);

        // La 4700 solo tiene 30 aunque la declaración aplique 100
        var (a2, c2) = GeneradorLiquidacionIva.Generar(E([], [S(477, "47700000", -200m)], new() { [C1] = 30m }, comp: 100m));
        Assert.Equal(30m, Assert.Single(c2).Compensado);
        Assert.Contains(new ApunteGenerado(Acreedora, C1, 0, 170m), a2);

        // Si la declaración no compensa, el saldo de la 4700 no se toca
        var (a3, _) = GeneradorLiquidacionIva.Generar(E([], [S(477, "47700000", -200m)], new() { [C1] = 30m }, comp: 0m));
        Assert.DoesNotContain(a3, a => a.CuentaId == Deudora);
    }

    [Fact]
    public void Varias_subcuentas_y_rectificativas_que_invierten_el_saldo()
    {
        var sop = new[] { S(1, "47200001", 50m), S(2, "47200002", -10m) };      // la 2: rectificativa recibida
        var rep = new[] { S(3, "47700001", -100m) };
        var (apuntes, canales) = GeneradorLiquidacionIva.Generar(E(sop, rep));
        Assert.Contains(new ApunteGenerado(2, C1, 10m, 0), apuntes);
        Assert.Equal(60m, Assert.Single(canales).Resultado);
        Cuadra(apuntes);
    }

    [Fact]
    public void Se_liquida_canal_a_canal()
    {
        var sop = new[] { S(472, "47200000", 10m, C1), S(472, "47200000", 90m, C2) };
        var rep = new[] { S(477, "47700000", -50m, C1), S(477, "47700000", -50m, C2) };
        var (apuntes, canales) = GeneradorLiquidacionIva.Generar(E(sop, rep));
        Assert.Equal([40m, -40m], canales.Select(c => c.Resultado));
        Assert.Contains(new ApunteGenerado(Acreedora, C1, 0, 40m), apuntes);
        Assert.Contains(new ApunteGenerado(Deudora, C2, 40m, 0), apuntes);
        Cuadra(apuntes);
    }

    [Fact]
    public void Resultado_cero_solo_salda_las_cuentas()
    {
        var (apuntes, _) = GeneradorLiquidacionIva.Generar(E([S(472, "47200000", 70m)], [S(477, "47700000", -70m)]));
        Assert.Equal(2, apuntes.Count);
        Cuadra(apuntes);
    }
}
