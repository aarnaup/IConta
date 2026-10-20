using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;

namespace Contabilidad.Tests;

public class SepaTests
{
    private static readonly OrdenanteSepa Empresa = new("Demo Canarias, S.L.", "B12345678", "000", "ES9121000418450200051332", "CAIXESBBXXX");
    private static readonly DateTime Creado = new(2026, 10, 20, 12, 0, 0);

    private static OperacionSepa Op(string id, decimal importe, string secuencia = "RCUR", string? bic = null)
        => new(id, importe, "Peña & Hijos, S.A.", "ES7921000813610123456789", bic, "Factura F-1 (julio)", "MND-0001", new DateOnly(2025, 1, 15), secuencia);

    [Fact]
    public void Identificador_de_acreedor_con_control_mod_97()
    {
        var id = Sepa.IdentificadorAcreedor("B12345678");
        Assert.Matches("^ES[0-9]{2}000B12345678$", id);
        // Comprobación inversa: NIF + país + control deja resto 1, como un IBAN
        Assert.Equal(1, Sepa.Mod97("B12345678" + "ES" + id[2..4]));
        Assert.Equal(id[..4], Sepa.IdentificadorAcreedor("B-12345678", "001")[..4]);   // el sufijo y los guiones no cambian el control
        Assert.EndsWith("001B12345678", Sepa.IdentificadorAcreedor("B12345678", "001"));
    }

    [Theory]
    [InlineData("Peña & Hijos, S.A.", "Pena + Hijos, S.A.")]
    [InlineData("Ferretería Ñandú; nº 5", "Ferreteria Nandu n 5")]
    [InlineData("  varios   espacios  ", "varios espacios")]
    public void Texto_en_el_juego_de_caracteres_sepa(string entrada, string esperado)
        => Assert.Equal(esperado, Sepa.Texto(entrada, 70));

    [Fact]
    public void Texto_se_recorta_a_la_longitud_maxima()
        => Assert.Equal(35, Sepa.Texto(new string('A', 50), 35).Length);

    [Fact]
    public void Adeudos_pain_008_con_un_bloque_por_secuencia()
    {
        var xml = Sepa.Adeudos(Empresa, "REM-1", Creado, new DateOnly(2026, 10, 25), "CORE",
            [Op("E1", 100m, "FRST"), Op("E2", 50.5m), Op("E3", 10m)]);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "urn:iso:std:iso:20022:tech:xsd:pain.008.001.02";
        var cab = doc.Root!.Element(ns + "CstmrDrctDbtInitn")!.Element(ns + "GrpHdr")!;
        Assert.Equal("3", cab.Element(ns + "NbOfTxs")!.Value);
        Assert.Equal("160.50", cab.Element(ns + "CtrlSum")!.Value);
        var bloques = doc.Descendants(ns + "PmtInf").ToList();
        Assert.Equal(["FRST", "RCUR"], bloques.Select(b => b.Descendants(ns + "SeqTp").Single().Value));
        Assert.Equal(["1", "2"], bloques.Select(b => b.Element(ns + "NbOfTxs")!.Value));
        Assert.Equal("2026-10-25", bloques[0].Element(ns + "ReqdColltnDt")!.Value);
        Assert.Equal(Empresa.IdentificadorAcreedor, bloques[0].Descendants(ns + "CdtrSchmeId").Descendants(ns + "Id").Last().Value);
        var tx = doc.Descendants(ns + "DrctDbtTxInf").First();
        Assert.Equal("MND-0001", tx.Descendants(ns + "MndtId").Single().Value);
        Assert.Equal("2025-01-15", tx.Descendants(ns + "DtOfSgntr").Single().Value);
        Assert.Equal("NOTPROVIDED", tx.Element(ns + "DbtrAgt")!.Descendants(ns + "Id").Single().Value);   // sin BIC
        Assert.Equal("Pena + Hijos, S.A.", tx.Element(ns + "Dbtr")!.Value);
        Assert.Equal("EUR", tx.Element(ns + "InstdAmt")!.Attribute("Ccy")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transferencias_pain_001()
    {
        var xml = Sepa.Transferencias(Empresa, "PAG-1", Creado, new DateOnly(2026, 10, 25), [Op("T1", 428m, bic: "BBVAESMMXXX"), Op("T2", 2140m)]);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";
        Assert.Equal("2568.00", doc.Descendants(ns + "CtrlSum").First().Value);
        Assert.Equal("B12345678000", doc.Descendants(ns + "InitgPty").Descendants(ns + "Id").Last().Value);
        var txs = doc.Descendants(ns + "CdtTrfTxInf").ToList();
        Assert.Equal(2, txs.Count);
        Assert.Equal("BBVAESMMXXX", txs[0].Element(ns + "CdtrAgt")!.Descendants(ns + "BIC").Single().Value);
        Assert.Null(txs[1].Element(ns + "CdtrAgt"));                                    // sin BIC se omite
        Assert.Equal("ES9121000418450200051332", doc.Descendants(ns + "DbtrAcct").Single().Value);
    }

    // Validación contra los esquemas oficiales ISO 20022 (copiados en Esquemas/)
    private static void ValidarEsquema(string xml, string esquema)
    {
        var schemas = new XmlSchemaSet();
        schemas.Add(null, Path.Combine(AppContext.BaseDirectory, "Esquemas", esquema));
        var errores = new List<string>();
        XDocument.Parse(xml).Validate(schemas, (_, e) => errores.Add(e.Message));
        Assert.True(errores.Count == 0, string.Join(" | ", errores));
    }

    [Fact]
    public void Los_adeudos_cumplen_el_esquema_pain_008_001_02()
        => ValidarEsquema(Sepa.Adeudos(Empresa with { Bic = null }, "REM-1", Creado, new DateOnly(2026, 10, 25), "CORE",
            [Op("E1", 100m, "FRST"), Op("E2", 50.5m, bic: "BBVAESMMXXX"), Op("E3", 0.01m, "OOFF")]), "pain.008.001.02.xsd");

    [Fact]
    public void Las_transferencias_cumplen_el_esquema_pain_001_001_03()
        => ValidarEsquema(Sepa.Transferencias(Empresa, "PAG-1", Creado, new DateOnly(2026, 10, 25),
            [Op("T1", 428m, bic: "BBVAESMMXXX"), Op("T2", 2140m)]), "pain.001.001.03.xsd");

    [Fact]
    public void Validaciones()
    {
        Assert.Throws<ReglaContableException>(() => Sepa.Transferencias(Empresa, "X", Creado, new DateOnly(2026, 1, 1), []));
        Assert.Throws<ReglaContableException>(() => Sepa.Transferencias(Empresa, "X", Creado, new DateOnly(2026, 1, 1), [Op("T1", 1m) with { Iban = "ES0000000000000000000000" }]));
        Assert.Throws<ReglaContableException>(() => Sepa.Transferencias(Empresa, "X", Creado, new DateOnly(2026, 1, 1), [Op("T1", 1m), Op("T1", 2m)]));
        Assert.Throws<ReglaContableException>(() => Sepa.Adeudos(Empresa, "X", Creado, new DateOnly(2026, 1, 1), "CORE", [Op("E1", 1m) with { MandatoReferencia = null }]));
    }
}
