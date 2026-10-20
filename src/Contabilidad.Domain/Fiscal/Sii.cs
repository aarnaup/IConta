using System.Globalization;
using System.Xml.Linq;

namespace Contabilidad.Domain.Fiscal;

/// <summary>Línea de IVA de una factura para el SII.</summary>
public sealed record LineaSii(string TipoOperacion, decimal Porcentaje, decimal Base, decimal Cuota, decimal PorcentajeRecargo,
    decimal CuotaRecargo, decimal CuotaDeducible, bool BienInversion);

/// <summary>Factura contabilizada con lo que el SII necesita.</summary>
public sealed record FacturaSii(
    long Id, string Libro, string? Serie, string Numero, DateOnly FechaExpedicion, DateOnly? FechaOperacion, DateOnly FechaContable,
    string TipoFactura, string? Descripcion, string Nif, string Nombre, string Pais, bool PaisUe, string TipoDocumento, decimal Total,
    bool CriterioCaja, bool Servicios, string? RectificadaNumero, DateOnly? RectificadaFecha, IReadOnlyList<LineaSii> Lineas,
    string? Estado = null)
{
    public string NumSerie => (Serie ?? "") + Numero;
}

/// <summary>Resultado de una factura en la respuesta de la AEAT.</summary>
public sealed record RespuestaLineaSii(string NumSerie, DateOnly? FechaExpedicion, string Estado, string? Codigo, string? Descripcion);

public sealed record RespuestaSii(string EstadoEnvio, string? Csv, IReadOnlyList<RespuestaLineaSii> Lineas, string? Fallo);

/// <summary>
/// Suministro Inmediato de Información del IVA (SII, versión 1.1): mensajes SOAP de alta (A0) o modificación (A1) de facturas
/// expedidas y recibidas, y lectura de la respuesta. Estructura según los esquemas SuministroLR.xsd y SuministroInformacion.xsd
/// de la AEAT; la AEAT admite hasta 10.000 facturas por envío.
/// </summary>
public static class Sii
{
    public const string Version = "1.1";
    public const int MaximoPorEnvio = 10_000;
    private const string Base = "https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/ssii/fact/ws/";
    public static readonly XNamespace SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Lr = Base + "SuministroLR.xsd";
    public static readonly XNamespace Inf = Base + "SuministroInformacion.xsd";
    public static readonly XNamespace Resp = Base + "RespuestaSuministro.xsd";

    /// <summary>Direcciones del servicio web (entorno de pruebas o producción; certificado de representante o de sello).</summary>
    public static string Direccion(string libro, bool pruebas, bool sello = false)
    {
        var host = (pruebas, sello) switch
        {
            (true, false) => "https://prewww1.aeat.es",
            (true, true) => "https://prewww10.aeat.es",
            (false, false) => "https://www1.agenciatributaria.gob.es",
            (false, true) => "https://www10.agenciatributaria.gob.es",
        };
        return libro == "EMITIDAS" ? $"{host}/wlpl/SSII-FACT/ws/fe/SiiFactFEV1SOAP" : $"{host}/wlpl/SSII-FACT/ws/fr/SiiFactFRV1SOAP";
    }

