using Contabilidad.Domain;
using Contabilidad.Domain.Canales;

namespace Contabilidad.Tests;

public class ExpresionEvaluadorTests
{
    private static readonly Dictionary<string, decimal> V = new() { ["IMPORTE"] = 100.55m };

    [Theory]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("-IMPORTE + 200", 99.45)]
    [InlineData("ROUND(IMPORTE * 0.6, 2)", 60.33)]
    [InlineData("MIN(3, 5, 1)", 1)]
    [InlineData("MAX(3, 5, 1)", 5)]
    [InlineData("ABS(-7.5)", 7.5)]
    public void Evalua_expresiones_validas(string expr, double esperado)
        => Assert.Equal((decimal)esperado, ExpresionEvaluador.Evaluar(expr, V));

    [Theory]
    [InlineData("")]
    [InlineData("IMPORTE / 0")]
    [InlineData("DESCONOCIDA + 1")]
    [InlineData("IMPORTE ** 2")]
    [InlineData("(1 + 2")]
    [InlineData("FOO(1)")]
    public void Rechaza_expresiones_invalidas(string expr)
        => Assert.Throws<ReglaContableException>(() => ExpresionEvaluador.Evaluar(expr, V));

    [Fact]
    public void Las_variables_no_distinguen_mayusculas()
        => Assert.Equal(100.55m, ExpresionEvaluador.Evaluar("importe", V));
}
