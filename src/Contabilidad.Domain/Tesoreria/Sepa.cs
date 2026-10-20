using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Contabilidad.Domain.Tesoreria;

/// <summary>Empresa que presenta la remesa.</summary>
public sealed record OrdenanteSepa(string Nombre, string Nif, string Sufijo, string Iban, string? Bic)
{
    /// <summary>Identificador de acreedor SEPA (AT-02).</summary>
    public string IdentificadorAcreedor => Sepa.IdentificadorAcreedor(Nif, Sufijo);
}

/// <summary>Un recibo (adeudo) o un pago (transferencia) de la remesa.</summary>
public sealed record OperacionSepa(string EndToEndId, decimal Importe, string Nombre, string Iban, string? Bic, string Concepto,
    string? MandatoReferencia = null, DateOnly? MandatoFirma = null, string Secuencia = "RCUR");

/// <summary>Ficheros SEPA en XML (ISO 20022) tal como los admiten los bancos españoles.</summary>
public static class Sepa
{
    private const string NsAdeudos = "urn:iso:std:iso:20022:tech:xsd:pain.008.001.02";
    private const string NsTransferencias = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    /// <summary>
    /// Identificador de acreedor (AT-02): país + 2 dígitos de control + sufijo (3) + NIF. El control se calcula como el del IBAN
    /// (ISO 7064 mod 97-10) sobre el NIF seguido de «ES00»; el sufijo no interviene.
    /// </summary>
    public static string IdentificadorAcreedor(string nif, string sufijo = "000", string pais = "ES")
    {
        var n = Limpiar(nif).ToUpperInvariant();
        if (n.Length == 0) throw new ReglaContableException("La empresa no tiene NIF: hace falta para el identificador de acreedor SEPA.");
        var control = 98 - Mod97(n + pais + "00");
        return $"{pais}{control:00}{(sufijo ?? "000").PadLeft(3, '0')[..3]}{n}";
    }

    /// <summary>Resto ISO 7064 mod 97-10 (letras A=10 … Z=35), el mismo cálculo que el del IBAN.</summary>
    public static int Mod97(string s)
    {
        var resto = 0;
        foreach (var c in s)
        {
            var v = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetterUpper(c) ? c - 'A' + 10 : throw new ReglaContableException($"Carácter no válido en «{s}».");
            resto = v < 10 ? (resto * 10 + v) % 97 : (resto * 100 + v) % 97;
        }
        return resto;
    }

