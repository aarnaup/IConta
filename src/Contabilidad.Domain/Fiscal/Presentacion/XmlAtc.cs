using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Contabilidad.Domain.Fiscal.Presentacion;

/// <summary>Datos del declarante y de pago para los XML de la Agencia Tributaria Canaria.</summary>
public sealed class FicheroAtcDatos
{
    public string Nif { get; set; } = "";
    public string Nombre { get; set; } = "";
    /// <summary>Siglas de la vía pública (CL, AV…), 2 caracteres.</summary>
    public string SiglasVia { get; set; } = "CL";
    public string NombreVia { get; set; } = "";
    /// <summary>Código de provincia (35 Las Palmas, 38 Santa Cruz de Tenerife).</summary>
    public string Provincia { get; set; } = "";
    /// <summary>Código del municipio.</summary>
    public string Municipio { get; set; } = "";
    public string CodigoPostal { get; set; } = "";
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    /// <summary>Forma de pago del ingreso: 1 efectivo, 2 adeudo en cuenta, 3 pago fraccionado, 4 domiciliación, 5 pago telemático.</summary>
    public string FormaPago { get; set; } = "5";
    public string? Iban { get; set; }
    /// <summary>Justificante de la declaración que se complementa.</summary>
    public string? JustificanteAnterior { get; set; }
}

/// <summary>
/// XML de los modelos 420 y 425 de la Agencia Tributaria Canaria, según sus esquemas (Presentacion-420/425-XMLSchema.xsd), que es lo que
/// importa el programa de ayuda de la ATC para generar la presentación. Importes en céntimos sin separador ni ceros a la izquierda
/// (mínimo dos dígitos); porcentajes con dos decimales implícitos.
/// </summary>
public static class XmlAtc
{
    /// <summary>Casilla oficial del 420 de cada clave interna de la autoliquidación del IGIC.</summary>
    public static string? Casilla420(string clave)
    {
        foreach (var t in Calculador425.Tipos)
            foreach (var p in t.Pcts)
            {
                var k = p.ToString("0.##", CultureInfo.InvariantCulture);
                if (clave == $"D{k}B") return t.B;
                if (clave == $"D{k}C") return t.C;
            }
        return clave switch
        {
            "ISPB" => "19", "ISPC" => "20", "MODB" => "21", "MODC" => "22", "TDEV" => "25",
            "SCB" => "26", "SCC" => "27", "SIB" => "28", "SIC" => "29", "ICB" => "30", "ICC" => "31", "IIB" => "32", "IIC" => "33",
            "RDB" => "34", "RDC" => "35", "RBI" => "37", "REG" => "39", "TDED" => "40", "COMP" => "43", "DED" => "44", "LIQ" => "45",
            _ => null,
        };
    }

    public static string Importe(decimal v)
    {
        var c = (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero);
        var s = Math.Abs(c).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        return c < 0 ? "-" + s : s;
    }

    private static string Porcentaje(decimal p) => ((long)Math.Round(p * 100m)).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');

