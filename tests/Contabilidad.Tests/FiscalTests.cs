using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class PeriodoFiscalTests
{
    [Theory]
    [InlineData("1T", "2026-01-01", "2026-03-31")]
    [InlineData("2T", "2026-04-01", "2026-06-30")]
    [InlineData("3T", "2026-07-01", "2026-09-30")]
    [InlineData("4T", "2026-10-01", "2026-12-31")]
    [InlineData("02", "2026-02-01", "2026-02-28")]
    [InlineData("12", "2026-12-01", "2026-12-31")]
    [InlineData("0A", "2026-01-01", "2026-12-31")]
    public void Resuelve_las_fechas_del_periodo(string codigo, string inicio, string fin)
    {
        var p = PeriodoFiscal.Resolver(2026, codigo);
        Assert.Equal(DateOnly.Parse(inicio), p.Inicio);
        Assert.Equal(DateOnly.Parse(fin), p.Fin);
    }

    [Fact]
    public void Febrero_bisiesto() => Assert.Equal(new DateOnly(2028, 2, 29), PeriodoFiscal.Resolver(2028, "02").Fin);

    [Theory]
    [InlineData("5T")] [InlineData("00")] [InlineData("13")] [InlineData("")] [InlineData(null)] [InlineData("A0")]
    public void Rechaza_periodos_invalidos(string? codigo) => Assert.Throws<ReglaContableException>(() => PeriodoFiscal.Resolver(2026, codigo));

    [Theory]
    [InlineData("4T", true)] [InlineData("12", true)] [InlineData("0A", true)] [InlineData("3T", false)] [InlineData("11", false)]
    public void Ultimo_periodo_del_anio(string codigo, bool esperado) => Assert.Equal(esperado, PeriodoFiscal.Resolver(2026, codigo).EsUltimoDelAnio);

    [Fact]
    public void Cada_modelo_solo_admite_sus_periodos()
    {
        Assert.Throws<ReglaContableException>(() => ModelosFiscales.ResolverPeriodo(ModelosFiscales.Obtener("347"), 2026, "1T"));
        Assert.Throws<ReglaContableException>(() => ModelosFiscales.ResolverPeriodo(ModelosFiscales.Obtener("130"), 2026, "01"));
        Assert.Throws<ReglaContableException>(() => ModelosFiscales.ResolverPeriodo(ModelosFiscales.Obtener("303"), 2026, "0A"));
        ModelosFiscales.ResolverPeriodo(ModelosFiscales.Obtener("303"), 2026, "07");
        ModelosFiscales.ResolverPeriodo(ModelosFiscales.Obtener("390"), 2026, "0A");
    }

    [Fact]
    public void Un_modelo_desconocido_se_rechaza() => Assert.Throws<ReglaContableException>(() => ModelosFiscales.Obtener("999"));

    [Theory]
    [InlineData("303", "3T", 2026, "2026-10-01", "2026-10-20")]   // 20 oct 2026 es martes
    [InlineData("303", "1T", 2027, "2027-04-01", "2027-04-20")]   // martes
    [InlineData("303", "4T", 2026, "2027-01-01", "2027-02-01")]   // 30 ene 2027 es sábado -> lunes
    [InlineData("130", "2T", 2026, "2026-07-01", "2026-07-20")]   // lunes
    [InlineData("347", "0A", 2026, "2027-02-01", "2027-03-01")]   // 28 feb 2027 es domingo -> lunes 1 mar
    [InlineData("390", "0A", 2026, "2027-01-01", "2027-02-01")]
    public void Plazo_de_presentacion(string modelo, string periodo, int anio, string desde, string hasta)
    {
        var (d, h) = PlazoPresentacion.Calcular(modelo, PeriodoFiscal.Resolver(anio, periodo));
        Assert.Equal(DateOnly.Parse(desde), d);
        Assert.Equal(DateOnly.Parse(hasta), h);
    }
}

