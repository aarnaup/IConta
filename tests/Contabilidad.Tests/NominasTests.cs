using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Nominas;

namespace Contabilidad.Tests;

public class NominasTests
{
    private static readonly CuentasNomina C = new(640, 642, 649, 4751, 476, 460, 465);

    [Fact]
    public void Asiento_de_la_nomina_cuadra_y_paga_el_liquido_en_la_465()
    {
        var n = new ImportesNomina(Bruto: 2000, Especie: 0, IngresoACuenta: 0, IngresoRepercutido: true, Retencion: 300, SsTrabajador: 127,
            SsEmpresa: 640, Anticipos: 100);
        Assert.Equal(1473m, n.Liquido);
        Assert.Equal(15m, n.PorcentajeRetencion);
        var a = GeneradorAsientoNomina.Generar(n, C, 1);
        Assert.Equal(a.Sum(x => x.Debe), a.Sum(x => x.Haber));
        Assert.Equal([(640L, 2000m, 0m), (642L, 640m, 0m), (4751L, 0m, 300m), (476L, 0m, 767m), (460L, 0m, 100m), (465L, 0m, 1473m)],
            a.Select(x => (x.CuentaId, x.Debe, x.Haber)));
    }

    [Fact]
    public void Ingreso_a_cuenta_de_la_especie_repercutido_o_asumido_por_la_empresa()
    {
        var rep = new ImportesNomina(1500, 200, 30, true, 150, 95, 480, 0);
        Assert.Equal(1500m - 150 - 95 - 30, rep.Liquido);
        var a = GeneradorAsientoNomina.Generar(rep, C, 1);
        Assert.Equal(180m, a.Single(x => x.CuentaId == 4751).Haber);
        Assert.DoesNotContain(a, x => x.CuentaId == 649);

        var asumido = rep with { IngresoRepercutido = false };
        var b = GeneradorAsientoNomina.Generar(asumido, C, 1);
        Assert.Equal(30m, b.Single(x => x.CuentaId == 649).Debe);
        Assert.Equal(b.Sum(x => x.Debe), b.Sum(x => x.Haber));
    }

    [Fact]
    public void Importes_que_no_tienen_sentido()
    {
        Assert.Throws<ReglaContableException>(() => new ImportesNomina(0, 0, 0, true, 0, 0, 0, 0).Validar());
        Assert.Throws<ReglaContableException>(() => new ImportesNomina(1000, 0, 0, true, 0, 0, 0, 1200).Validar());   // líquido negativo
        Assert.Throws<ReglaContableException>(() => new ImportesNomina(1000, 0, 50, true, 0, 0, 0, 0).Validar());    // ingreso sin especie
        Assert.Throws<ReglaContableException>(() => new ImportesNomina(1000, 0, 0, true, -1, 0, 0, 0).Validar());
    }

    [Fact]
    public void El_111_suma_trabajo_y_actividades_economicas()
    {
        var d = new DeclaracionCalculada { Modelo = "111", Anio = 2026, Periodo = "1T" };
        CalculadorRetenciones.Calcular("111", [new PercepcionRetenida(9, "11111111H", "Abogada", 1000, 150)], 1950m, 1400m, d,
            [new PercepcionTrabajo(1, "22222222J", "Ana", 2000, 300, 0, 0), new PercepcionTrabajo(1, "22222222J", "Ana", 2000, 300, 0, 0),
             new PercepcionTrabajo(2, "33333333P", "Luis", 3000, 600, 400, 50)]);
        var c = d.Casillas.ToDictionary(x => x.Clave, x => x.Importe);
        Assert.Equal((2m, 7000m, 1200m), (c["01"], c["02"], c["03"]));
        Assert.Equal((1m, 400m, 50m), (c["04"], c["05"], c["06"]));
        Assert.Equal((1m, 1000m, 150m), (c["07"], c["08"], c["09"]));
        Assert.Equal(1400m, c["28"]);
        Assert.Equal(1400m, d.AIngresar);
        Assert.Contains(d.Avisos, a => a.Contains("4751"));                     // la cuenta (1.950) no cuadra con 1.400
    }

    [Fact]
    public void El_190_lleva_la_clave_A_con_los_datos_del_trabajador()
    {
        var d = new DeclaracionCalculada { Modelo = "190", Anio = 2026, Periodo = "0A" };
        var datos = new DatosTrabajador(1985, 2, "12345678Z", 0, 1, false);
        CalculadorResumenRetenciones.Calcular("190",
            [new PercepcionAnual(9, "11111111H", "Abogada", "ES", "28", "PROF_GEN", 15, 4000, 600)], 4560m, 4, d,
            [new TrabajoAnual(1, "22222222J", "Ana", "ES", "46", 24000, 3600, 2400, 360, 360, 1524, datos)]);
        var lineas = Assert.IsType<List<PerceptorResumen>>(d.Detalle);
        Assert.Equal(["A", "G"], lineas.Select(l => l.Clave));
        var a = lineas[0];
        Assert.Equal((24000m, 3600m, 15m, 2400m, 1524m), (a.Base, a.Retencion, a.Porcentaje, a.Especie, a.GastosDeducibles));
        Assert.Equal(datos, a.Datos);
        var c = d.Casillas.ToDictionary(x => x.Clave, x => x.Importe);
        Assert.Equal((2m, 24000m + 2400 + 4000, 3600m + 360 + 600), (c["01"], c["02"], c["03"]));
        Assert.DoesNotContain(d.Avisos, x => x.Contains("no coinciden"));        // 4.560 frente a los 111 presentados
    }
}