    private static string Mayus(string? s) => new string((s ?? "").Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD)
        .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark || ch == '̃').ToArray()).Normalize(NormalizationForm.FormC);

    private static XElement Identificacion(FicheroAtcDatos d)
    {
        if (string.IsNullOrWhiteSpace(d.NombreVia) || string.IsNullOrWhiteSpace(d.Municipio) || d.CodigoPostal.Length != 5 || d.Provincia.Length != 2)
            throw new ReglaContableException("Faltan datos del domicilio para la ATC: vía, código del municipio, código postal y provincia.",
                "Indícalos al generar el fichero (la dirección y el código postal se toman del NIF de la empresa).");
        var otp = new XElement("OTP", new XAttribute("NIF", d.Nif), new XAttribute("NRS", Mayus(d.Nombre)),
            new XAttribute("SVP", Mayus(d.SiglasVia).PadRight(2)[..2]), new XAttribute("NVP", Mayus(d.NombreVia)));
        if (!string.IsNullOrWhiteSpace(d.Telefono)) otp.Add(new XAttribute("TEL", d.Telefono.Trim()));
        if (!string.IsNullOrWhiteSpace(d.Email)) otp.Add(new XAttribute("EMA", d.Email.Trim()));
        otp.Add(new XAttribute("POP", d.Provincia), new XAttribute("CMU", d.Municipio), new XAttribute("CP", d.CodigoPostal), new XAttribute("PAI", "ES"));
        return new XElement("IDE", otp);
    }

    private static XElement? BaseCuota(string nombre, IReadOnlyDictionary<string, decimal> c, string? b, string cuota, decimal? tipo = null)
    {
        var bas = b is null ? 0 : c.GetValueOrDefault(b);
        var cuo = c.GetValueOrDefault(cuota);
        if (bas == 0 && cuo == 0) return null;
        var e = new XElement(nombre);
        if (b is not null) e.Add(new XAttribute("BAS", Importe(bas)));
        if (tipo is { } t) e.Add(new XAttribute("TIP", Porcentaje(t)));
        e.Add(new XAttribute("CUO", Importe(cuo)));
        return e;
    }

    private static XElement Resultado(decimal resultado, bool ultimoPeriodo, bool solicitaDevolucion, FicheroAtcDatos d)
    {
        var tip = resultado > 0 ? "I" : resultado == 0 ? "S" : ultimoPeriodo && solicitaDevolucion ? "D" : "C";
        var res = new XElement("RES", new XAttribute("TIP", tip), new XAttribute("IMP", Importe(Math.Abs(resultado))));
        if (tip == "I") res.Add(new XAttribute("FPA", d.FormaPago));
        var iban = (d.Iban ?? "").Replace(" ", "").ToUpperInvariant();
        if (iban.Length == 24 && (tip == "D" || (tip == "I" && d.FormaPago is "2" or "4"))) res.Add(new XAttribute("IBAN", iban));
        return res;
    }

    /// <summary>Modelo 420 (autoliquidación) con las casillas de la declaración guardada.</summary>
    public static XDocument Modelo420(int anio, string periodo, string tipoDeclaracion, IReadOnlyList<Casilla> casillas, decimal resultado,
        bool solicitaDevolucion, FicheroAtcDatos d)
    {
        var c = casillas.GroupBy(x => x.Clave).ToDictionary(g => g.Key, g => g.Sum(x => x.Importe));
        var dec = new XElement("DEC", new XAttribute("MOD", "420"), new XAttribute("ANY", anio.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("PER", periodo));
        if (tipoDeclaracion == "COMPLEMENTARIA")
        {
            dec.Add(new XAttribute("COM", "X"));
            if (!string.IsNullOrWhiteSpace(d.JustificanteAnterior)) dec.Add(new XAttribute("NJA", d.JustificanteAnterior.Trim()));
        }
        dec.Add(Identificacion(d));

        var dev = new XElement("IGI_DEV", new XAttribute("TOT", Importe(c.GetValueOrDefault("TDEV"))));
        foreach (var t in Calculador425.Tipos)
            foreach (var p in t.Pcts)
            {
                var k = p.ToString("0.##", CultureInfo.InvariantCulture);
                if (BaseCuota("DEV", c, $"D{k}B", $"D{k}C", p) is { } e) dev.Add(e);
            }
        foreach (var e in new[] { BaseCuota("OIN", c, "ISPB", "ISPC"), BaseCuota("MBC", c, "MODB", "MODC") }) if (e is not null) dev.Add(e);
        if (dev.HasElements || c.GetValueOrDefault("TDEV") != 0) dec.Add(dev);

        var ded = new XElement("IGI_DED", new XAttribute("TOT", Importe(c.GetValueOrDefault("TDED"))));
        foreach (var e in new[] { BaseCuota("OIC", c, "SCB", "SCC"), BaseCuota("OII", c, "SIB", "SIC"), BaseCuota("IMC", c, "ICB", "ICC"),
                                  BaseCuota("IMI", c, "IIB", "IIC"), BaseCuota("RED", c, "RDB", "RDC"), BaseCuota("RBI", c, null, "RBI"),
                                  BaseCuota("RPP", c, null, "REG") })
            if (e is not null) ded.Add(e);
        if (ded.HasElements || c.GetValueOrDefault("TDED") != 0) dec.Add(ded);

        var dif = c.GetValueOrDefault("TDEV") - c.GetValueOrDefault("TDED");
        dec.Add(new XElement("LIQ", new XAttribute("DIF", Importe(dif)), new XAttribute("RCU", "00"),
            new XAttribute("CPA", Importe(c.GetValueOrDefault("COMP"))), new XAttribute("DAC", Importe(c.GetValueOrDefault("DED"))),
            new XAttribute("RLI", Importe(resultado))));
        dec.Add(Resultado(resultado, periodo is "4T" or "12", solicitaDevolucion, d));
        return new XDocument(new XDeclaration("1.0", "ISO-8859-1", null), dec);
    }

    /// <summary>Modelo 425 (resumen anual) con las casillas oficiales que calcula <see cref="Calculador425"/>.</summary>
    public static XDocument Modelo425(int anio, IReadOnlyList<Casilla> casillas, DateOnly fecha, FicheroAtcDatos d)
    {
        var c = casillas.GroupBy(x => x.Clave).ToDictionary(g => g.Key, g => g.Sum(x => x.Importe));
        decimal C(string k) => c.GetValueOrDefault(k);
        var dec = new XElement("DEC", new XAttribute("MOD", "425"), new XAttribute("ANY", anio.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("PER", "0A"), new XAttribute("FIM", fecha.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        dec.Add(Identificacion(d));

        if (c.ContainsKey("95"))
        {
            var reg = new XElement("REG", new XAttribute("RES", Importe(C("95"))));
            var dev = new XElement("DEV", new XAttribute("TBA", Importe(C("74"))), new XAttribute("TCU", Importe(C("79"))));
            foreach (var t in Calculador425.Tipos)
                if (BaseCuota("RGO", c, t.B, t.C, C(t.T)) is { } e) dev.Add(e);
            foreach (var e in new[] { BaseCuota("MBC", c, "70", "71"), BaseCuota("OIN", c, "75", "76") }) if (e is not null) dev.Add(e);
            reg.Add(dev);
            if (C("94") != 0)
            {
                var ded = new XElement("DED", new XAttribute("TOT", Importe(C("94"))));
                foreach (var e in new[] { BaseCuota("OIC", c, "80", "81"), BaseCuota("OII", c, "82", "83"), BaseCuota("IMC", c, "84", "85"),
                                          BaseCuota("IMI", c, "86", "87"), BaseCuota("RED", c, "88", "89"), BaseCuota("RBI", c, null, "91"),
                                          BaseCuota("RPP", c, null, "93") })
                    if (e is not null) ded.Add(e);
                reg.Add(ded);
            }
            dec.Add(reg);
        }
        if (C("TRS") != 0)
            dec.Add(new XElement("RES", new XAttribute("RES", Importe(C("TRS"))),
                new XElement("CRS", new XAttribute("TRS", Importe(C("TRS"))), new XAttribute("TOT", Importe(C("TRS"))))));
        dec.Add(new XElement("LIQ", new XAttribute("RCU", Importe(C("112"))), new XAttribute("SUT", Importe(C("113"))),
            new XAttribute("CPA", Importe(C("114"))), new XAttribute("RLI", Importe(C("115")))));
        if (C("116") != 0 || C("117") != 0 || C("118") != 0 || C("119") != 0)
            dec.Add(new XElement("AUT", new XAttribute("TOA", Importe(C("116"))), new XAttribute("TOM", Importe(C("117"))),
                new XAttribute("IMC", Importe(C("118"))), new XAttribute("IMD", Importe(C("119")))));
        var ope = new XElement("OPE");
        foreach (var (k, a) in new[] { ("VREG", "REG"), ("VRES", "RES"), ("VEXP", "EXP"), ("VESD", "ESD"), ("VNSJ", "NSJ") })
            if (C(k) != 0) ope.Add(new XAttribute(a, Importe(C(k))));
        ope.Add(new XAttribute("TOT", Importe(C("120"))));
        dec.Add(ope);
        return new XDocument(new XDeclaration("1.0", "ISO-8859-1", null), dec);
    }

    /// <summary>Bytes en ISO-8859-1 (la codificación que declaran los esquemas de la ATC).</summary>
    public static byte[] Bytes(XDocument doc)
    {
        var latin1 = Encoding.Latin1;
        using var ms = new MemoryStream();
        using (var w = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings { Encoding = latin1, Indent = true }))
            doc.Save(w);
        return ms.ToArray();
    }
}