    private static string Importe(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
    private static string Pct(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Fecha(DateOnly d) => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static string Corta(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    public static string NifEspanol(string nif)
    {
        var n = new string((nif ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return n.StartsWith("ES", StringComparison.Ordinal) && n.Length == 11 ? n[2..] : n;
    }

    /// <summary>Mensaje de alta o modificación de facturas de un libro.</summary>
    public static XDocument Mensaje(string libro, string nifTitular, string nombreTitular, string tipoComunicacion, IReadOnlyList<FacturaSii> facturas)
    {
        if (libro is not ("EMITIDAS" or "RECIBIDAS")) throw new ReglaContableException("Libro no válido: EMITIDAS o RECIBIDAS.");
        if (tipoComunicacion is not ("A0" or "A1")) throw new ReglaContableException("Tipo de comunicación no válido: A0 alta o A1 modificación.");
        if (facturas.Count == 0) throw new ReglaContableException("No hay facturas que enviar.");
        if (facturas.Count > MaximoPorEnvio) throw new ReglaContableException($"Un envío al SII admite como mucho {MaximoPorEnvio} facturas.");
        var emitidas = libro == "EMITIDAS";
        var raiz = new XElement(Lr + (emitidas ? "SuministroLRFacturasEmitidas" : "SuministroLRFacturasRecibidas"),
            new XElement(Inf + "Cabecera",
                new XElement(Inf + "IDVersionSii", Version),
                new XElement(Inf + "Titular",
                    new XElement(Inf + "NombreRazon", Corta(nombreTitular, 120)),
                    new XElement(Inf + "NIF", NifEspanol(nifTitular))),
                new XElement(Inf + "TipoComunicacion", tipoComunicacion)));
        foreach (var f in facturas)
            raiz.Add(emitidas ? Emitida(f, nifTitular) : Recibida(f));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(SoapEnv + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", SoapEnv), new XAttribute(XNamespace.Xmlns + "siiLR", Lr),
                new XAttribute(XNamespace.Xmlns + "sii", Inf),
                new XElement(SoapEnv + "Header"),
                new XElement(SoapEnv + "Body", raiz)));
    }

    private static XElement Periodo(FacturaSii f)
        => new(Inf + "PeriodoLiquidacion",
            new XElement(Inf + "Ejercicio", f.FechaContable.Year.ToString(CultureInfo.InvariantCulture)),
            new XElement(Inf + "Periodo", f.FechaContable.Month.ToString("00", CultureInfo.InvariantCulture)));

    /// <summary>Identificación de un tercero: NIF español o IDOtro (02 NIF-IVA, 03 pasaporte, 04 documento del país de residencia).</summary>
    private static XElement[] Identificacion(FacturaSii f)
    {
        var pais = string.IsNullOrWhiteSpace(f.Pais) ? "ES" : f.Pais.ToUpperInvariant();
        if (pais == "ES") return [new XElement(Inf + "NIF", NifEspanol(f.Nif))];
        var tipo = f.TipoDocumento switch { "PASAPORTE" => "03", "VIES" => "02", _ => f.PaisUe ? "02" : "04" };
        var id = new string((f.Nif ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (tipo == "02" && !id.StartsWith(pais == "GR" ? "EL" : pais, StringComparison.Ordinal)) id = (pais == "GR" ? "EL" : pais) + id;
        return [new XElement(Inf + "IDOtro",
            new XElement(Inf + "CodigoPais", pais), new XElement(Inf + "IDType", tipo), new XElement(Inf + "ID", Corta(id, 20)))];
    }

    private static string TipoFactura(FacturaSii f) => f.TipoFactura switch
    {
        "SIMPLIFICADA" => "F2",
        "RECTIFICATIVA" => "R1",
        "IMPORTACION" => "F5",
        _ => "F1",
    };

    private static IEnumerable<XElement> Rectificacion(FacturaSii f, string tipo)
    {
        if (!tipo.StartsWith('R')) yield break;
        yield return new XElement(Inf + "TipoRectificativa", "I");
        if (f.RectificadaNumero is { } n && f.RectificadaFecha is { } fe)
            yield return new XElement(Inf + "FacturasRectificadas",
                new XElement(Inf + "IDFacturaRectificada",
                    new XElement(Inf + "NumSerieFacturaEmisor", Corta(n, 60)),
                    new XElement(Inf + "FechaExpedicionFacturaEmisor", Fecha(fe))));
    }

    private static string Descripcion(FacturaSii f) => Corta(string.IsNullOrWhiteSpace(f.Descripcion) ? $"Factura {f.NumSerie}" : f.Descripcion.Trim(), 500);

    private static XElement Emitida(FacturaSii f, string nifTitular)
    {
        var tipo = TipoFactura(f);
        var exterior = (f.Pais ?? "ES").ToUpperInvariant() != "ES";
        var clave = f.Lineas.Any(l => l.TipoOperacion == "EXPORTACION") ? "02" : f.CriterioCaja ? "07" : "01";
        var detalle = new XElement(Lr + "FacturaExpedida", new XElement(Inf + "TipoFactura", tipo));
        detalle.Add(Rectificacion(f, tipo));
        if (f.FechaOperacion is { } fo && fo != f.FechaExpedicion) detalle.Add(new XElement(Inf + "FechaOperacion", Fecha(fo)));
        detalle.Add(new XElement(Inf + "ClaveRegimenEspecialOTrascendencia", clave),
            new XElement(Inf + "ImporteTotal", Importe(f.Total)),
            new XElement(Inf + "DescripcionOperacion", Descripcion(f)));
        if (tipo != "F2")
            detalle.Add(new XElement(Inf + "Contraparte", new XElement(Inf + "NombreRazon", Corta(f.Nombre, 120)), Identificacion(f)));

        var desglose = DesgloseEmitida(f.Lineas);
        // Con destinatario extranjero el desglose va por tipo de operación (entrega de bienes o prestación de servicios)
        var tipoDesglose = exterior
            ? new XElement(Inf + "TipoDesglose", new XElement(Inf + "DesgloseTipoOperacion",
                new XElement(Inf + (f.Servicios ? "PrestacionServicios" : "Entrega"), desglose.Elements())))
            : new XElement(Inf + "TipoDesglose", new XElement(Inf + "DesgloseFactura", desglose.Elements()));
        detalle.Add(tipoDesglose);

        return new XElement(Lr + "RegistroLRFacturasEmitidas", Periodo(f),
            new XElement(Lr + "IDFactura",
                new XElement(Inf + "IDEmisorFactura", new XElement(Inf + "NIF", NifEspanol(nifTitular))),
                new XElement(Inf + "NumSerieFacturaEmisor", Corta(f.NumSerie, 60)),
                new XElement(Inf + "FechaExpedicionFacturaEmisor", Fecha(f.FechaExpedicion))),
            detalle);
    }

    /// <summary>Sujeta (exenta o no exenta) y no sujeta, a partir de las líneas.</summary>
    private static XElement DesgloseEmitida(IReadOnlyList<LineaSii> lineas)
    {
        var d = new XElement("d");
        var exentas = lineas.Where(l => l.TipoOperacion is "EXENTA" or "EXPORTACION" or "INTRACOM_ENTREGA")
            .GroupBy(l => l.TipoOperacion switch { "EXPORTACION" => "E2", "INTRACOM_ENTREGA" => "E5", _ => "E1" }).ToList();
        var noExentas = lineas.Where(l => l.TipoOperacion is "INTERIOR" or "INVERSION_SUJETO_PASIVO").ToList();
        if (exentas.Count > 0 || noExentas.Count > 0)
        {
            var sujeta = new XElement(Inf + "Sujeta");
            if (exentas.Count > 0)
                sujeta.Add(new XElement(Inf + "Exenta", exentas.Take(7).Select(g => new XElement(Inf + "DetalleExenta",
                    new XElement(Inf + "CausaExencion", g.Key), new XElement(Inf + "BaseImponible", Importe(g.Sum(l => l.Base)))))));
            if (noExentas.Count > 0)
            {
                var isp = noExentas.All(l => l.TipoOperacion == "INVERSION_SUJETO_PASIVO");
                var porTipo = noExentas.GroupBy(l => (isp ? 0m : l.Porcentaje, isp ? 0m : l.PorcentajeRecargo));
                sujeta.Add(new XElement(Inf + "NoExenta",
                    new XElement(Inf + "TipoNoExenta", isp ? "S2" : "S1"),
                    new XElement(Inf + "DesgloseIVA", porTipo.Select(g =>
                    {
                        var e = new XElement(Inf + "DetalleIVA",
                            new XElement(Inf + "TipoImpositivo", Pct(g.Key.Item1)),
                            new XElement(Inf + "BaseImponible", Importe(g.Sum(l => l.Base))),
                            new XElement(Inf + "CuotaRepercutida", Importe(isp ? 0 : g.Sum(l => l.Cuota))));
                        if (g.Key.Item2 != 0)
                            e.Add(new XElement(Inf + "TipoRecargoEquivalencia", Pct(g.Key.Item2)),
                                new XElement(Inf + "CuotaRecargoEquivalencia", Importe(g.Sum(l => l.CuotaRecargo))));
                        return e;
                    }))));
            }
            d.Add(sujeta);
        }
        // No sujeta: por los arts. 7 y 14 (y otros) o por las reglas de localización, en este orden
        var art7 = lineas.Where(l => l.TipoOperacion == "NO_SUJETA").ToList();
        var localizacion = lineas.Where(l => l.TipoOperacion is "NO_SUJETA_LOCAL" or "NO_SUJETA_OSS").ToList();
        if (art7.Count > 0 || localizacion.Count > 0)
        {
            var ns = new XElement(Inf + "NoSujeta");
            if (art7.Count > 0) ns.Add(new XElement(Inf + "ImportePorArticulos7_14_Otros", Importe(art7.Sum(l => l.Base))));
            if (localizacion.Count > 0) ns.Add(new XElement(Inf + "ImporteTAIReglasLocalizacion", Importe(localizacion.Sum(l => l.Base))));
            d.Add(ns);
        }
        return d;
    }

    private static XElement Recibida(FacturaSii f)
    {
        var tipo = TipoFactura(f);
        var intracom = f.Lineas.Any(l => l.TipoOperacion is "INTRACOM_ADQUISICION" or "INTRACOM_SERVICIOS");
        // Clave 02: compras a agricultores, ganaderos o pescadores en régimen especial (el justificante es el recibo de compensación: F6)
        var reagp = f.Lineas.Count > 0 && f.Lineas.All(l => l.TipoOperacion == "COMPENSACION_REAGP");
        var clave = reagp ? "02" : intracom ? "09" : f.CriterioCaja ? "07" : "01";
        if (reagp && tipo == "F1") tipo = "F6";
        var ident = Identificacion(f);
        var detalle = new XElement(Lr + "FacturaRecibida", new XElement(Inf + "TipoFactura", tipo));
        detalle.Add(Rectificacion(f, tipo));
        if (f.FechaOperacion is { } fo && fo != f.FechaExpedicion) detalle.Add(new XElement(Inf + "FechaOperacion", Fecha(fo)));
        detalle.Add(new XElement(Inf + "ClaveRegimenEspecialOTrascendencia", clave),
            new XElement(Inf + "ImporteTotal", Importe(f.Total)),
            new XElement(Inf + "DescripcionOperacion", Descripcion(f)));

        var desglose = new XElement(Inf + "DesgloseFactura");
        XElement Detalle(IGrouping<(decimal Pct, decimal Rec), LineaSii> g, bool conCuota)
        {
            var e = new XElement(Inf + "DetalleIVA");
            if (conCuota) e.Add(new XElement(Inf + "TipoImpositivo", Pct(g.Key.Pct)));
            e.Add(new XElement(Inf + "BaseImponible", Importe(g.Sum(l => l.Base))));
            if (conCuota) e.Add(new XElement(Inf + "CuotaSoportada", Importe(g.Sum(l => l.Cuota))));
            if (g.Key.Rec != 0)
                e.Add(new XElement(Inf + "TipoRecargoEquivalencia", Pct(g.Key.Rec)),
                    new XElement(Inf + "CuotaRecargoEquivalencia", Importe(g.Sum(l => l.CuotaRecargo))));
            return e;
        }
        var isp = f.Lineas.Where(l => l.TipoOperacion == "INVERSION_SUJETO_PASIVO").ToList();
        var compensaciones = f.Lineas.Where(l => l.TipoOperacion == "COMPENSACION_REAGP").ToList();
        var resto = f.Lineas.Where(l => l.TipoOperacion is not ("INVERSION_SUJETO_PASIVO" or "COMPENSACION_REAGP")).ToList();
        if (isp.Count > 0)
            desglose.Add(new XElement(Inf + "InversionSujetoPasivo",
                isp.GroupBy(l => (l.Porcentaje, l.PorcentajeRecargo)).Select(g => Detalle(g, true))));
        if (resto.Count > 0)
            desglose.Add(new XElement(Inf + "DesgloseIVA",
                resto.GroupBy(l => (l.TipoOperacion is "EXENTA" or "NO_SUJETA" or "NO_SUJETA_LOCAL" or "NO_SUJETA_OSS" ? -1m : l.Porcentaje, l.PorcentajeRecargo))
                    .Select(g => Detalle(g, g.Key.Item1 >= 0))));
        if (compensaciones.Count > 0)
        {
            var dg = desglose.Element(Inf + "DesgloseIVA") ?? new XElement(Inf + "DesgloseIVA");
            if (dg.Parent is null) desglose.Add(dg);
            dg.Add(compensaciones.GroupBy(l => l.Porcentaje).Select(g => new XElement(Inf + "DetalleIVA",
                new XElement(Inf + "BaseImponible", Importe(g.Sum(l => l.Base))),
                new XElement(Inf + "PorcentCompensacionREAGYP", Pct(g.Key)),
                new XElement(Inf + "ImporteCompensacionREAGYP", Importe(g.Sum(l => l.Cuota))))));
        }
        detalle.Add(desglose,
            new XElement(Inf + "Contraparte", new XElement(Inf + "NombreRazon", Corta(f.Nombre, 120)), ident),
            new XElement(Inf + "FechaRegContable", Fecha(f.FechaContable)),
            new XElement(Inf + "CuotaDeducible", Importe(f.Lineas.Sum(l => l.CuotaDeducible))));

        return new XElement(Lr + "RegistroLRFacturasRecibidas", Periodo(f),
            new XElement(Lr + "IDFactura",
                new XElement(Inf + "IDEmisorFactura", Identificacion(f)),
                new XElement(Inf + "NumSerieFacturaEmisor", Corta(f.NumSerie, 60)),
                new XElement(Inf + "FechaExpedicionFacturaEmisor", Fecha(f.FechaExpedicion))),
            detalle);
    }

    /// <summary>Respuesta de la AEAT (o un fallo SOAP). Se lee por nombre local para no depender de los prefijos.</summary>
    public static RespuestaSii LeerRespuesta(string xml)
    {
        var doc = XDocument.Parse(xml);
        var fallo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fallo is not null)
            return new RespuestaSii("Fallo", null, [], fallo.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value ?? fallo.Value);
        string? Texto(XElement e, string nombre) => e.Elements().FirstOrDefault(x => x.Name.LocalName == nombre)?.Value;
        var raiz = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.StartsWith("RespuestaLR", StringComparison.Ordinal))
                   ?? throw new ReglaContableException("La respuesta no es del SII.");
        var lineas = raiz.Elements().Where(e => e.Name.LocalName == "RespuestaLinea").Select(l =>
        {
            var id = l.Elements().FirstOrDefault(x => x.Name.LocalName == "IDFactura");
            var num = id?.Descendants().FirstOrDefault(x => x.Name.LocalName == "NumSerieFacturaEmisor")?.Value ?? "";
            var fe = id?.Descendants().FirstOrDefault(x => x.Name.LocalName == "FechaExpedicionFacturaEmisor")?.Value;
            DateOnly? fecha = DateOnly.TryParseExact(fe, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : null;
            return new RespuestaLineaSii(num, fecha, Texto(l, "EstadoRegistro") ?? "", Texto(l, "CodigoErrorRegistro"), Texto(l, "DescripcionErrorRegistro"));
        }).ToList();
        return new RespuestaSii(Texto(raiz, "EstadoEnvio") ?? "", Texto(raiz, "CSV"), lineas, null);
    }

    /// <summary>Estado de la factura según la respuesta: ACEPTADA, ACEPTADA_CON_ERRORES o RECHAZADA.</summary>
    public static string Estado(string estadoRegistro) => estadoRegistro switch
    {
        "Correcto" => "ACEPTADA",
        "AceptadoConErrores" => "ACEPTADA_CON_ERRORES",
        _ => "RECHAZADA",
    };
}
