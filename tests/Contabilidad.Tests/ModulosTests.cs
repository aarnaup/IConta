using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class ModulosTests
{
    private static ModulosActividad Bar(decimal asalariados = 2) => new(null, "Bar", 20000m, asalariados, false, 8000m, 3000m, 4m, 20m);

    [Fact]
    public void Simplificado_ingreso_a_cuenta_y_cuota_anual()
    {
        var d = new DeclaracionCalculada();
        Assert.Equal(320m, CalculadorModulos.Simplificado303([Bar()], false, 0, d));
        Assert.Equal(320m, d["RS"]);
        // Anual: 8000 − 3000 − 80 = 4920 (más que la mínima del 20 %: 1600); menos los tres ingresos a cuenta
        var d4 = new DeclaracionCalculada();
        Assert.Equal(4920m - 960m, CalculadorModulos.Simplificado303([Bar()], true, 960m, d4));
        Assert.Equal((4920m, 960m), (d4["RSD"], d4["RSA"]));
        // Con mucha cuota soportada manda la cuota mínima
        Assert.Equal(1600m, CalculadorModulos.CuotaAnualIva([Bar() with { CuotaIvaSoportadaAnual = 7900m }]));
        Assert.Throws<ReglaContableException>(() => CalculadorModulos.Simplificado303([], false, 0, new DeclaracionCalculada()));
    }

    [Fact]
    public void Modelo_131_por_asalariados_y_agricola()
    {
        Assert.Equal((4m, 3m, 2m), (CalculadorModulos.Porcentaje131(2), CalculadorModulos.Porcentaje131(1), CalculadorModulos.Porcentaje131(0)));
        var d = new DeclaracionCalculada();
        CalculadorModulos.Calcular131([Bar(), new ModulosActividad(null, "Huerta", 0, 0, true, 0, 0, 0, 0)], 5000m, 50m, d);
        Assert.Equal(800m, d["A1C"]);
        Assert.Equal((20_000m, 800m), (d["01"], d["02"]));
        Assert.Equal((5_000m, 100m), (d["05"], d["06"]));
        Assert.Equal((900m, 50m, 850m), (d["07"], d["08"], d["10"]));
        Assert.Equal(850m, d["15"]);
        Assert.Equal(850m, d.AIngresar);
        // Negativos de trimestres anteriores: se deducen hasta dejar la diferencia a cero
        var n = new DeclaracionCalculada();
        CalculadorModulos.Calcular131([Bar()], 0, 0, n, negativosAnteriores: 1_000m);
        Assert.Equal((800m, 0m), (n["11"], n["15"]));
        Assert.Throws<ReglaContableException>(() => CalculadorModulos.Calcular131(Enumerable.Range(1, 6).Select(i => Bar() with { Actividad = $"A{i}" }).ToList(),
            0, 0, new DeclaracionCalculada()));
    }

    [Fact]
    public void El_303_suma_la_cuota_del_simplificado_al_resultado()
    {
        var d = new DeclaracionCalculada { Modelo = "303" };
        CalculadorImpuestoIndirecto.Calcular(false, new EntradaImpuestoIndirecto(
            [new GrupoIva("RECIBIDAS", false, "INTERIOR", 21, 0, true, 1000, 210, 0, 210)], 0, 0, false, false, CuotaSimplificado: 320m), d);
        Assert.Equal(-210m + 320m, d["64"]);
        Assert.Equal(110m, d["71"]);
    }
}
