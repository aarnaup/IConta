using System.Text.Json.Nodes;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;

namespace Contabilidad.Tests;

public class LegislacionTests
{
    private static ContextoLegislacion Ctx(IReadOnlyList<ParametroFiscal>? p = null, IReadOnlyList<VersionFormulario>? f = null,
        IReadOnlyList<DisenoAeat>? d = null) => new(p ?? [], f ?? [], d ?? []);

    [Fact]
    public void Parametro_usa_el_vigente_en_la_fecha()
    {
        Assert.Equal(25m, Legislacion.Parametro("IS_TIPO_REDUCIDA_DIMENSION", new DateOnly(2024, 12, 31)));
        Assert.Equal(24m, Legislacion.Parametro("IS_TIPO_REDUCIDA_DIMENSION", new DateOnly(2025, 1, 1)));
        Assert.Equal(20m, Legislacion.Parametro("IS_TIPO_REDUCIDA_DIMENSION", new DateOnly(2035, 6, 1)));
    }

    [Fact]
    public void Parametro_de_la_base_de_datos_sustituye_al_del_programa()
    {
        using (Legislacion.Usar(Ctx([new("IRPF_130_PCT", new DateOnly(2000, 1, 1), 20, ""), new("IRPF_130_PCT", new DateOnly(2027, 1, 1), 18, "")])))
        {
            Assert.Equal(20m, Legislacion.Parametro("IRPF_130_PCT", new DateOnly(2026, 12, 31)));
            Assert.Equal(18m, Legislacion.Parametro("IRPF_130_PCT", new DateOnly(2027, 3, 31)));
            // las claves que la base de datos no tiene siguen con el valor del programa
            Assert.Equal(3005.06m, Legislacion.Parametro("IVA_347_UMBRAL", new DateOnly(2027, 1, 1)));
        }
        Assert.Equal(20m, Legislacion.Parametro("IRPF_130_PCT", new DateOnly(2027, 3, 31)));
        Assert.Throws<ReglaContableException>(() => Legislacion.Parametro("IS_TIPO_REDUCIDA_DIMENSION", new DateOnly(1999, 1, 1)));
    }

    [Fact]
    public void Tipos_de_sociedades_por_ejercicio()
    {
        Assert.Equal((23m, 23m), CalculadorSociedades.Tipos(new("REDUCIDA_DIMENSION"), 2026));
        using (Legislacion.Usar(Ctx([new("IS_TIPO_REDUCIDA_DIMENSION", new DateOnly(2026, 1, 1), 22, "")])))
            Assert.Equal((22m, 22m), CalculadorSociedades.Tipos(new("REDUCIDA_DIMENSION"), 2026));
    }

    [Fact]
    public void Exportar_y_aplicar_conserva_el_formulario()
    {
        var f = FormulariosFiscales.DelPrograma("303", 2026);
        var g = Legislacion.Aplicar(f, Legislacion.Exportar(f));
        Assert.Equal(f.Apartados.SelectMany(a => a.Filas).SelectMany(x => x.Casillas), g.Apartados.SelectMany(a => a.Filas).SelectMany(x => x.Casillas));
        Assert.Equal(f.Totales.Select(t => t.Casilla), g.Totales.Select(t => t.Casilla));
        Assert.Equal(f.CasillaResultado, g.CasillaResultado);
    }

    [Fact]
    public void La_version_del_formulario_rige_desde_su_anio()
    {
        var def = new JsonObject
        {
            ["renombrar"] = new JsonObject { ["03"] = "03B" },
            ["totales"] = new JsonArray(new JsonObject { ["casilla"] = "99", ["terminos"] = new JsonArray("01", new JsonArray("02", -1)) }),
        }.ToJsonString();
        using (Legislacion.Usar(Ctx(f: [new VersionFormulario("130", 2027, def)])))
        {
            Assert.DoesNotContain(FormulariosFiscales.Obtener("130", [], 2026).Totales, t => t.Casilla == "99");
            var t99 = Assert.Single(FormulariosFiscales.Obtener("130", [], 2027).Totales, t => t.Casilla == "99");
            Assert.Equal([("01", 1), ("02", -1)], t99.Terminos);
            Assert.Empty(Legislacion.Renombrar("130", 2026));
            Assert.Equal("03B", Legislacion.Renombrar("130", 2028)["03"]);
        }
    }

    [Fact]
    public void Diseno_se_exporta_e_importa_y_el_guardado_tiene_preferencia()
    {
        var d = FicheroModelo.Diseno("303", 2026);
        var copia = Legislacion.ImportarDiseno("303", d.Desde, d.Hasta, Legislacion.ExportarDiseno(d));
        Assert.Equal(d.Longitud, copia.Longitud);
        Assert.Equal(Legislacion.ExportarDiseno(d), Legislacion.ExportarDiseno(copia));

        var nuevo = Legislacion.ImportarDiseno("303", new DateOnly(2027, 1, 1), null, Legislacion.ExportarDiseno(d));
        using (Legislacion.Usar(Ctx(d: [nuevo])))
        {
            Assert.Same(nuevo, FicheroModelo.Diseno("303", 2027));
            Assert.NotSame(nuevo, FicheroModelo.Diseno("303", 2026));
        }
    }

    [Fact]
    public void Diseno_invalido_se_rechaza()
        => Assert.Throws<ReglaContableException>(() => Legislacion.ImportarDiseno("303", new DateOnly(2027, 1, 1), null, "{\"modelo\":\"303\"}"));
}