public class CalculadorImpuestoIndirectoTests
{
    private static GrupoIva G(string libro, decimal pct, decimal b, decimal c, decimal ded = 0, bool rect = false, bool inv = false,
        decimal pctRec = 0, decimal cRec = 0, string op = "INTERIOR")
        => new(libro, rect, op, pct, pctRec, inv, b, c, cRec, ded);

    private static DeclaracionCalculada Nueva() => new() { Modelo = "303", Anio = 2026, Periodo = "3T" };

    // Datos del ejemplo comprobado contra PostgreSQL (tercer trimestre)
    private static readonly GrupoIva[] Trimestre3 =
    [
        G("EMITIDAS", 4, 200, 8), G("EMITIDAS", 10, 500, 50), G("EMITIDAS", 21, 1000, 210),
        G("EMITIDAS", 21, -100, -21, rect: true),
        G("RECIBIDAS", 21, 1400, 294, ded: 252), G("RECIBIDAS", 21, 2000, 420, ded: 420, inv: true),
    ];

    [Fact]
    public void Iva_tercer_trimestre_con_resultado_a_compensar()
    {
        var d = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new(Trimestre3, 0, 0, false, false), d);

        Assert.Equal(200m, d["01"]); Assert.Equal(4m, d["02"]); Assert.Equal(8m, d["03"]);
        Assert.Equal(500m, d["04"]); Assert.Equal(50m, d["06"]);
        Assert.Equal(1000m, d["07"]); Assert.Equal(21m, d["08"]); Assert.Equal(210m, d["09"]);
        Assert.Equal(-100m, d["14"]); Assert.Equal(-21m, d["15"]);
        Assert.Equal(247m, d["27"]);                      // 8 + 50 + 210 − 21
        Assert.Equal(1400m, d["28"]); Assert.Equal(252m, d["29"]);
        Assert.Equal(2000m, d["30"]); Assert.Equal(420m, d["31"]);
        Assert.Equal(672m, d["45"]);
        Assert.Equal(-425m, d["46"]);
        Assert.Equal(-425m, d["71"]);
        Assert.Equal(-425m, d.Resultado);
        Assert.Equal(0m, d.AIngresar);
        Assert.Equal(425m, d.ACompensar);
        Assert.Equal(425m, d["87"]);
    }

    [Fact]
    public void Cuarto_trimestre_aplica_prorrata_definitiva_y_la_compensacion_anterior()
    {
        var d = Nueva();
        GrupoIva[] g = [G("EMITIDAS", 21, 5000, 1050), G("RECIBIDAS", 21, 100, 21, ded: 21)];
        CalculadorImpuestoIndirecto.Calcular(false, new(g, -21m, 425m, false, true), d);

        Assert.Equal(-21m, d["44"]);
        Assert.Equal(0m, d["45"]);                        // 21 − 21
        Assert.Equal(1050m, d["46"]);
        Assert.Equal(425m, d["68"]);
        Assert.Equal(625m, d["69"]);
        Assert.Equal(625m, d.AIngresar);
        Assert.Equal(0m, d.ACompensar);
        Assert.Equal(0m, d["87"]);
    }

    [Fact]
    public void La_compensacion_aplicada_no_supera_el_resultado()
    {
        var d = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new([G("EMITIDAS", 21, 100, 21)], 0, 500m, false, false), d);
        Assert.Equal(21m, d["68"]);
        Assert.Equal(0m, d.AIngresar);
        Assert.Equal(479m, d["87"]);                      // lo que sobra sigue pendiente
    }

    [Fact]
    public void La_prorrata_definitiva_no_se_aplica_fuera_del_ultimo_periodo()
    {
        var d = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new([G("RECIBIDAS", 21, 100, 21, ded: 21)], -21m, 0, false, false), d);
        Assert.Equal(0m, d["44"]);
        Assert.Contains(d.Avisos, a => a.Contains("último periodo"));
    }

    [Fact]
    public void Devolucion_solo_en_el_ultimo_periodo()
    {
        var g = new[] { G("RECIBIDAS", 21, 1000, 210, ded: 210) };
        var ultimo = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new(g, 0, 0, true, true), ultimo);
        Assert.Equal(210m, ultimo.ADevolver); Assert.Equal(0m, ultimo.ACompensar);

        var otro = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new(g, 0, 0, true, false), otro);
        Assert.Equal(0m, otro.ADevolver); Assert.Equal(210m, otro.ACompensar);
        Assert.NotEmpty(otro.Avisos);
    }

    [Fact]
    public void Recargo_de_equivalencia_va_a_sus_casillas()
    {
        var d = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new([G("EMITIDAS", 21, 1000, 210, pctRec: 5.2m, cRec: 52)], 0, 0, false, false), d);
        Assert.Equal(1000m, d["22"]); Assert.Equal(5.2m, d["23"]); Assert.Equal(52m, d["24"]);
        Assert.Equal(262m, d["27"]);
    }

    [Fact]
    public void Un_tipo_sin_casilla_en_el_303_falla_en_vez_de_perderse()
        => Assert.Throws<ReglaContableException>(() =>
            CalculadorImpuestoIndirecto.Calcular(false, new([G("EMITIDAS", 7, 100, 7)], 0, 0, false, false), Nueva()));

    [Fact]
    public void Operaciones_intracomunitarias_o_inversion_del_sujeto_pasivo_se_rechazan()
        => Assert.Throws<ReglaContableException>(() =>
            CalculadorImpuestoIndirecto.Calcular(false, new([G("RECIBIDAS", 21, 100, 21, op: "INTRACOM_ADQUISICION")], 0, 0, false, false), Nueva()));

    [Fact]
    public void Las_operaciones_exentas_y_no_sujetas_no_alteran_el_resultado()
    {
        var d = Nueva();
        CalculadorImpuestoIndirecto.Calcular(false, new([G("EMITIDAS", 0, 500, 0, op: "EXENTA"), G("EMITIDAS", 21, 100, 21)], 0, 0, false, false), d);
        Assert.Equal(21m, d["27"]);
    }

    [Fact]
    public void Igic_usa_claves_por_tipo_y_cuadra()
    {
        var d = Nueva();
        GrupoIva[] g = [G("EMITIDAS", 7, 300, 21), G("EMITIDAS", 9.5m, 100, 9.5m), G("RECIBIDAS", 7, 200, 14, ded: 14)];
        CalculadorImpuestoIndirecto.Calcular(true, new(g, 0, 0, false, false), d);

        Assert.Equal(300m, d["D7B"]); Assert.Equal(21m, d["D7C"]);
        Assert.Equal(100m, d["D9.5B"]); Assert.Equal(9.5m, d["D9.5C"]);
        Assert.Equal(30.5m, d["TDEV"]);
        Assert.Equal(200m, d["SCB"]); Assert.Equal(14m, d["SCC"]);
        Assert.Equal(14m, d["TDED"]);
        Assert.Equal(16.5m, d["RES"]);
        Assert.Equal(16.5m, d["LIQ"]);
        Assert.Equal(16.5m, d.AIngresar);
    }

    [Fact]
    public void Igic_acepta_cualquier_tipo_sin_fallar()
    {
        var d = Nueva();
        CalculadorImpuestoIndirecto.Calcular(true, new([G("EMITIDAS", 15, 100, 15), G("EMITIDAS", 3, 100, 3)], 0, 0, false, false), d);
        Assert.Equal(18m, d["TDEV"]);
    }
}

