using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;

namespace Contabilidad.Tests;

public class XmlAtcTests
{
    private static readonly FicheroAtcDatos Datos = new()
    {
        Nif = "B35123456", Nombre = "Canarias Demo S.L.", SiglasVia = "CL", NombreVia = "León y Castillo 1", Provincia = "35",
        Municipio = "016", CodigoPostal = "35003", Telefono = "928000000", Email = "a@b.es",
    };

    private static void Valida(XDocument doc, string modelo)
    {
        var set = new XmlSchemaSet();
        set.Add(null, Path.Combine(AppContext.BaseDirectory, "Esquemas", $"Presentacion-{modelo}-XMLSchema.xsd"));
        var errores = new List<string>();
        doc.Validate(set, (_, e) => errores.Add(e.Message));
        Assert.True(errores.Count == 0, string.Join("\n", errores));
    }

    private static DeclaracionCalculada Calcula420()
    {
        var d = new DeclaracionCalculada { Modelo = "420", Anio = 2026, Periodo = "3T" };
        CalculadorImpuestoIndirecto.Calcular(true, new([
            new GrupoIva("EMITIDAS", false, "INTERIOR", 7, 0, false, 10000, 700, 0, 0),
            new GrupoIva("EMITIDAS", false, "INTERIOR", 3, 0, false, 1000, 30, 0, 0),
            new GrupoIva("EMITIDAS", true, "INTERIOR", 7, 0, false, -500, -35, 0, 0),
            new GrupoIva("RECIBIDAS", false, "INTERIOR", 7, 0, false, 4000, 280, 0, 280),
            new GrupoIva("RECIBIDAS", false, "IMPORTACION", 7, 0, true, 2000, 140, 0, 140),
        ], 0, 50, false, false), d);
        return d;
    }

    [Fact]
    public void El_420_lleva_la_numeracion_oficial_y_valida_contra_el_esquema_de_la_ATC()
    {
        var d = Calcula420();
        Assert.StartsWith("[09]", d.Casillas.Single(c => c.Clave == "D7C").Descripcion);
        Assert.StartsWith("[40]", d.Casillas.Single(c => c.Clave == "TDED").Descripcion);
        var doc = XmlAtc.Modelo420(2026, "3T", "ORDINARIA", d.Casillas, d.Resultado, false, Datos);
        Valida(doc, "420");
        var dec = doc.Root!;
        Assert.Equal(["MOD", "ANY", "PER"], dec.Attributes().Select(a => a.Name.LocalName));
        var dev = dec.Element("IGI_DEV")!;
        Assert.Equal("69500", dev.Attribute("TOT")!.Value);                           // 730 − 35
        Assert.Contains(dev.Elements("DEV"), e => e.Attribute("TIP")!.Value == "700" && e.Attribute("BAS")!.Value == "1000000");
        Assert.Equal(("-50000", "-3500"), (dev.Element("MBC")!.Attribute("BAS")!.Value, dev.Element("MBC")!.Attribute("CUO")!.Value));
        Assert.Equal("14000", dec.Element("IGI_DED")!.Element("IMI")!.Attribute("CUO")!.Value);
        var liq = dec.Element("LIQ")!;
        Assert.Equal(("27500", "5000", "22500"), (liq.Attribute("DIF")!.Value, liq.Attribute("CPA")!.Value, liq.Attribute("RLI")!.Value));
        Assert.Equal(("I", "22500", "5"), (dec.Element("RES")!.Attribute("TIP")!.Value, dec.Element("RES")!.Attribute("IMP")!.Value,
            dec.Element("RES")!.Attribute("FPA")!.Value));
    }

