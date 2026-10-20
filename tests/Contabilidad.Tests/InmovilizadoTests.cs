using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Contabilidad.Domain.Inmovilizado;

namespace Contabilidad.Tests;

public class CalculadorAmortizacionTests
{
    // Ordenador de 1.200 € a 4 años desde el 1/1/2026: termina el 31/12/2029
    private static readonly FichaAmortizacion Pc = new(1200m, 0m, new DateOnly(2026, 1, 1), 48, "LINEAL");

    [Fact]
    public void Fin_de_la_vida_util()
        => Assert.Equal(new DateOnly(2029, 12, 31), Pc.Fin);

    [Fact]
    public void Acumulada_por_dias()
    {
        Assert.Equal(0m, CalculadorAmortizacion.AcumuladaA(Pc, new DateOnly(2025, 12, 31)));
        Assert.Equal(299.79m, CalculadorAmortizacion.AcumuladaA(Pc, new DateOnly(2026, 12, 31)));   // 365 de 1.461 días
        Assert.Equal(1200m, CalculadorAmortizacion.AcumuladaA(Pc, new DateOnly(2029, 12, 31)));
        Assert.Equal(1200m, CalculadorAmortizacion.AcumuladaA(Pc, new DateOnly(2035, 1, 1)));       // no pasa de la base
    }

    [Fact]
    public void El_valor_residual_no_se_amortiza()
    {
        var f = Pc with { ValorResidual = 200m };
        Assert.Equal(1000m, CalculadorAmortizacion.AcumuladaA(f, new DateOnly(2030, 6, 30)));
    }

    [Fact]
    public void Pendiente_descuenta_lo_ya_amortizado_y_nunca_es_negativo()
    {
        var hasta = new DateOnly(2026, 12, 31);
        Assert.Equal(299.79m, CalculadorAmortizacion.Pendiente(Pc, hasta, 0));
        Assert.Equal(99.79m, CalculadorAmortizacion.Pendiente(Pc, hasta, 200m));
        Assert.Equal(0m, CalculadorAmortizacion.Pendiente(Pc, hasta, 500m));
    }

    [Fact]
    public void Amortizar_mes_a_mes_suma_lo_mismo_que_de_una_vez()
    {
        decimal acumulado = 0;
        for (var m = new DateOnly(2026, 1, 31); m <= new DateOnly(2029, 12, 31); m = new DateOnly(m.Year, m.Month, 1).AddMonths(2).AddDays(-1))
            acumulado += CalculadorAmortizacion.Pendiente(Pc, m, acumulado);
        Assert.Equal(1200m, acumulado);
    }

    [Fact]
    public void El_cuadro_sigue_los_ejercicios_y_llega_exacto_a_la_base()
    {
        var f = new FichaAmortizacion(1000m, 0m, new DateOnly(2026, 9, 15), 36, "LINEAL");
        var natural = CalculadorAmortizacion.Cuadro(f, 1);
        Assert.Equal(4, natural.Count);                                       // 2026 (parcial), 2027, 2028, 2029 (parcial)
        Assert.Equal(new DateOnly(2026, 9, 15), natural[0].Desde);
        Assert.Equal(new DateOnly(2026, 12, 31), natural[0].Hasta);
        Assert.Equal(new DateOnly(2029, 9, 14), natural[^1].Hasta);
        Assert.Equal(1000m, natural.Sum(t => t.Cuota));
        Assert.Equal(0m, natural[^1].ValorNeto);

        var partido = CalculadorAmortizacion.Cuadro(f, 7);                    // ejercicios de julio a junio
        Assert.Equal(new DateOnly(2027, 6, 30), partido[0].Hasta);
        Assert.Equal(1000m, partido.Sum(t => t.Cuota));
    }

    [Fact]
    public void Lo_que_no_se_amortiza_no_tiene_cuadro()
    {
        var terreno = new FichaAmortizacion(50000m, 0m, new DateOnly(2026, 1, 1), null, "NO_AMORTIZA");
        Assert.False(terreno.Amortiza);
        Assert.Empty(CalculadorAmortizacion.Cuadro(terreno, 1));
        Assert.Equal(0m, CalculadorAmortizacion.AcumuladaA(terreno, new DateOnly(2030, 1, 1)));
    }
}

public class GeneradorBajaTests
{
    private static readonly CuentasInmovilizado Cuentas = new(217, 2817, 681);
    private const long Cobro = 572, Perdidas = 671, Beneficios = 771, Canal = 1;

    private static void Cuadra(IEnumerable<ApunteGenerado> a) => Assert.Equal(a.Sum(x => x.Debe), a.Sum(x => x.Haber));

    [Fact]
    public void Baja_sin_venta_lleva_el_valor_neto_a_perdidas()
    {
        var (apuntes, r) = GeneradorBaja.Generar(1200m, 800m, 0m, 0m, Cuentas, null, Perdidas, Beneficios, Canal);
        Assert.Equal(-400m, r.Resultado);
        Assert.Equal([new ApunteGenerado(2817, Canal, 800m, 0), new ApunteGenerado(Perdidas, Canal, 400m, 0), new ApunteGenerado(217, Canal, 0, 1200m)],
            apuntes);
    }

    [Fact]
    public void Venta_con_beneficio_incluye_la_amortizacion_hasta_la_fecha()
    {
        var (apuntes, r) = GeneradorBaja.Generar(1200m, 800m, 50m, 500m, Cuentas, Cobro, Perdidas, Beneficios, Canal);
        Assert.Equal((850m, 350m, 150m), (r.AmortizacionAcumulada, r.ValorNeto, r.Resultado));
        Assert.Contains(new ApunteGenerado(681, Canal, 50m, 0), apuntes);
        Assert.Contains(new ApunteGenerado(2817, Canal, 0, 50m), apuntes);
        Assert.Contains(new ApunteGenerado(2817, Canal, 850m, 0), apuntes);
        Assert.Contains(new ApunteGenerado(Cobro, Canal, 500m, 0), apuntes);
        Assert.Contains(new ApunteGenerado(Beneficios, Canal, 0, 150m), apuntes);
        Cuadra(apuntes);
    }

    [Fact]
    public void Venta_por_el_valor_neto_no_da_resultado()
    {
        var (apuntes, r) = GeneradorBaja.Generar(1200m, 800m, 0m, 400m, Cuentas, Cobro, Perdidas, Beneficios, Canal);
        Assert.Equal(0m, r.Resultado);
        Assert.DoesNotContain(apuntes, a => a.CuentaId is Perdidas or Beneficios);
        Cuadra(apuntes);
    }

    [Fact]
    public void Venta_sin_cuenta_de_cobro_no_se_admite()
        => Assert.Throws<ReglaContableException>(() => GeneradorBaja.Generar(1200m, 0m, 0m, 100m, Cuentas, null, Perdidas, Beneficios, Canal));

    [Fact]
    public void Un_terreno_sin_amortizacion_se_vende_sin_tocar_las_28()
    {
        var (apuntes, r) = GeneradorBaja.Generar(50000m, 0m, 0m, 60000m, new CuentasInmovilizado(210, null, null), Cobro, Perdidas, Beneficios, Canal);
        Assert.Equal(10000m, r.Resultado);
        Assert.Equal(3, apuntes.Count);
        Cuadra(apuntes);
    }
}
