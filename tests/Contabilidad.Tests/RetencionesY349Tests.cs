using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class RetencionesTests
{
    private static DeclaracionCalculada Nueva(string m) => new() { Modelo = m, Anio = 2026, Periodo = "3T" };

    [Fact]
    public void Modelo_111_agrupa_por_perceptor()
    {
        var d = Nueva("111");
        CalculadorRetenciones.Calcular("111", [
            new(1, "11111111H", "Abogada", 1000, 150), new(1, "11111111H", "Abogada", 500, 75),
            new(2, "22222222J", "Asesor", 2000, 300), new(2, "22222222J", "Asesor", -200, -30)], 495, 495, d);
        Assert.Equal((2m, 3300m, 495m, 495m), (d["07"], d["08"], d["09"], d["30"]));
        Assert.Equal(495m, d.AIngresar);
        Assert.Equal(2, ((IReadOnlyList<PercepcionRetenida>)d.Detalle!).Count);
        Assert.DoesNotContain(d.Avisos, a => a.Contains("4751"));
    }

    [Fact]
    public void Modelo_115_y_aviso_si_la_4751_no_cuadra()
    {
        var d = Nueva("115");
        CalculadorRetenciones.Calcular("115", [new(3, "33333333P", "Arrendador", 1200, 228)], 1728, 723, d);
        Assert.Equal((1m, 1200m, 228m, 228m), (d["01"], d["02"], d["03"], d["05"]));
        Assert.Contains(d.Avisos, a => a.Contains("4751"));
    }

    [Fact]
    public void Sin_retenciones_avisa()
    {
        var d = Nueva("115");
        CalculadorRetenciones.Calcular("115", [], null, 0, d);
        Assert.Equal(0m, d["05"]);
        Assert.Contains(d.Avisos, a => a.Contains("sin actividad"));
    }
}

public class Modelo349Tests
{
    [Theory]
    [InlineData("FR12345678901", "FR", "FR12345678901")]
    [InlineData("12345678901", "FR", "FR12345678901")]
    [InlineData("de 123.456.789", "DE", "DE123456789")]
    [InlineData("094259216", "GR", "EL094259216")]
    public void Nif_comunitario(string nif, string pais, string esperado) => Assert.Equal(esperado, Calculador349.NifComunitario(nif, pais));

    [Fact]
    public void Agrupa_por_operador_y_clave_y_suma_rectificaciones()
    {
        var d = new DeclaracionCalculada { Modelo = "349" };
        Calculador349.Calcular([
            new(1, "FR12345678901", "FR", true, "Client SARL", "E", 6000), new(1, "12345678901", "FR", true, "Client SARL", "E", 1000),
            new(1, "FR12345678901", "FR", true, "Client SARL", "S", 500), new(2, "DE123456789", "DE", true, "Lieferant", "A", 10000),
            new(3, "US-1", "US", false, "Buyer Inc", "E", 50)],
            [new("DE123456789", "Lieferant", "A", 2026, "2T", 4000, 3500)], d);
        var det = (Detalle349)d.Detalle!;
        Assert.Equal(4, det.Operaciones.Count);
        Assert.Equal(7000m, det.Operaciones.Single(o => o.NifComunitario == "FR12345678901" && o.Clave == "E").Base);
        Assert.Equal((4m, 17550m, 1m, 3500m), (d["01"], d["02"], d["03"], d["04"]));
        Assert.Contains(d.Avisos, a => a.Contains("Buyer Inc"));
    }
}