    /// <summary>
    /// Juego de caracteres SEPA: letras sin acentos, dígitos y / - ? : ( ) . , ' + y espacio. Lo demás se translitera o se quita.
    /// </summary>
    public static string Texto(string? s, int max)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;     // acentos y la tilde de la ñ
            if (char.IsAsciiLetterOrDigit(c) || " /-?:().,'+".Contains(c)) sb.Append(c);
            else if (c == '&') sb.Append('+');
            else if (char.IsWhiteSpace(c) || c is '_' or ';' or '"') sb.Append(' ');
        }
        var t = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    private static string Limpiar(string s) => new(s.Where(char.IsAsciiLetterOrDigit).ToArray());
    private static string Importe(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Fecha(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Validar(OrdenanteSepa o, IReadOnlyList<OperacionSepa> ops)
    {
        if (ops.Count == 0) throw new ReglaContableException("La remesa no tiene operaciones.");
        if (!Iban.EsValido(o.Iban)) throw new ReglaContableException("El IBAN de la cuenta de la empresa no es válido.");
        foreach (var op in ops)
        {
            if (op.Importe <= 0 || op.Importe != Math.Round(op.Importe, 2)) throw new ReglaContableException($"Importe no válido en {op.EndToEndId}.");
            if (!Iban.EsValido(op.Iban)) throw new ReglaContableException($"El IBAN de {op.Nombre} no es válido.");
        }
        if (ops.Select(x => x.EndToEndId).Distinct().Count() != ops.Count) throw new ReglaContableException("Hay referencias de operación repetidas.");
    }

    private static XElement Agente(XNamespace ns, string nombre, string? bic)
        => new(ns + nombre, new XElement(ns + "FinInstnId",
            string.IsNullOrWhiteSpace(bic) ? new XElement(ns + "Othr", new XElement(ns + "Id", "NOTPROVIDED")) : new XElement(ns + "BIC", bic.Trim().ToUpperInvariant())));

    private static XElement Cuenta(XNamespace ns, string nombre, string iban)
        => new(ns + nombre, new XElement(ns + "Id", new XElement(ns + "IBAN", Iban.Normalizar(iban))));

    private static XElement Cabecera(XNamespace ns, string msgId, DateTime creado, IReadOnlyList<OperacionSepa> ops, OrdenanteSepa o, string idOrdenante)
        => new(ns + "GrpHdr",
            new XElement(ns + "MsgId", Texto(msgId, 35)),
            new XElement(ns + "CreDtTm", creado.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(ns + "NbOfTxs", ops.Count),
            new XElement(ns + "CtrlSum", Importe(ops.Sum(x => x.Importe))),
            new XElement(ns + "InitgPty", new XElement(ns + "Nm", Texto(o.Nombre, 70)),
                new XElement(ns + "Id", new XElement(ns + "OrgId", new XElement(ns + "Othr", new XElement(ns + "Id", idOrdenante))))));

    private static string Serializar(XDocument doc)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriterUtf8(sb), new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            doc.Save(w);
        return sb.ToString();
    }

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    /// <summary>
    /// Adeudos directos SEPA (pain.008.001.02). Un bloque de pago por secuencia (primer adeudo FRST, recurrentes RCUR, único OOFF),
    /// porque la secuencia va a nivel de bloque.
    /// </summary>
    public static string Adeudos(OrdenanteSepa o, string msgId, DateTime creado, DateOnly fechaCobro, string esquema, IReadOnlyList<OperacionSepa> ops)
    {
        Validar(o, ops);
        if (ops.Any(x => string.IsNullOrWhiteSpace(x.MandatoReferencia) || x.MandatoFirma is null))
            throw new ReglaContableException("Todos los recibos necesitan un mandato con referencia y fecha de firma.");
        XNamespace ns = NsAdeudos;
        var idAcreedor = o.IdentificadorAcreedor;
        var bloques = ops.GroupBy(x => x.Secuencia).OrderBy(g => g.Key == "FRST" ? 0 : g.Key == "RCUR" ? 1 : 2).Select((g, i) =>
        {
            var lista = g.ToList();
            return new XElement(ns + "PmtInf",
                new XElement(ns + "PmtInfId", Texto($"{msgId}-{i + 1}", 35)),
                new XElement(ns + "PmtMtd", "DD"),
                new XElement(ns + "BtchBookg", "true"),
                new XElement(ns + "NbOfTxs", lista.Count),
                new XElement(ns + "CtrlSum", Importe(lista.Sum(x => x.Importe))),
                new XElement(ns + "PmtTpInf",
                    new XElement(ns + "SvcLvl", new XElement(ns + "Cd", "SEPA")),
                    new XElement(ns + "LclInstrm", new XElement(ns + "Cd", esquema)),
                    new XElement(ns + "SeqTp", g.Key)),
                new XElement(ns + "ReqdColltnDt", Fecha(fechaCobro)),
                new XElement(ns + "Cdtr", new XElement(ns + "Nm", Texto(o.Nombre, 70))),
                Cuenta(ns, "CdtrAcct", o.Iban),
                Agente(ns, "CdtrAgt", o.Bic),
                new XElement(ns + "ChrgBr", "SLEV"),
                new XElement(ns + "CdtrSchmeId", new XElement(ns + "Id", new XElement(ns + "PrvtId", new XElement(ns + "Othr",
                    new XElement(ns + "Id", idAcreedor), new XElement(ns + "SchmeNm", new XElement(ns + "Prtry", "SEPA")))))),
                lista.Select(op => new XElement(ns + "DrctDbtTxInf",
                    new XElement(ns + "PmtId", new XElement(ns + "EndToEndId", Texto(op.EndToEndId, 35))),
                    new XElement(ns + "InstdAmt", new XAttribute("Ccy", "EUR"), Importe(op.Importe)),
                    new XElement(ns + "DrctDbtTx", new XElement(ns + "MndtRltdInf",
                        new XElement(ns + "MndtId", Texto(op.MandatoReferencia, 35)),
                        new XElement(ns + "DtOfSgntr", Fecha(op.MandatoFirma!.Value)))),
                    Agente(ns, "DbtrAgt", op.Bic),
                    new XElement(ns + "Dbtr", new XElement(ns + "Nm", Texto(op.Nombre, 70))),
                    Cuenta(ns, "DbtrAcct", op.Iban),
                    new XElement(ns + "RmtInf", new XElement(ns + "Ustrd", Texto(op.Concepto, 140))))));
        });
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(ns + "Document",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            new XElement(ns + "CstmrDrctDbtInitn", Cabecera(ns, msgId, creado, ops, o, idAcreedor), bloques)));
        return Serializar(doc);
    }

    /// <summary>Transferencias SEPA (pain.001.001.03). El ordenante se identifica con NIF + sufijo.</summary>
    public static string Transferencias(OrdenanteSepa o, string msgId, DateTime creado, DateOnly fechaEjecucion, IReadOnlyList<OperacionSepa> ops)
    {
        Validar(o, ops);
        XNamespace ns = NsTransferencias;
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(ns + "Document",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            new XElement(ns + "CstmrCdtTrfInitn",
                Cabecera(ns, msgId, creado, ops, o, Limpiar(o.Nif).ToUpperInvariant() + o.Sufijo),
                new XElement(ns + "PmtInf",
                    new XElement(ns + "PmtInfId", Texto($"{msgId}-1", 35)),
                    new XElement(ns + "PmtMtd", "TRF"),
                    new XElement(ns + "BtchBookg", "true"),
                    new XElement(ns + "NbOfTxs", ops.Count),
                    new XElement(ns + "CtrlSum", Importe(ops.Sum(x => x.Importe))),
                    new XElement(ns + "PmtTpInf", new XElement(ns + "SvcLvl", new XElement(ns + "Cd", "SEPA"))),
                    new XElement(ns + "ReqdExctnDt", Fecha(fechaEjecucion)),
                    new XElement(ns + "Dbtr", new XElement(ns + "Nm", Texto(o.Nombre, 70))),
                    Cuenta(ns, "DbtrAcct", o.Iban),
                    Agente(ns, "DbtrAgt", o.Bic),
                    new XElement(ns + "ChrgBr", "SLEV"),
                    ops.Select(op => new XElement(ns + "CdtTrfTxInf",
                        new XElement(ns + "PmtId", new XElement(ns + "EndToEndId", Texto(op.EndToEndId, 35))),
                        new XElement(ns + "Amt", new XElement(ns + "InstdAmt", new XAttribute("Ccy", "EUR"), Importe(op.Importe))),
                        string.IsNullOrWhiteSpace(op.Bic) ? null : Agente(ns, "CdtrAgt", op.Bic),
                        new XElement(ns + "Cdtr", new XElement(ns + "Nm", Texto(op.Nombre, 70))),
                        Cuenta(ns, "CdtrAcct", op.Iban),
                        new XElement(ns + "RmtInf", new XElement(ns + "Ustrd", Texto(op.Concepto, 140)))))))));
        return Serializar(doc);
    }
}