public class Calculador130Tests
{
    private static DeclaracionCalculada Nueva() => new() { Modelo = "130", Anio = 2026, Periodo = "3T" };

    [Fact]
    public void Veinte_por_ciento_menos_retenciones_y_pagos_anteriores()
    {
        var d = Nueva();
        Calculador130.Calcular(new(3000, 1000, 150, 0), d);
        Assert.Equal(2000m, d["03"]); Assert.Equal(400m, d["04"]); Assert.Equal(250m, d["07"]);
        Assert.Equal(250m, d.AIngresar);
    }

    [Fact]
    public void Se_descuentan_los_pagos_de_trimestres_anteriores()
    {
        var d = Nueva();
        Calculador130.Calcular(new(3000, 1000, 0, 300), d);
        Assert.Equal(100m, d.AIngresar);
    }

    [Fact]
    public void Con_perdidas_no_hay_pago_ni_importe_negativo()
    {
        var d = Nueva();
        Calculador130.Calcular(new(1000, 3000, 0, 0), d);
        Assert.Equal(-2000m, d["03"]); Assert.Equal(0m, d["04"]);
        Assert.Equal(0m, d.AIngresar);
    }

    [Fact]
    public void Si_el_resultado_es_negativo_no_se_ingresa_y_se_avisa()
    {
        var d = Nueva();
        Calculador130.Calcular(new(3000, 1000, 500, 0), d);     // 400 − 500
        Assert.Equal(-100m, d["07"]);
        Assert.Equal(0m, d.AIngresar);
        Assert.Contains(d.Avisos, a => a.Contains("negativo"));
    }
}

