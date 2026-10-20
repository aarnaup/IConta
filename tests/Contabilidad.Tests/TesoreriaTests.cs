using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;

namespace Contabilidad.Tests;

public class CalculadorVencimientosTests
{
    private static readonly DateOnly F = new(2026, 1, 31);

    [Fact]
    public void Un_plazo_vence_a_los_dias_indicados()
    {
        var p = Assert.Single(CalculadorVencimientos.Generar(1070m, F, 1, 30, 0));
        Assert.Equal(new DateOnly(2026, 3, 2), p.Fecha);
        Assert.Equal(1070m, p.Importe);
        Assert.Equal(1, p.Numero); Assert.Equal(1, p.Total);
    }

    [Fact]
    public void Contado_vence_el_mismo_dia()
        => Assert.Equal(F, CalculadorVencimientos.Generar(50m, F, 1, 0, 0)[0].Fecha);

    [Fact]
    public void Varios_plazos_separan_las_fechas_y_suman_exactamente_el_total()
    {
        var ps = CalculadorVencimientos.Generar(100m, F, 3, 30, 30);
        Assert.Equal(3, ps.Count);
        Assert.Equal([F.AddDays(30), F.AddDays(60), F.AddDays(90)], ps.Select(p => p.Fecha));
        Assert.Equal(33.33m, ps[0].Importe);
        Assert.Equal(33.33m, ps[1].Importe);
        Assert.Equal(33.34m, ps[2].Importe);          // el residuo de redondeo va al último
        Assert.Equal(100m, ps.Sum(p => p.Importe));
    }

    [Theory]
    [InlineData(0.03, 3)]
    [InlineData(1234.57, 7)]
    [InlineData(999999.99, 12)]
    public void La_suma_nunca_se_desvia_del_total(double total, int plazos)
    {
        var t = (decimal)total;
        var ps = CalculadorVencimientos.Generar(t, F, plazos, 0, 30);
        Assert.Equal(t, ps.Sum(p => p.Importe));
        Assert.All(ps, p => Assert.True(p.Importe > 0));
    }

    [Fact]
    public void Rechaza_importes_no_positivos_plazos_fuera_de_rango_y_varios_plazos_sin_separacion()
    {
        Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(0m, F, 1, 0, 0));
        Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(-5m, F, 1, 0, 0));
        Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(10m, F, 0, 0, 0));
        Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(10m, F, 61, 0, 30));
        Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(10m, F, 2, 30, 0));
        Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(10m, F, 1, -1, 0));
    }

    [Fact]
    public void Un_importe_diminuto_no_se_reparte_en_plazos_de_cero()
        => Assert.Throws<ReglaContableException>(() => CalculadorVencimientos.Generar(0.01m, F, 3, 0, 30));

    [Theory]
    [InlineData(100, 100, "PENDIENTE")]
    [InlineData(100, 40, "PARCIAL")]
    [InlineData(100, 0, "LIQUIDADO")]
    public void El_estado_depende_de_lo_pendiente(decimal importe, decimal pendiente, string esperado)
        => Assert.Equal(esperado, CalculadorVencimientos.EstadoTras(importe, pendiente));
}

public class IbanTests
{
    [Theory]
    [InlineData("ES9121000418450200051332")]
    [InlineData("ES91 2100 0418 4502 0005 1332")]
    [InlineData("es91-2100-0418-4502-0005-1332")]
    [InlineData("DE89370400440532013000")]
    public void Acepta_ibans_correctos_con_cualquier_formato(string iban) => Assert.True(Iban.EsValido(iban));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ES9121000418450200051333")]      // dígito de control erróneo
    [InlineData("ES91210004184502000513")]        // longitud incorrecta para ES
    [InlineData("1234567890123456")]              // sin país
    [InlineData("ES91210004184502000513$2")]
    public void Rechaza_ibans_incorrectos(string? iban) => Assert.False(Iban.EsValido(iban));

    [Fact]
    public void Normalizar_quita_espacios_y_guiones_y_pasa_a_mayusculas()
        => Assert.Equal("ES9121000418450200051332", Iban.Normalizar(" es91 2100-0418 4502 0005 1332 "));
}
