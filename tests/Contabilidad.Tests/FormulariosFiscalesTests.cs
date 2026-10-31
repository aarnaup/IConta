using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class FormulariosFiscalesTests
{
    private static readonly Dictionary<string, decimal> Calc303 = new()
    {
        ["07"] = 1000, ["08"] = 21, ["09"] = 210, ["27"] = 210, ["28"] = 500, ["29"] = 105, ["45"] = 105,
        ["46"] = 105, ["64"] = 105, ["65"] = 100, ["66"] = 105, ["77"] = 0, ["110"] = 40, ["78"] = 40, ["69"] = 65, ["70"] = 0, ["71"] = 65,
    };
    private static readonly Dictionary<string, decimal> Nada = new();

    [Fact]
    public void Recalcular303_UnaCasillaManualMueveLosTotalesYLaCompensacion()
    {
        var f = FormulariosFiscales.Obtener("303", Calc303.Keys);
        var v = FormulariosFiscales.Recalcular(f, Calc303, Nada, new Dictionary<string, decimal> { ["29"] = 200 });
        Assert.Equal((200m, 10m, 10m, 0m), (v["45"], v["66"], v["78"], v["71"]));        // la compensación no pasa de lo atribuible

        var sin = FormulariosFiscales.Recalcular(f, Calc303, Nada, Nada);
        Assert.Equal(65m, sin["71"]);                                                       // sin cambios, lo calculado
    }

    [Fact]
    public void Recalcular303_UnTotalEscritoAManoSeRespetaYArrastra()
    {
        var f = FormulariosFiscales.Obtener("303", Calc303.Keys);
        var v = FormulariosFiscales.Recalcular(f, Calc303, Nada, new Dictionary<string, decimal> { ["27"] = 300, ["78"] = 0 });
        Assert.Equal((300m, 195m, 195m, 195m), (v["27"], v["46"], v["69"], v["71"]));
    }

    [Fact]
    public void Recalcular303_LosAjustesDeOtroPeriodoEntranEnLasCasillasDeModificacion()
    {
        var f = FormulariosFiscales.Obtener("303", Calc303.Keys.Append("15").ToList());
        var v = FormulariosFiscales.Recalcular(f, Calc303, new Dictionary<string, decimal> { ["14"] = 100, ["15"] = 21 }, Nada);
        Assert.Equal((21m, 231m, 86m), (v["15"], v["27"], v["71"]));
        Assert.Contains(f.Apartados[0].Filas, x => x.Casillas.Contains("15"));
    }

    [Fact]
    public void Recalcular130_El20PorCientoSeRecalculaYNoBajaDeCero()
    {
        var calc = new Dictionary<string, decimal> { ["01"] = 10000, ["02"] = 4000, ["03"] = 6000, ["04"] = 1200, ["05"] = 0, ["06"] = 0, ["07"] = 1200,
            ["12"] = 1200, ["13"] = 0, ["14"] = 1200, ["15"] = 0, ["16"] = 0, ["17"] = 1200, ["18"] = 0, ["19"] = 1200 };
        var f = FormulariosFiscales.Obtener("130", calc.Keys);
        Assert.Equal(400m, FormulariosFiscales.Recalcular(f, calc, Nada, new Dictionary<string, decimal> { ["02"] = 8000 })["19"]);
        Assert.Equal(0m, FormulariosFiscales.Recalcular(f, calc, Nada, new Dictionary<string, decimal> { ["02"] = 12000 })["04"]);
        Assert.Equal((0m, 0m, 0m), FormulariosFiscales.Repartir(f, -50, devolucion: false));
    }

    [Fact]
    public void Recalcular111_LasRetencionesAnadidasAManoSumanEnElTotal()
    {
        var calc = new Dictionary<string, decimal> { ["07"] = 1, ["08"] = 1000, ["09"] = 150, ["28"] = 150, ["29"] = 0, ["30"] = 150 };
        var f = FormulariosFiscales.Obtener("111", calc.Keys);
        var v = FormulariosFiscales.Recalcular(f, calc, Nada, new Dictionary<string, decimal> { ["15"] = 38 });
        Assert.Equal((188m, 188m), (v["28"], v["30"]));
    }

    [Fact]
    public void Generico_SinTotalesNiResultado()
    {
        var f = FormulariosFiscales.Obtener("347", ["01", "02"]);
        Assert.Null(f.CasillaResultado);
        Assert.Equal(["01", "02"], f.Apartados.Single().Filas.Select(x => x.Casillas[0]));
    }

    [Theory]
    [InlineData(2026, "1T", 2026, "2T")]
    [InlineData(2026, "4T", 2027, "1T")]
    [InlineData(2026, "09", 2026, "10")]
    [InlineData(2026, "12", 2027, "01")]
    public void Siguiente_PasaAlPeriodoSiguiente(int anio, string periodo, int anioSig, string periodoSig)
        => Assert.Equal((anioSig, periodoSig), FormulariosFiscales.Siguiente(anio, periodo));

    [Theory]
    [InlineData("303", "07", "14")]
    [InlineData("303", "09", "15")]
    [InlineData("303", "29", "41")]
    [InlineData("303", "27", null)]
    [InlineData("420", "D7C", "MODC")]
    [InlineData("420", "SCB", "RDB")]
    [InlineData("111", "09", "09")]
    [InlineData("111", "30", null)]
    public void CasillaCorreccion_LlevaCadaDiferenciaASuCasilla(string modelo, string casilla, string? destino)
        => Assert.Equal(destino, FormulariosFiscales.CasillaCorreccion(modelo, casilla));
}
