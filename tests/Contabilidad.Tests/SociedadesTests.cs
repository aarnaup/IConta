using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class SociedadesTests
{
    private static readonly DateOnly Ini = new(2026, 1, 1), Fin = new(2026, 12, 31);

    private static DeclaracionCalculada Calc(decimal resultado, TipoGravamenIs tipo, decimal bins = 0, IReadOnlyList<AjusteIs>? ajustes = null,
        decimal reserva = 0, decimal ret = 0, decimal pagos = 0, decimal deduc = 0, DateOnly? ini = null, DateOnly? fin = null, decimal impuesto = 0)
    {
        var d = new DeclaracionCalculada { Modelo = "200" };
        CalculadorSociedades.Calcular200(new Entrada200(resultado, ajustes ?? [], reserva, bins, 0, 0, deduc, ret, (pagos, 0, 0), tipo, ini ?? Ini,
            fin ?? Fin, impuesto), d);
        return d;
    }

    [Fact]
    public void Tipo_general_con_ajustes_retenciones_y_pagos_fraccionados()
    {
        var d = Calc(100_000m, new("GENERAL"), ajustes: [new("Multas", 2_000m, "MULTAS"), new("Amortización fiscal", -12_000m, "AMORTIZACION"),
            new("Otro", 500m), new("Otro más", 250m, "OTRAS")], ret: 1_000m, pagos: 5_000m);
        Assert.Equal(100_000m, d["00500"]);
        Assert.Equal(100_000m, d["00501"]);
        Assert.Equal(2_000m, d["01815"]);
        Assert.Equal(12_000m, d["00304"]);
        Assert.Equal(750m, d["00413"]);
        Assert.Equal(90_750m, d["00550"]);
        Assert.Equal(25m, d["00558"]);
        Assert.Equal(22_687.50m, d["00562"]);
        Assert.Equal(22_687.50m, d["00592"]);
        Assert.Equal(1_000m, d["01766"]);
        Assert.Equal(21_687.50m, d["00599"]);
        Assert.Equal(5_000m, d["00601"]);
        Assert.Equal(16_687.50m, d["00611"]);
        Assert.Equal(16_687.50m, d["00621"]);
        Assert.Equal(16_687.50m, d.AIngresar);
    }

    [Fact]
    public void El_gasto_por_impuesto_contabilizado_se_corrige_y_las_clases_validan_el_signo()
    {
        var d = Calc(100_000m, new("GENERAL"), impuesto: 25_000m);
        Assert.Equal(75_000m, d["00500"]);
        Assert.Equal(25_000m, d["00301"]);
        Assert.Equal(100_000m, d["00550"]);
        Assert.Throws<ReglaContableException>(() => Calc(1m, new("GENERAL"), ajustes: [new("Multa devuelta", -10m, "MULTAS")]));
        Assert.Throws<ReglaContableException>(() => Calc(1m, new("GENERAL"), ajustes: [new("X", 10m, "INVENTADA")]));
        var m = Calc(100_000m, new("MICRO"));
        Assert.Equal(21m, m["00558"]);
        Assert.Equal(19m, m["TG1"]);
    }

    [Fact]
    public void Compensa_bases_negativas_y_genera_las_nuevas()
    {
        var d = Calc(50_000m, new("GENERAL"), bins: 80_000m);
        Assert.Equal(50_000m, d["00547"]);   // hasta 1 M€ sin límite porcentual
        Assert.Equal(0m, d["00552"]);
        var n = Calc(-30_000m, new("GENERAL"));
        Assert.Equal(-30_000m, n["00552"]);
        Assert.Equal(0m, n["00562"]);
        Assert.Equal(2_100_000m, CalculadorSociedades.LimiteBins(3_000_000m, 0));
        Assert.Equal(1_000_000m, CalculadorSociedades.LimiteBins(1_200_000m, 30_000_000m));
    }

    [Fact]
    public void Detalle_de_bases_negativas_por_anio_compensando_las_mas_antiguas()
    {
        var d = new DeclaracionCalculada { Modelo = "200" };
        CalculadorSociedades.Calcular200(new Entrada200(50_000m, [], 0, 80_000m, 0, 0, 0, 0, (0, 0, 0), new("GENERAL"), Ini, Fin,
            BinsPorAnio: [new(2023, 30_000m), new(2021, 50_000m)]), d);
        Assert.Equal(50_000m, d["00547"]);
        Assert.Equal((50_000m, 50_000m, 0m), (d["BIN2021P"], d["BIN2021A"], d["BIN2021F"]));
        Assert.Equal((30_000m, 30_000m), (d["BIN2023P"], d["BIN2023F"]));
        Assert.Null(d.Casillas.FirstOrDefault(c => c.Clave == "BIN2023A"));
        Assert.Equal((80_000m, 30_000m), (d["00670"], d["00671"]));
        // Base negativa del periodo: se genera en su año
        var n = Calc(-10_000m, new("GENERAL"), bins: 5_000m);
        Assert.Equal((5_000m, 5_000m), (n["BIN2025P"], n["BIN2025F"]));      // sin detalle: del año anterior
        Assert.Equal((10_000m, 10_000m), (n["BIN2026P"], n["BIN2026F"]));
        Assert.Equal(15_000m, n["00671"]);
        Assert.Throws<ReglaContableException>(() => CalculadorSociedades.Calcular200(new Entrada200(1m, [], 0, 10m, 0, 0, 0, 0, (0, 0, 0),
            new("GENERAL"), Ini, Fin, BinsPorAnio: [new(2020, 5m)]), new DeclaracionCalculada()));
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
        Assert.Equal(20_000m, a["01"]);
        Assert.Equal(3_600m, a["03"]);
        Assert.Equal(17m, CalculadorSociedades.Porcentaje403(new("GENERAL"), 2026));
        var b = new DeclaracionCalculada();
        CalculadorSociedades.Calcular202(new Entrada202("ART_40_3", new("GENERAL"), 2026, 0, 60_000m, 10_000m, 0, 500m, 2_000m), b);
        Assert.Equal(60_000m, b["13"]);
        Assert.Equal(10_000m, b["14"]);
        Assert.Equal(50_000m, b["16"]);
        Assert.Equal(17m, b["17"]);
        Assert.Equal(8_500m, b["18"]);
        Assert.Equal(8_500m - 500m - 2_000m, b["34"]);
        Assert.Equal(b["32"], b["34"]);
    }

    [Fact]
    public void Pago_fraccionado_de_microempresa_con_dos_porcentajes()
    {
        // 2026: 19 % los primeros 50.000 € y 21 % el resto → 5/7: 13 % y 15 %
        var d = new DeclaracionCalculada();
        CalculadorSociedades.Calcular202(new Entrada202("ART_40_3", new("MICRO"), 2026, 0, 80_000m, 0, 0, 0, 0), d);
        Assert.Equal(50_000m, d["20"]);
        Assert.Equal(13m, d["21"]);
        Assert.Equal(30_000m, d["23"]);
        Assert.Equal(15m, d["24"]);
        Assert.Equal(6_500m + 4_500m, d["26"]);
        Assert.Equal(11_000m, d["34"]);
        Assert.Null(d.Casillas.FirstOrDefault(c => c.Clave == "16"));
    }

    [Fact]
    public void Gran_empresa_con_19_veinteavos_del_tipo_y_pago_minimo()
    {
        Assert.Equal(24m, CalculadorSociedades.PorcentajeSobre(25m, 12_000_000m));
        Assert.Equal(17m, CalculadorSociedades.PorcentajeSobre(25m, 9_999_999m));
        // Base 1.000.000 con 300.000 de retenciones: 24 % = 240.000 − 300.000 → 0; mínimo 23 % = 230.000
        var d = new DeclaracionCalculada();
        CalculadorSociedades.Calcular202(new Entrada202("ART_40_3", new("GENERAL"), 2026, 0, 1_000_000m, 0, 0, 300_000m, 0, 12_000_000m), d);
        Assert.Equal(24m, d["17"]);
        Assert.Equal(0m, d["32"]);
        Assert.Equal(230_000m, d["33"]);
        Assert.Equal(230_000m, d["34"]);
        Assert.Throws<ReglaContableException>(() => CalculadorSociedades.Calcular202(
            new Entrada202("ART_40_2", new("GENERAL"), 2026, 1m, 0, 0, 0, 0, 0, 12_000_000m), new DeclaracionCalculada()));
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
