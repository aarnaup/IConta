using Contabilidad.Domain;
using Contabilidad.Domain.Balances;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;

namespace Contabilidad.Tests;

public class Estados200LegislacionTests
{
    private static readonly ModeloEstado Mini = new("X", "X", [""],
    [
        new("A", "A", 1, false, Suma: "A1 A2"),
        new("A1", "A1", 2, false, "4709:DEBE"),
        new("A2", "A2", 2, false, "-570"),
        new("P", "P", 2, true, "4750 4709:HABER"),
    ]);

    [Fact]
    public void Debe_y_haber_van_a_partidas_distintas_y_el_signo_resta()
    {
        var e = GeneradorEstados.Calcular(Mini, [new SaldoBalance("47090001", "HP deudora", 300, 1000, 700), new SaldoBalance("57000000", "Caja", 50, 50, 0),
            new SaldoBalance("47500000", "HP acreedora", -80, 20, 100)]);
        Assert.Equal(1000m, e["A1"]);                 // solo el debe de la 4709
        Assert.Equal(-50m, e["A2"]);                  // la caja resta por el «-»
        Assert.Equal(950m, e["A"]);
        Assert.Equal(80m + 700m, e["P"]);             // pasivo: haber − debe de la 4750 y el haber de la 4709
        Assert.Empty(e.SinClasificar);
    }

    [Fact]
    public void Calculo_explica_si_suma_o_resta()
    {
        Assert.Equal("+ (Debe − Haber)", ReglaCuenta.Leer("201").Calculo(false));
        Assert.Equal("+ (Haber − Debe)", ReglaCuenta.Leer("100").Calculo(true));
        Assert.Equal("+ Haber", ReglaCuenta.Leer("4709:HABER").Calculo(false));
        Assert.Equal("− Haber", ReglaCuenta.Leer("-4709:HABER").Calculo(false));
        Assert.Equal("+ Haber", ReglaCuenta.Leer("4709:HABER").Calculo(true));
        Assert.Equal("− Debe", ReglaCuenta.Leer("-4709:DEBE").Calculo(false));
        Assert.Equal("+ (Debe − Haber) si es deudor", ReglaCuenta.Leer("5523:D").Calculo(false));
        Assert.Equal("-5523:A", ReglaCuenta.Leer("-5523:A").ToString());
    }

    [Fact]
    public void El_estado_del_programa_se_exporta_e_importa_igual()
    {
        Assert.Equal(6, EstadosLegislados.Codigos.Count());
        foreach (var c in EstadosLegislados.Codigos)
        {
            var m = EstadosLegislados.DelPrograma(c);
            EstadosLegislados.Validar(m);
            var m2 = EstadosLegislados.Importar(m, EstadosLegislados.Exportar(m));
            Assert.Equal(m.Partidas.Select(p => (p.Id, p.Texto, p.Nivel, p.Acreedora, p.Cuentas ?? "", p.Suma)),
                m2.Partidas.Select(p => (p.Id, p.Texto, p.Nivel, p.Acreedora, p.Cuentas ?? "", p.Suma)));
        }
    }

    [Fact]
    public void Una_version_rige_desde_su_ejercicio()
    {
        var m = Estados200.DelPrograma(Estados200.CodigoBalance);
        var cambiado = m with { Partidas = m.Partidas.Select(p => p.Id == "00106" ? p with { Cuentas = "204 2804" } : p).ToList() };
        using (Legislacion.Usar(new ContextoLegislacion([], [new VersionFormulario(Estados200.CodigoBalance, 2027, Estados200.Exportar(cambiado))], [])))
        {
            Assert.Equal("204", Estados200.Modelo(Estados200.CodigoBalance, 2026).Partidas.First(p => p.Id == "00106").Cuentas);
            Assert.Equal("204 2804", Estados200.Modelo(Estados200.CodigoBalance, 2027).Partidas.First(p => p.Id == "00106").Cuentas);
        }
    }

    [Fact]
    public void Las_cuentas_anuales_usan_la_version_de_su_ejercicio()
    {
        var m = ModelosEstados.Balance;
        var cambiado = m with { Partidas = m.Partidas.Select(p => p.Id == "AVI" ? p with { Cuentas = "474 4709:DEBE" } : p).ToList() };
        using (Legislacion.Usar(new ContextoLegislacion([], [new VersionFormulario("BALANCE", 2027, EstadosLegislados.Exportar(cambiado))], [])))
        {
            Assert.Equal("474", EstadosLegislados.Modelo("BALANCE", 2026).Partidas.First(p => p.Id == "AVI").Cuentas);
            var v = EstadosLegislados.Modelo("BALANCE", 2028);
            Assert.Equal("474 4709:DEBE", v.Partidas.First(p => p.Id == "AVI").Cuentas);
            Assert.Equal("VI. Activos por impuesto diferido", v.Partidas.First(p => p.Id == "AVI").Texto);
        }
    }

    [Fact]
    public void Se_rechazan_cuentas_repetidas_y_totales_inexistentes()
    {
        var m = Estados200.DelPrograma(Estados200.CodigoBalance);
        var repetida = m with { Partidas = m.Partidas.Select(p => p.Id == "00106" ? p with { Cuentas = "204 201" } : p).ToList() };
        Assert.Throws<ReglaContableException>(() => Estados200.Validar(repetida));
        var total = m with { Partidas = m.Partidas.Select(p => p.Id == "00101" ? p with { Suma = "00102 99999" } : p).ToList() };
        Assert.Throws<ReglaContableException>(() => Estados200.Validar(total));
        var ciclo = m with { Partidas = m.Partidas.Select(p => p.Id == "00102" ? p with { Suma = "00101" } : p).ToList() };
        Assert.Throws<ReglaContableException>(() => Estados200.Validar(ciclo));
        Estados200.Validar(m);
        Estados200.Validar(Estados200.DelPrograma(Estados200.CodigoPyg));
    }

    [Fact]
    public void Las_partidas_tienen_la_descripcion_del_diseno()
    {
        var d = Estados200.Descripciones(2025);
        Assert.Contains("Desarrollo", d["00103"]);
        Assert.Contains("cifra de negocios", d["00255"]);
    }
}
