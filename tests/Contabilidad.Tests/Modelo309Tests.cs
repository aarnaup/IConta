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
        Assert.Equal((1000m, 210m, 500m, 50m), (c["AIB21B"], c["AIB21C"], c["AIB10B"], c["AIB10C"]));
        Assert.Equal((200m, 42m), (c["ISP21B"], c["ISP21C"]));
        Assert.Equal((52m, 7m), (c["RE5.2C"], c["RE1.4C"]));                   // el recargo solo en las adquisiciones de bienes
        Assert.Equal(361m, c["RES"]);
        Assert.Equal(361m, d.AIngresar);
    }

    [Fact]
    public void En_el_REAGP_no_hay_recargo_y_sin_operaciones_no_se_presenta()
    {
        var d = new DeclaracionCalculada { Modelo = "309" };
        Calculador309.Calcular([new Linea309("ADQUISICION", 10, 1.4m, 1000)], recargoEquivalencia: false, d);
        Assert.Equal(100m, d.AIngresar);
        Assert.DoesNotContain(d.Casillas, x => x.Clave.StartsWith("RE", StringComparison.Ordinal) && x.Clave != "RES");
        var v = new DeclaracionCalculada { Modelo = "309" };
        Calculador309.Calcular([], false, v);
        Assert.Contains(v.Avisos, a => a.Contains("no hay que presentar"));
    }
}
