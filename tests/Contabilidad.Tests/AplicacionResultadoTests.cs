using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;

namespace Contabilidad.Tests;

public class GeneradorAplicacionResultadoTests
{
    private const long C1 = 1, C2 = 2;
    private static readonly CuentasAplicacion Cuentas = new(129, 112, 113, 120, 121, 526, 557);

    private static SaldosAplicacion S(decimal resultado, long canal = C1, decimal aCuenta = 0, decimal perdidas = 0)
        => new(canal, canal.ToString(), resultado, aCuenta, perdidas);
    private static LineaAplicacion L(string destino, string modo, decimal valor = 0, long? cuenta = null) => new(destino, modo, valor, cuenta);

    private static void Cuadra(IEnumerable<ApunteGenerado> apuntes)
    {
        foreach (var g in apuntes.GroupBy(a => a.CanalId)) Assert.Equal(g.Sum(a => a.Debe), g.Sum(a => a.Haber));
    }

    [Fact]
    public void Beneficio_a_reserva_legal_y_el_resto_a_voluntarias()
    {
        var (apuntes, canales) = GeneradorAplicacionResultado.Generar([S(2100m)],
            [L(DestinoResultado.ReservaLegal, "PORCENTAJE", 10), L(DestinoResultado.ReservaVoluntaria, "RESTO")], Cuentas);
        Assert.Equal(
            [new ApunteGenerado(129, C1, 2100m, 0), new ApunteGenerado(112, C1, 0, 210m), new ApunteGenerado(113, C1, 0, 1890m)],
            apuntes);
        Assert.Equal(2100m, Assert.Single(canales).Resultado);
    }

    [Fact]
    public void El_dividendo_cancela_primero_lo_repartido_a_cuenta()
    {
        var (apuntes, _) = GeneradorAplicacionResultado.Generar([S(1000m, aCuenta: 300m)],
            [L(DestinoResultado.Dividendos, "IMPORTE", 800), L(DestinoResultado.Remanente, "RESTO")], Cuentas);
        Assert.Contains(new ApunteGenerado(557, C1, 0, 300m), apuntes);
        Assert.Contains(new ApunteGenerado(526, C1, 0, 500m), apuntes);
        Assert.Contains(new ApunteGenerado(120, C1, 0, 200m), apuntes);
        Cuadra(apuntes);
    }

    [Fact]
    public void El_dividendo_acordado_no_puede_ser_menor_que_el_repartido_a_cuenta()
        => Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(1000m, aCuenta: 600m)],
            [L(DestinoResultado.Dividendos, "IMPORTE", 500), L(DestinoResultado.Remanente, "RESTO")], Cuentas));

    [Fact]
    public void La_perdida_se_lleva_a_resultados_negativos()
    {
        var (apuntes, _) = GeneradorAplicacionResultado.Generar([S(-700m)], [L(DestinoResultado.ResultadosNegativos, "RESTO")], Cuentas);
        Assert.Equal([new ApunteGenerado(129, C1, 0, 700m), new ApunteGenerado(121, C1, 700m, 0)], apuntes);
    }

    [Fact]
    public void Con_perdida_no_se_reparten_dividendos_ni_con_beneficio_se_lleva_a_negativos()
    {
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(-700m)], [L(DestinoResultado.Dividendos, "RESTO")], Cuentas));
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(700m)], [L(DestinoResultado.ResultadosNegativos, "RESTO")], Cuentas));
    }

    [Fact]
    public void Compensar_perdidas_abona_la_121_sin_pasarse_de_su_saldo()
    {
        var (apuntes, _) = GeneradorAplicacionResultado.Generar([S(1000m, perdidas: 400m)],
            [L(DestinoResultado.CompensarPerdidas, "IMPORTE", 400), L(DestinoResultado.ReservaVoluntaria, "RESTO")], Cuentas);
        Assert.Contains(new ApunteGenerado(121, C1, 0, 400m), apuntes);
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(1000m, perdidas: 100m)],
            [L(DestinoResultado.CompensarPerdidas, "IMPORTE", 400), L(DestinoResultado.ReservaVoluntaria, "RESTO")], Cuentas));
    }

    [Fact]
    public void Sin_resto_el_reparto_debe_coincidir_con_el_resultado()
    {
        var e = Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(1000m)],
            [L(DestinoResultado.ReservaVoluntaria, "IMPORTE", 900)], Cuentas));
        Assert.Contains("no coincide", e.Message);
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(1000m)],
            [L(DestinoResultado.ReservaLegal, "IMPORTE", 600), L(DestinoResultado.ReservaVoluntaria, "IMPORTE", 600)], Cuentas));
    }

    [Fact]
    public void Porcentajes_y_resto_se_aplican_en_cada_canal()
    {
        var (apuntes, canales) = GeneradorAplicacionResultado.Generar([S(2100m, C1), S(500m, C2), S(0m, 3)],
            [L(DestinoResultado.ReservaLegal, "PORCENTAJE", 10), L(DestinoResultado.Remanente, "RESTO")], Cuentas);
        Assert.Equal(2, canales.Count);                                   // el canal sin resultado no se toca
        Assert.Contains(new ApunteGenerado(112, C2, 0, 50m), apuntes);
        Assert.Contains(new ApunteGenerado(120, C2, 0, 450m), apuntes);
        Cuadra(apuntes);
    }

    [Fact]
    public void Sin_resultado_pendiente_no_hay_asiento()
        => Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(0m)], [L(DestinoResultado.Remanente, "RESTO")], Cuentas));

    [Fact]
    public void Validaciones_de_las_lineas()
    {
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(100m)], [], Cuentas));
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(100m)],
            [L(DestinoResultado.Remanente, "RESTO"), L(DestinoResultado.ReservaVoluntaria, "RESTO")], Cuentas));
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(100m)], [L(DestinoResultado.Cuenta, "RESTO")], Cuentas));
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(100m)], [L("OTRO", "RESTO")], Cuentas));
        Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(100m)], [L(DestinoResultado.Remanente, "PORCENTAJE", 120)], Cuentas));
    }

    [Fact]
    public void Falta_la_cuenta_de_un_destino_usado()
        => Assert.Throws<ReglaContableException>(() => GeneradorAplicacionResultado.Generar([S(100m)],
            [L(DestinoResultado.Remanente, "RESTO")], Cuentas with { Remanente = null }));

    [Theory]
    [InlineData(2100, 3000, 0, 210)]       // 10 % del beneficio
    [InlineData(2100, 3000, 500, 100)]     // solo hasta el 20 % del capital (600)
    [InlineData(2100, 3000, 600, 0)]       // ya está dotada
    [InlineData(-500, 3000, 0, 0)]         // con pérdidas no se dota
    [InlineData(2100, 0, 0, 0)]            // sin capital (empresario individual)
    public void Reserva_legal_minima(decimal beneficio, decimal capital, decimal actual, decimal esperada)
        => Assert.Equal(esperada, GeneradorAplicacionResultado.ReservaLegalMinima(beneficio, capital, actual));
}