public class Calculador347Tests
{
    private static Operacion347 Op(long t, string clave, int q, decimal imp, string nif = "B1") => new(t, nif, $"Tercero {t}", clave, q, imp);
    private static DeclaracionCalculada Nueva() => new() { Modelo = "347", Anio = 2026, Periodo = "0A" };

    [Fact]
    public void Declara_solo_lo_que_supera_el_umbral_por_clave()
    {
        var d = Nueva();
        Calculador347.Calcular([
            Op(1, "B", 3, 2277.50m), Op(1, "B", 4, 6050m),          // 8.327,50 -> sí
            Op(2, "A", 3, 4328m), Op(2, "A", 4, 121m),              // 4.449 -> sí
            Op(3, "B", 1, 3005.06m),                                // justo el umbral -> no
            Op(4, "A", 2, 3005.07m),                                // un céntimo más -> sí
            Op(6, "B", 1, 4000m), Op(6, "B", 2, -1500m),            // 2.500 tras rectificar -> no
        ], d);

        var filas = Assert.IsAssignableFrom<List<Fila347>>(d.Detalle);
        Assert.Equal(new long[] { 1, 2, 4 }, filas.Select(f => f.TerceroId).OrderBy(x => x).ToArray());
        Assert.Equal(3m, d["N"]);
        Assert.Equal(4449m + 3005.07m, d["TA"]);
        Assert.Equal(8327.50m, d["TB"]);
    }

    [Fact]
    public void Las_claves_A_y_B_de_un_mismo_tercero_se_evaluan_por_separado()
    {
        var d = Nueva();
        Calculador347.Calcular([Op(1, "A", 1, 2000m), Op(1, "B", 1, 2000m)], d);   // 4.000 en total, pero 2.000 por clave
        Assert.Equal(0m, d["N"]);
    }

    [Fact]
    public void Desglosa_por_trimestre_y_avisa_si_falta_el_nif()
    {
        var d = Nueva();
        Calculador347.Calcular([Op(1, "B", 1, 1000m, nif: ""), Op(1, "B", 3, 2500m, nif: "")], d);
        var f = Assert.Single(Assert.IsAssignableFrom<List<Fila347>>(d.Detalle));
        Assert.Equal(3500m, f.Total);
        Assert.Equal(1000m, f.T1); Assert.Equal(0m, f.T2); Assert.Equal(2500m, f.T3); Assert.Equal(0m, f.T4);
        Assert.Contains(d.Avisos, a => a.Contains("NIF"));
    }
}
