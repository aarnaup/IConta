using Contabilidad.Domain;
using Contabilidad.Domain.Analitica;

namespace Contabilidad.Tests;

public class AnaliticaTests
{
    [Fact]
    public void Reparte_por_dimension_y_el_redondeo_va_al_ultimo()
    {
        var p = RepartoAnalitico.Repartir(100m, 0m,
        [
            new(1, 10, 33.3333m), new(2, 10, 33.3333m), new(3, 10, 33.3334m),   // centros de coste
            new(7, 20, 100m),                                                // proyecto
        ]);
        Assert.Equal([33.33m, 33.33m, 33.34m], p.Where(x => x.ElementoId <= 3).Select(x => x.Debe));
        Assert.Equal(100m, p.Single(x => x.ElementoId == 7).Debe);
        Assert.All(p, x => Assert.Equal(0m, x.Haber));
    }

    [Fact]
    public void Los_porcentajes_de_cada_dimension_suman_100()
    {
        Assert.Throws<ReglaContableException>(() => RepartoAnalitico.Validar([new(1, 10, 60m), new(2, 10, 30m)]));
        Assert.Throws<ReglaContableException>(() => RepartoAnalitico.Validar([new(1, 10, 100m), new(1, 10, 100m)]));
        Assert.Throws<ReglaContableException>(() => RepartoAnalitico.Validar([new(1, 10, 0m)]));
        RepartoAnalitico.Validar([new(1, 10, 60m), new(2, 10, 40m), new(3, 20, 100m)]);
    }

    [Fact]
    public void El_reparto_por_defecto_es_el_de_la_cuenta_mas_concreta_en_cada_dimension()
    {
        (string, ImputacionElemento)[] definidos =
        [
            ("62", new(1, 10, 100m)),              // todo el 62 al centro 1
            ("621", new(2, 10, 50m)), ("621", new(3, 10, 50m)),   // arrendamientos a medias
            ("6", new(9, 20, 100m)),               // todos los gastos al proyecto general
        ];
        var r = RepartoAnalitico.PorDefecto("62100001", definidos);
        Assert.Equal([2L, 3L, 9L], r.Select(x => x.ElementoId).OrderBy(x => x));
        Assert.Equal([1L, 9L], RepartoAnalitico.PorDefecto("62900000", definidos).Select(x => x.ElementoId).OrderBy(x => x));
        Assert.Empty(RepartoAnalitico.PorDefecto("70000000", definidos));
    }

    [Fact]
    public void Presupuesto_lineal_y_signo_de_lo_real()
    {
        Assert.Equal(1000m, Presupuestos.RepartirLineal(1000m, 12).Sum());
        Assert.Equal(83.37m, Presupuestos.RepartirLineal(1000m, 12)[^1]);
        Assert.Equal(500m, Presupuestos.Real("70", 0m, 500m));
        Assert.Equal(300m, Presupuestos.Real("62", 300m, 0m));
        Assert.True(Presupuestos.Favorable("62", 1000m, 900m));
        Assert.False(Presupuestos.Favorable("70", 1000m, 900m));
        Assert.Equal(90m, Presupuestos.Ejecucion(1000m, 900m));
        Assert.Null(Presupuestos.Ejecucion(0m, 900m));
    }

    [Fact]
    public void Avisa_de_lineas_que_se_solapan()
    {
        var s = Presupuestos.Solapados([("62", null), ("621", null), ("70", 5), ("700", 6), ("64", null), ("64", 3)]);
        Assert.Contains("62 incluye 621", s);
        Assert.Contains("64 incluye 64", s);
        Assert.DoesNotContain(s, x => x.StartsWith("70"));
        Assert.Throws<ReglaContableException>(() => Presupuestos.ValidarPatron("62A"));
    }
}
