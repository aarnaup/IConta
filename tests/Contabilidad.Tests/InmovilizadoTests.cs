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

public class MetodosAmortizacionTests
{
    private static readonly DateOnly Ini = new(2026, 1, 1);

    [Fact]
    public void Coeficiente_de_tablas_es_lineal_al_porcentaje_anual()
    {
        var f = new Contabilidad.Domain.Inmovilizado.FichaAmortizacion(10000, 0, Ini, null, "COEFICIENTE_TABLA", 0, 20);
        Assert.Equal(new DateOnly(2030, 12, 30), f.Fin);                       // 1.825 días (20 % × días / 365): 2028 es bisiesto
        Assert.Equal(2000m, Contabilidad.Domain.Inmovilizado.CalculadorAmortizacion.AcumuladaA(f, new DateOnly(2026, 12, 31)));
        Assert.Equal(10000m, Contabilidad.Domain.Inmovilizado.CalculadorAmortizacion.AcumuladaA(f, new DateOnly(2031, 3, 1)));
    }

    [Fact]
    public void Degresivo_porcentaje_constante_y_resto_el_ultimo_anio()
    {
        // 10 años: lineal 10 % × 2,5 = 25 % sobre el valor pendiente
        var f = new Contabilidad.Domain.Inmovilizado.FichaAmortizacion(10000, 0, Ini, 120, "DEGRESIVO");
        Assert.Equal(25m, f.PorcentajeDegresivo);
        var A = (DateOnly d) => Contabilidad.Domain.Inmovilizado.CalculadorAmortizacion.AcumuladaA(f, d);
        Assert.Equal(2500m, A(new DateOnly(2026, 12, 31)));
        Assert.Equal(4375m, A(new DateOnly(2027, 12, 31)));                     // 2.500 + 25 % de 7.500
        Assert.Equal(10000m, A(new DateOnly(2035, 12, 31)));                    // el último año se amortiza lo que queda
        Assert.True(A(new DateOnly(2035, 6, 30)) < 10000m);
        Assert.Equal(1.5m * 100m / 4m, new Contabilidad.Domain.Inmovilizado.FichaAmortizacion(1, 0, Ini, 48, "DEGRESIVO").PorcentajeDegresivo);
        Assert.Equal(2m * 100m / 6m, new Contabilidad.Domain.Inmovilizado.FichaAmortizacion(1, 0, Ini, 72, "DEGRESIVO").PorcentajeDegresivo, 3);
    }

    [Fact]
    public void Libertad_de_amortizacion_todo_el_primer_dia()
    {
        var f = new Contabilidad.Domain.Inmovilizado.FichaAmortizacion(3000, 0, Ini, null, "LIBRE");
        Assert.Equal(3000m, Contabilidad.Domain.Inmovilizado.CalculadorAmortizacion.AcumuladaA(f, Ini));
    }

    [Fact]
    public void Suma_de_digitos_decreciente_y_creciente()
    {
        // 4 años: S = 10; decreciente 4/10, 3/10, 2/10, 1/10; creciente al revés
        var dec = new Contabilidad.Domain.Inmovilizado.FichaAmortizacion(10000, 0, Ini, 48, "DIGITOS_DECRECIENTE");
        var cre = dec with { Metodo = "DIGITOS_CRECIENTE" };
        decimal A(Contabilidad.Domain.Inmovilizado.FichaAmortizacion f, DateOnly d) => Contabilidad.Domain.Inmovilizado.CalculadorAmortizacion.AcumuladaA(f, d);
        Assert.Equal(new DateOnly(2029, 12, 31), dec.Fin);
        Assert.Equal([4000m, 7000m, 9000m, 10000m], new[] { 2026, 2027, 2028, 2029 }.Select(y => A(dec, new DateOnly(y, 12, 31))));
        Assert.Equal([1000m, 3000m, 6000m, 10000m], new[] { 2026, 2027, 2028, 2029 }.Select(y => A(cre, new DateOnly(y, 12, 31))));
        Assert.Equal(4000m + Math.Round(3000m * 181 / 365, 2), A(dec, new DateOnly(2027, 6, 30)));      // año en curso, por días
        var cuadro = Contabilidad.Domain.Inmovilizado.CalculadorAmortizacion.Cuadro(dec, 1);
        Assert.Equal([4000m, 3000m, 2000m, 1000m], cuadro.Select(t => t.Cuota));
        Assert.False((dec with { VidaUtilMeses = 30 }).Amortiza);                                         // solo años completos
    }
}