public class CriterioCajaTests
{
    private static readonly LineaCaja L21 = new(1, 21, 0, false, 1000, 210, 0, 210);
    private static FacturaCaja F(string libro = "EMITIDAS", decimal total = 1210, bool rect = false)
        => new(1, libro, rect, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), total, 9, [L21]);

    [Fact]
    public void Sin_cobro_no_se_devenga_y_se_devenga_al_cobrar_en_proporcion()
    {
        var f = F();
        CobroCaja[] cobros = [new(1, new DateOnly(2026, 5, 2), 605), new(1, new DateOnly(2026, 11, 20), 605)];
        Assert.Empty(CriterioCaja.Devengado(f, cobros, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)).Lineas);
        var t2 = CriterioCaja.Devengado(f, cobros, new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));
        Assert.Equal((500m, 105m), (t2.Base, t2.Cuota));
        var t4 = CriterioCaja.Devengado(f, cobros, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
        Assert.Equal(105m, t4.Cuota);
    }

    [Fact]
    public void Lo_no_cobrado_se_devenga_el_31_de_diciembre_del_anio_siguiente()
    {
        var f = F();
        CobroCaja[] cobros = [new(1, new DateOnly(2026, 5, 2), 121)];
        Assert.Equal(21m, CriterioCaja.Devengado(f, cobros, new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)).Cuota);
        Assert.Empty(CriterioCaja.Devengado(f, cobros, new DateOnly(2027, 7, 1), new DateOnly(2027, 9, 30)).Lineas);
        var limite = CriterioCaja.Devengado(f, cobros, new DateOnly(2027, 10, 1), new DateOnly(2027, 12, 31));
        Assert.Equal((900m, 189m), (limite.Base, limite.Cuota));
    }

    [Fact]
    public void Devolucion_de_un_recibo_resta_y_los_redondeos_no_se_acumulan()
    {
        var f = F(total: 1000);   // con retención: el total cobrable es menor que base + cuota
        CobroCaja[] cobros = [new(1, new DateOnly(2026, 4, 5), 333.33m), new(1, new DateOnly(2026, 4, 25), 333.33m),
                              new(1, new DateOnly(2026, 5, 20), -333.33m), new(1, new DateOnly(2026, 8, 1), 666.67m)];
        var t2 = CriterioCaja.Devengado(f, cobros, new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));
        var t3 = CriterioCaja.Devengado(f, cobros, new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30));
        Assert.Equal(70m, t2.Cuota);
        Assert.Equal(210m, t2.Cuota + t3.Cuota);
    }

    [Fact]
    public void Rectificativa_ligada_sigue_el_cobro_de_la_factura_original()
    {
        var orig = F();                                                   // 1.000 + 210, 10/03/2026
        var rect = new FacturaCaja(2, "EMITIDAS", true, new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 10), -242, 9,
            [new LineaCaja(1, 21, 0, false, -200, -42, 0, 0)], RectificadaId: 1);
        var p2 = new PeriodoFiscal(2026, "2T", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));
        var p3 = new PeriodoFiscal(2026, "3T", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30));

        // Sin cobrar al rectificar: la rectificativa no se devenga sola; al cobrar los 968 se devengan 210 − 42
        Assert.Empty(CriterioCaja.DelPeriodo([orig, rect], [], p2));
        var cobro = new CobroCaja[] { new(1, new DateOnly(2026, 8, 1), 968) };
        var t3 = CriterioCaja.DelPeriodo([orig, rect], cobro, p3);
        Assert.Equal(168m, t3.Sum(d => d.Cuota));
        Assert.Equal((210m, -42m), (t3.Single(d => d.Factura.Id == 1).Cuota, t3.Single(d => d.Factura.Id == 2).Cuota));

        // Ya cobrada al rectificar: la rectificación se devenga en su fecha
        var cobrada = new CobroCaja[] { new(1, new DateOnly(2026, 4, 2), 1210) };
        var t2 = CriterioCaja.DelPeriodo([orig, rect], cobrada, p2);
        Assert.Equal((210m, -42m), (t2.Single(d => d.Factura.Id == 1).Cuota, t2.Single(d => d.Factura.Id == 2).Cuota));

        // Cobrada la mitad antes de rectificar: al rectificar no cambia lo devengado (605 / 968 del nuevo total)
        var mitad = new CobroCaja[] { new(1, new DateOnly(2026, 4, 2), 605) };
        var antes = CriterioCaja.DelIntervalo([orig, rect], mitad, new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 9)).Sum(d => d.Cuota);
        var conRect = CriterioCaja.DelIntervalo([orig, rect], mitad, new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31)).Sum(d => d.Cuota);
        Assert.Equal(105m, antes);
        Assert.Equal(105m, conRect);
        // y el límite es el de la original (31/12/2027): entonces se devenga todo lo que falta
        var limite = CriterioCaja.DelIntervalo([orig, rect], mitad, new DateOnly(2027, 10, 1), new DateOnly(2027, 12, 31)).Sum(d => d.Cuota);
        Assert.Equal(168m - 105m, limite);
    }

    [Fact]
    public void Recibidas_deducen_al_pagar_y_rectificativas_en_su_fecha()
    {
        var f = F("RECIBIDAS");
        var t = CriterioCaja.Devengado(f, [new(1, new DateOnly(2026, 4, 10), 1210)], new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));
        Assert.Equal(210m, t.Cuota);
        var r = F(total: -121, rect: true) with { Lineas = [new LineaCaja(1, 21, 0, false, -100, -21, 0, 0)] };
        Assert.Equal(-21m, CriterioCaja.Devengado(r, [], new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)).Cuota);
        var grupos = CriterioCaja.Grupos([t]);
        Assert.Equal(("RECIBIDAS", 1000m, 210m), (grupos.Single().Libro, grupos.Single().Base, grupos.Single().CuotaDeducible));
    }

    [Fact]
    public void Casillas_informativas_del_303()
    {
        var d = new DeclaracionCalculada { Modelo = "303" };
        CalculadorImpuestoIndirecto.Calcular(false, new([], 0, 0, false, false, 0, new InformacionCaja(1000, 210, 500, 105)), d);
        Assert.Equal((1000m, 210m, 500m, 105m), (d["62"], d["63"], d["74"], d["75"]));
    }
}

