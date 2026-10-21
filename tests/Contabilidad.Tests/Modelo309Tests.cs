using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class Modelo309Tests
{
    [Fact]
    public void Minorista_en_recargo_liquida_la_adquisicion_con_su_recargo_y_la_ISP()
    {
        var d = new DeclaracionCalculada { Modelo = "309", Anio = 2026, Periodo = "2T" };
        Calculador309.Calcular([new Linea309("ADQUISICION", 21, 5.2m, 1000), new Linea309("ADQUISICION", 10, 1.4m, 500),
                                new Linea309("ISP", 21, 5.2m, 200)], recargoEquivalencia: true, d);
        var c = d.Casillas.ToDictionary(x => x.Clave, x => x.Importe);
        Assert.Equal((1200m, 21m, 252m), (c["01"], c["02"], c["03"]));           // régimen general por tipo (adquisiciones e ISP)
        Assert.Equal((500m, 10m, 50m), (c["25"], c["26"], c["27"]));
        Assert.Equal((1000m, 5.2m, 52m), (c["10"], c["11"], c["12"]));          // el recargo solo en las adquisiciones de bienes
        Assert.Equal((500m, 1.4m, 7m), (c["13"], c["14"], c["15"]));
        Assert.Equal((361m, 361m, 1m), (c["22"], c["24"], c["HI"]));
        Assert.Contains(d.Avisos, a => a.Contains("cada hecho imponible"));
        Assert.Equal(361m, d.AIngresar);
    }

    [Fact]
    public void En_el_REAGP_no_hay_recargo_y_sin_operaciones_no_se_presenta()
    {
        var d = new DeclaracionCalculada { Modelo = "309" };
        Calculador309.Calcular([new Linea309("ADQUISICION", 10, 1.4m, 1000)], recargoEquivalencia: false, d);
        Assert.Equal(100m, d.AIngresar);
        Assert.DoesNotContain(d.Casillas, x => x.Clave == "10");
        var v = new DeclaracionCalculada { Modelo = "309" };
        Calculador309.Calcular([], false, v);
        Assert.Contains(v.Avisos, a => a.Contains("no hay que presentar"));
    }
}