    [Fact]
    public void El_425_con_las_casillas_oficiales_valida_contra_su_esquema()
    {
        var d = new DeclaracionCalculada { Modelo = "425", Anio = 2026, Periodo = "0A" };
        Calculador425.Calcular(new Entrada425([
            new GrupoIva("EMITIDAS", false, "INTERIOR", 7, 0, false, 10000, 700, 0, 0),
            new GrupoIva("EMITIDAS", false, "EXPORTACION", 0, 0, false, 3000, 0, 0, 0),
            new GrupoIva("RECIBIDAS", false, "INTERIOR", 7, 0, false, 4000, 280, 0, 280),
        ], 0, 0, [new Resumen303("1T", 420, 420, 0, 0, 0, 0, 0)]), d);
        var c = d.Casillas.ToDictionary(x => x.Clave, x => x.Importe);
        Assert.Equal((10000m, 700m, 280m, 420m, 420m), (c["07"], c["09"], c["81"], c["95"], c["115"]));
        Assert.Equal(13000m, c["120"]);
        Assert.Contains(d.Avisos, a => a.StartsWith("Cuadra"));
        var doc = XmlAtc.Modelo425(2026, d.Casillas, new DateOnly(2027, 1, 20), Datos);
        Valida(doc, "425");
        Assert.Equal("1000000", doc.Root!.Element("REG")!.Element("DEV")!.Element("RGO")!.Attribute("BAS")!.Value);
        Assert.Equal("1300000", doc.Root.Element("OPE")!.Attribute("TOT")!.Value);
    }

    [Fact]
    public void El_425_lleva_los_datos_estadisticos_por_actividad()
    {
        var d = new DeclaracionCalculada { Modelo = "425", Anio = 2026, Periodo = "0A" };
        Calculador425.Calcular(new Entrada425([new GrupoIva("EMITIDAS", false, "INTERIOR", 7, 0, false, 10000, 700, 0, 0)], 0, 0, []), d);
        var datos = new FicheroAtcDatos
        {
            Nif = Datos.Nif, Nombre = Datos.Nombre, SiglasVia = "CL", NombreVia = "Triana 1", Provincia = "35", Municipio = "35016", CodigoPostal = "35002",
            Obligado415 = true,
            Actividades = [new ActividadAtc("6599", 10000, 8000, 80, 82), new ActividadAtc("8331", 0, 0)],
        };
        var doc = XmlAtc.Modelo425(2026, d.Casillas, new DateOnly(2027, 1, 20), datos);
        Valida(doc, "425");
        var est = doc.Root!.Element("EST")!;
        Assert.Equal(("S", "N"), (est.Attribute("DTP")!.Value, est.Attribute("RECC")!.Value));
        var act = est.Elements("ACT").ToList();
        Assert.Equal(2, act.Count);
        Assert.Equal(("6599", "8000", "8200", "G", "1000000", "800000"), (act[0].Attribute("EPI")!.Value, act[0].Attribute("PRO")!.Value,
            act[0].Attribute("DEF")!.Value, act[0].Attribute("TIP")!.Value, act[0].Attribute("IMP")!.Value, act[0].Attribute("IMD")!.Value));
        Assert.Null(act[1].Attribute("PRO"));
    }

    [Fact]
    public void La_validacion_detecta_un_XML_incorrecto()
    {
        var doc = XmlAtc.Modelo420(2026, "3T", "ORDINARIA", Calcula420().Casillas, 225, false, Datos);
        doc.Root!.Element("RES")!.SetAttributeValue("TIP", "Z");
        doc.Root.Element("LIQ")!.SetAttributeValue("DIF", "12,5");
        var set = new XmlSchemaSet();
        set.Add(null, Path.Combine(AppContext.BaseDirectory, "Esquemas", "Presentacion-420-XMLSchema.xsd"));
        var errores = new List<string>();
        doc.Validate(set, (_, e) => errores.Add(e.Message));
        Assert.Equal(2, errores.Count);
    }

    [Fact]
    public void Sin_domicilio_no_hay_fichero()
        => Assert.Throws<ReglaContableException>(() => XmlAtc.Modelo420(2026, "3T", "ORDINARIA", Calcula420().Casillas, 0, false,
            new FicheroAtcDatos { Nif = "B1", Nombre = "X" }));
}