public class ResumenRetencionesTests
{
    [Fact]
    public void Modelo_190_por_perceptor_y_contraste_con_los_111()
    {
        var d = new DeclaracionCalculada { Modelo = "190", Anio = 2026, Periodo = "0A" };
        CalculadorResumenRetenciones.Calcular("190", [
            new(1, "11111111H", "Abogada", "ES", "28", "PROF_GEN", 15, 1000, 150), new(1, "11111111H", "Abogada", "ES", "28", "PROF_GEN", 15, 300, 45),
            new(2, "22222222J", "Diseñador", "ES", null, "PROF_INI", 7, 1000, 70)], 265, 4, d);
        var det = (IReadOnlyList<PerceptorResumen>)d.Detalle!;
        Assert.Equal(2, det.Count);
        Assert.Equal(("G", "01", 1300m, 195m), (det[0].Clave, det[0].Subclave, det[0].Base, det[0].Retencion));
        Assert.Equal((2m, 2300m, 265m), (d["01"], d["02"], d["03"]));
        Assert.Contains(d.Avisos, a => a.Contains("Diseñador"));            // sin provincia
        Assert.DoesNotContain(d.Avisos, a => a.Contains("no coinciden"));
    }

    [Fact]
    public void Modelo_180_avisa_si_no_cuadra_con_los_115_y_no_residentes_van_a_la_98()
    {
        var d = new DeclaracionCalculada { Modelo = "180" };
        CalculadorResumenRetenciones.Calcular("180", [new(3, "X1", "Owner Ltd", "GB", null, "ARREND", 19, 12000, 2280)], 1710, 3, d);
        Assert.Equal("98", ((IReadOnlyList<PerceptorResumen>)d.Detalle!)[0].Provincia);
        Assert.Contains(d.Avisos, a => a.Contains("no coinciden"));
    }
}
