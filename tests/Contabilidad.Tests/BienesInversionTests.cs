using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class BienesInversionTests
{
    // Máquina comprada en 2026 con 21.000 de IVA y prorrata definitiva del 80 %: dedujo 16.800
    private static BienInversionRegularizable Maquina(DateOnly? venta = null, bool exenta = false, DateOnly? baja = null)
        => new(1, "INM-1", "Máquina", 2026, 5, 21000m, 80, venta, exenta, baja);

    [Fact]
    public void Deduccion_inicial_y_ultimo_anio()
    {
        Assert.Equal(16800m, Maquina().DeduccionInicial);
        Assert.Equal(2030, Maquina().UltimoAnio);
        Assert.Equal(2035, new BienInversionRegularizable(2, "N", "Nave", 2026, 10, 1m, 100).UltimoAnio);
    }

    [Theory]
    [InlineData(2027, 60, -840)]     // (21.000 × 60 % − 16.800) / 5
    [InlineData(2029, 95, 630)]      // (19.950 − 16.800) / 5
    [InlineData(2028, 75, 0)]        // 5 puntos: no se regulariza
    [InlineData(2030, 90, 0)]        // 10 puntos justos: tampoco
    [InlineData(2030, 69, -462)]     // 11 puntos: sí
    public void Regularizacion_anual(int anio, int prorrata, decimal esperado)
    {
        var r = RegularizacionBienesInversion.Anual(Maquina(), anio, prorrata);
        Assert.NotNull(r);
        Assert.Equal(esperado, r.Importe);
        Assert.Equal("ANUAL", r.Tipo);
    }

    [Fact]
    public void Fuera_del_periodo_o_sin_prorrata()
    {
        Assert.Null(RegularizacionBienesInversion.Anual(Maquina(), 2026, 50));   // el año de inicio va con la prorrata definitiva
        Assert.Null(RegularizacionBienesInversion.Anual(Maquina(), 2031, 50));
        var sin = RegularizacionBienesInversion.Anual(Maquina(), 2027, null)!;
        Assert.Equal(0, sin.Importe);
        Assert.StartsWith("Falta", sin.Nota);
        Assert.Null(RegularizacionBienesInversion.Anual(Maquina(baja: new DateOnly(2027, 3, 1)), 2027, 50));   // destruido: no se regulariza
    }

    [Fact]
    public void Venta_sujeta_regulariza_el_resto_al_100()
    {
        var b = Maquina(new DateOnly(2030, 5, 10));
        var r = RegularizacionBienesInversion.Transmision(b)!;
        Assert.Equal((2030, 100, 1, 840m), (r.Anio, r.Prorrata, r.AniosRegularizados, r.Importe));
        Assert.Null(RegularizacionBienesInversion.Anual(b, 2030, 60));             // la venta sustituye a la anual de ese año
        Assert.NotNull(RegularizacionBienesInversion.Anual(b, 2029, 60));
    }

    [Fact]
    public void Venta_exenta_regulariza_el_resto_al_0()
    {
        var r = RegularizacionBienesInversion.Transmision(Maquina(new DateOnly(2028, 2, 1), exenta: true))!;
        Assert.Equal((3, -10080m), (r.AniosRegularizados, r.Importe));            // 21.000 × −80 % × 3/5
        Assert.Null(RegularizacionBienesInversion.Transmision(Maquina(new DateOnly(2031, 2, 1))));   // ya fuera del periodo
        Assert.Equal(4, RegularizacionBienesInversion.Transmision(Maquina(new DateOnly(2026, 9, 1)))!.AniosRegularizados);
    }

    [Fact]
    public void Lo_que_entra_en_cada_periodo()
    {
        var bienes = new[] { Maquina(), Maquina(new DateOnly(2027, 5, 10)) with { Id = 2 } };
        int? Prorrata(BienInversionRegularizable b, int anio) => 60;
        var t2 = RegularizacionBienesInversion.DelPeriodo(bienes, PeriodoFiscal.Resolver(2027, "2T"), Prorrata);
        Assert.Equal((2L, "TRANSMISION", 840m * 4), (t2.Single().BienId, t2.Single().Tipo, t2.Single().Importe));
        var t4 = RegularizacionBienesInversion.DelPeriodo(bienes, PeriodoFiscal.Resolver(2027, "4T"), Prorrata);
        Assert.Equal((1L, "ANUAL", -840m), (t4.Single().BienId, t4.Single().Tipo, t4.Single().Importe));
    }

    [Fact]
    public void Casilla_43_en_el_303()
    {
        var d = new DeclaracionCalculada { Modelo = "303" };
        CalculadorImpuestoIndirecto.Calcular(false, new EntradaImpuestoIndirecto(
            [new GrupoIva("EMITIDAS", false, "INTERIOR", 21, 0, false, 10000, 2100, 0, 0)], 0, 0, false, true, -840), d);
        Assert.Equal(-840m, d["43"]);
        Assert.Equal(-840m, d["45"]);
        Assert.Equal(2940m, d["46"]);
        var igic = new DeclaracionCalculada { Modelo = "420" };
        CalculadorImpuestoIndirecto.Calcular(true, new EntradaImpuestoIndirecto([], 0, 0, false, true, 630), igic);
        Assert.Equal(630m, igic["RBI"]);
    }

    [Fact]
    public void Cambio_de_afectacion_sin_prorrata()
    {
        // Empresa sin prorrata: deducción inicial del 100 %; en 2027 el bien se usa al 50 % en operaciones con derecho
        var b = new BienInversionRegularizable(3, "F", "Furgón", 2026, 5, 10500, 100);
        Assert.Equal(-1050m, RegularizacionBienesInversion.Anual(b, 2027, 50)!.Importe);
        var igual = RegularizacionBienesInversion.Anual(b, 2028, 100)!;
        Assert.Equal(0m, igual.Importe);
        Assert.StartsWith("Mismo porcentaje", igual.Nota);
    }
}
