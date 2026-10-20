using Contabilidad.Domain;
using Contabilidad.Domain.Canales;

namespace Contabilidad.Tests;

public class RepartidorCanalesTests
{
    private readonly RepartidorCanales _r = new();

    private static ConfiguracionCanal Base(long id) => new(id, id.ToString(), TipoCanal.Base, null, []);

    [Fact]
    public void Canal_base_va_al_100_a_si_mismo()
    {
        var res = _r.Repartir(Base(1), 250m, 0m);
        var x = Assert.Single(res);
        Assert.Equal(1, x.CanalBaseId);
        Assert.Equal(250m, x.Debe);
        Assert.Null(x.CanalVersionId);
    }

    [Fact]
    public void Canal_A_envia_el_importe_completo_a_cada_canal_base()
    {
        var a = new ConfiguracionCanal(10, "A", TipoCanal.Derivado, 77,
        [
            new LineaCanal(1, ModoReparto.Porcentaje, 100, null, 2, 1),
            new LineaCanal(2, ModoReparto.Porcentaje, 100, null, 2, 2),
            new LineaCanal(3, ModoReparto.Porcentaje, 100, null, 2, 3),
        ]);
        var res = _r.Repartir(a, 0m, 100m);
        Assert.Equal(3, res.Count);
        Assert.All(res, x => { Assert.Equal(100m, x.Haber); Assert.Equal(0m, x.Debe); Assert.Equal(77, x.CanalVersionId); });
    }

    [Fact]
    public void Canal_con_formula_y_resto_cuadra_exactamente()
    {
        var e = new ConfiguracionCanal(11, "E", TipoCanal.Derivado, 5,
        [
            new LineaCanal(1, ModoReparto.Formula, null, "IMPORTE * 0.6", 2, 1),
            new LineaCanal(2, ModoReparto.Resto, null, null, 2, 2),
        ]);
        var res = _r.Repartir(e, 100.01m, 0m);
        Assert.Equal(60.01m, res.Single(x => x.CanalBaseId == 1).Debe);
        Assert.Equal(40.00m, res.Single(x => x.CanalBaseId == 2).Debe);
        Assert.Equal(100.01m, res.Sum(x => x.Debe));
    }

    [Fact]
    public void Canal_derivado_sin_formula_vigente_falla()
    {
        var c = new ConfiguracionCanal(12, "F", TipoCanal.Derivado, null, []);
        Assert.Throws<ReglaContableException>(() => _r.Repartir(c, 10m, 0m));
    }

    [Fact]
    public void Debe_y_haber_a_la_vez_o_negativos_se_rechazan()
    {
        Assert.Throws<ReglaContableException>(() => _r.Repartir(Base(1), 10m, 5m));
        Assert.Throws<ReglaContableException>(() => _r.Repartir(Base(1), -1m, 0m));
    }
}
