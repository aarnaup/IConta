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
