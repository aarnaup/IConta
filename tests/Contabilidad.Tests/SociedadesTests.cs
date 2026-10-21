using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class SociedadesTests
{
    private static readonly DateOnly Ini = new(2026, 1, 1), Fin = new(2026, 12, 31);

    private static DeclaracionCalculada Calc(decimal resultado, TipoGravamenIs tipo, decimal bins = 0, IReadOnlyList<AjusteIs>? ajustes = null,
        decimal reserva = 0, decimal ret = 0, decimal pagos = 0, decimal deduc = 0, DateOnly? ini = null, DateOnly? fin = null)
    {
        var d = new DeclaracionCalculada { Modelo = "200" };
        CalculadorSociedades.Calcular200(new Entrada200(resultado, ajustes ?? [], reserva, bins, 0, 0, deduc, ret, pagos, tipo, ini ?? Ini, fin ?? Fin), d);
        return d;
    }

    [Fact]
    public void Tipo_general_con_ajustes_retenciones_y_pagos_fraccionados()
    {
        var d = Calc(100_000m, new("GENERAL"), ajustes: [new("Multas", 2_000m), new("Amortización fiscal", -12_000m)], ret: 1_000m, pagos: 5_000m);
        Assert.Equal(90_000m, d["BIP"]);
        Assert.Equal(22_500m, d["CI"]);
        Assert.Equal(16_500m, d["CD"]);
        Assert.Equal(16_500m, d.AIngresar);
    }

    [Fact]
    public void Compensa_bases_negativas_y_genera_las_nuevas()
    {
        var d = Calc(50_000m, new("GENERAL"), bins: 80_000m);
        Assert.Equal(50_000m, d["COMP"]);   // hasta 1 M€ sin límite porcentual
        Assert.Equal(0m, d["BI"]);
        var n = Calc(-30_000m, new("GENERAL"));
        Assert.Equal(-30_000m, n["BI"]);
        Assert.Equal(0m, n["CI"]);
        Assert.Equal(2_100_000m, CalculadorSociedades.LimiteBins(3_000_000m, 0));
        Assert.Equal(1_000_000m, CalculadorSociedades.LimiteBins(1_200_000m, 30_000_000m));
    }

    [Fact]
    public void Tipos_de_microempresa_por_tramos_y_proporcionales_en_ejercicio_corto()
    {
        Assert.Equal(50_000m * 0.19m + 50_000m * 0.21m, CalculadorSociedades.CuotaIntegra(100_000m, new("MICRO"), Ini, Fin));
        // Medio año: el primer tramo es 50.000 × 181/365
        var corto = CalculadorSociedades.CuotaIntegra(100_000m, new("MICRO"), Ini, new DateOnly(2026, 6, 30));
        Assert.Equal(Math.Round(24_794.52m * 0.19m + (100_000m - 24_794.52m) * 0.21m, 2), corto);
        Assert.Equal((23m, 23m), CalculadorSociedades.Tipos(new("REDUCIDA_DIMENSION"), 2026));
        Assert.Equal((15m, 15m), CalculadorSociedades.Tipos(new("NUEVA_CREACION"), 2026));
        Assert.Equal(10_000m, CalculadorSociedades.CuotaIntegra(50_000m, new("PERSONALIZADO", 20m), Ini, Fin));
        Assert.Throws<ReglaContableException>(() => CalculadorSociedades.Tipos(new("PERSONALIZADO"), 2026));
    }

    [Fact]
    public void Pagos_fraccionados_por_las_dos_modalidades()
    {
        var a = new DeclaracionCalculada();
        CalculadorSociedades.Calcular202(new Entrada202("ART_40_2", new("GENERAL"), 2026, 20_000m, 0, 0, 0, 0, 0), a);
        Assert.Equal(3_600m, a["RES"]);
        Assert.Equal(17m, CalculadorSociedades.Porcentaje403(new("GENERAL"), 2026));
        var b = new DeclaracionCalculada();
        CalculadorSociedades.Calcular202(new Entrada202("ART_40_3", new("GENERAL"), 2026, 0, 60_000m, 10_000m, 0, 500m, 2_000m), b);
        Assert.Equal(50_000m, b["BI"]);
        Assert.Equal(8_500m - 500m - 2_000m, b["RES"]);
    }

    [Fact]
    public void Plazos_del_200_con_ejercicio_partido_y_del_202()
    {
        var p = new PeriodoFiscal(2025, "0A", new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30));
        var (d, h) = PlazoPresentacion.Calcular("200", p);
        Assert.Equal(new DateOnly(2027, 1, 1), d);
        Assert.Equal(new DateOnly(2027, 1, 25), h);
        Assert.Equal(new DateOnly(2027, 7, 1), PlazoPresentacion.Calcular("200", PeriodoFiscal.Resolver(2026, "0A")).Desde);
        Assert.Equal(new DateOnly(2026, 10, 1), PlazoPresentacion.Calcular("202", PeriodoFiscal.Resolver(2026, "2P")).Desde);
    }
}

public class AsientoSociedadesTests
{
    private static readonly Contabilidad.Domain.Fiscal.CuentasIs C = new(6300, 6301, 473, 4752, 4709, 4740, 479);

    [Fact]
    public void Pago_fraccionado_a_473_contra_4752()
    {
        var a = Contabilidad.Domain.Fiscal.GeneradorAsientoSociedades.PagoFraccionado(1500, C, 1);
        Assert.Equal([(473L, 1500m, 0m), (4752L, 0m, 1500m)], a.Select(x => (x.CuentaId, x.Debe, x.Haber)));
        Assert.Throws<Contabilidad.Domain.ReglaContableException>(() => Contabilidad.Domain.Fiscal.GeneradorAsientoSociedades.PagoFraccionado(0, C, 1));
    }

    [Fact]
    public void Liquidacion_con_cuota_a_ingresar_y_diferido_de_amortizaciones()
    {
        // Cuota 10.000, retenciones 1.000, pagos 3.000: 6.000 a ingresar; el activo diferido sube de 500 a 800 y aparece un pasivo de 200
        var a = Contabilidad.Domain.Fiscal.GeneradorAsientoSociedades.Liquidacion(10000, 1000, 3000, 800, 500, 200, 0, C, 1);
        Assert.Equal(a.Sum(x => x.Debe), a.Sum(x => x.Haber));
        Assert.Equal(10000m, a.Single(x => x.CuentaId == 6300).Debe);
        Assert.Equal(4000m, a.Single(x => x.CuentaId == 473).Haber);
        Assert.Equal(6000m, a.Single(x => x.CuentaId == 4752).Haber);
        Assert.Equal(300m, a.Single(x => x.CuentaId == 4740).Debe);
        Assert.Equal(200m, a.Single(x => x.CuentaId == 479).Haber);
        Assert.Equal(100m, a.Single(x => x.CuentaId == 6301).Haber);       // ingreso neto por impuesto diferido: 300 − 200
    }

    [Fact]
    public void Liquidacion_a_devolver_y_reversion_del_diferido()
    {
        var a = Contabilidad.Domain.Fiscal.GeneradorAsientoSociedades.Liquidacion(2000, 500, 2500, 0, 400, 0, 0, C, 1);
        Assert.Equal(a.Sum(x => x.Debe), a.Sum(x => x.Haber));
        Assert.Equal(1000m, a.Single(x => x.CuentaId == 4709).Debe);
        Assert.Equal(400m, a.Single(x => x.CuentaId == 4740).Haber);
        Assert.Equal(400m, a.Single(x => x.CuentaId == 6301).Debe);
    }
}
