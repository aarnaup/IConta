using System.Xml.Linq;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class SiiTests
{
    private static readonly XNamespace I = Sii.Inf, L = Sii.Lr;

    private static FacturaSii F(string libro, string numero, string nif, string pais, IReadOnlyList<LineaSii> lineas, string tipo = "COMPLETA",
        bool ue = false, bool servicios = false, string? rect = null, bool caja = false)
        => new(1, libro, "A", numero, new DateOnly(2026, 3, 10), null, new DateOnly(2026, 3, 12), tipo, "Venta de material", nif, "Cliente Ñandú",
            pais, ue, "NIF", lineas.Sum(l => l.Base + l.Cuota + l.CuotaRecargo), caja, servicios, rect, rect is null ? null : new DateOnly(2026, 1, 5), lineas);

    private static LineaSii L1(string tipo, decimal pct, decimal b, decimal rec = 0, bool bi = false)
        => new(tipo, pct, b, Math.Round(b * pct / 100, 2), rec, Math.Round(b * rec / 100, 2), Math.Round(b * pct / 100, 2), bi);

    [Fact]
    public void Emitida_interior_con_desglose_de_factura_y_recargo()
    {
        var doc = Sii.Mensaje("EMITIDAS", "ESB12345678", "Mi Empresa S.L.", "A0",
            [F("EMITIDAS", "001", "A11111111", "ES", [L1("INTERIOR", 21, 1000), L1("INTERIOR", 10, 200, rec: 1.4m)])]);
        var cab = doc.Descendants(I + "Cabecera").Single();
        Assert.Equal("1.1", cab.Element(I + "IDVersionSii")!.Value);
        Assert.Equal("B12345678", cab.Element(I + "Titular")!.Element(I + "NIF")!.Value);
        Assert.Equal("A0", cab.Element(I + "TipoComunicacion")!.Value);

        var reg = doc.Descendants(L + "RegistroLRFacturasEmitidas").Single();
        Assert.Equal(["PeriodoLiquidacion", "IDFactura", "FacturaExpedida"], reg.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("03", reg.Descendants(I + "Periodo").Single().Value);
        Assert.Equal("A001", reg.Descendants(I + "NumSerieFacturaEmisor").Single().Value);
        Assert.Equal("10-03-2026", reg.Descendants(I + "FechaExpedicionFacturaEmisor").Single().Value);
        var fe = reg.Element(L + "FacturaExpedida")!;
        Assert.Equal(["TipoFactura", "ClaveRegimenEspecialOTrascendencia", "ImporteTotal", "DescripcionOperacion", "Contraparte", "TipoDesglose"],
            fe.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("01", fe.Element(I + "ClaveRegimenEspecialOTrascendencia")!.Value);
        Assert.Equal("A11111111", fe.Element(I + "Contraparte")!.Element(I + "NIF")!.Value);
        var ne = fe.Descendants(I + "NoExenta").Single();
        Assert.Equal("S1", ne.Element(I + "TipoNoExenta")!.Value);
        var det = ne.Descendants(I + "DetalleIVA").ToList();
        Assert.Equal(["21", "1000.00", "210.00"], det[0].Elements().Select(e => e.Value));
        Assert.Equal(["10", "200.00", "20.00", "1.4", "2.80"], det[1].Elements().Select(e => e.Value));
        Assert.NotNull(fe.Descendants(I + "DesgloseFactura").SingleOrDefault());
    }

    [Fact]
    public void Emitida_a_un_cliente_comunitario_va_por_tipo_de_operacion_y_exenta_E5()
    {
        var doc = Sii.Mensaje("EMITIDAS", "B12345678", "Mi Empresa", "A0",
            [F("EMITIDAS", "002", "12345678901", "FR", [L1("INTRACOM_ENTREGA", 0, 5000)], ue: true)]);
        var fe = doc.Descendants(L + "FacturaExpedida").Single();
        var otro = fe.Descendants(I + "IDOtro").Single();
        Assert.Equal(["FR", "02", "FR12345678901"], otro.Elements().Select(e => e.Value));
        var entrega = fe.Descendants(I + "DesgloseTipoOperacion").Single().Element(I + "Entrega")!;
        Assert.Equal(["E5", "5000.00"], entrega.Descendants(I + "DetalleExenta").Single().Elements().Select(e => e.Value));
    }

    [Fact]
    public void No_sujeta_por_articulos_7_y_14_y_por_reglas_de_localizacion()
    {
        var doc = Sii.Mensaje("EMITIDAS", "B12345678", "Mi Empresa", "A0",
            [F("EMITIDAS", "003", "A11111111", "ES", [L1("NO_SUJETA", 0, 300), L1("NO_SUJETA_LOCAL", 0, 800), L1("NO_SUJETA_OSS", 0, 50)])]);
        var ns = doc.Descendants(I + "NoSujeta").Single();
        Assert.Equal(["ImportePorArticulos7_14_Otros", "ImporteTAIReglasLocalizacion"], ns.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["300.00", "850.00"], ns.Elements().Select(e => e.Value));
    }

    [Fact]
    public void Rectificativa_por_diferencias_con_la_factura_rectificada()
    {
        var doc = Sii.Mensaje("EMITIDAS", "B12345678", "Mi Empresa", "A0",
            [F("EMITIDAS", "R01", "A11111111", "ES", [L1("INTERIOR", 21, -100)], tipo: "RECTIFICATIVA", rect: "A001")]);
        var fe = doc.Descendants(L + "FacturaExpedida").Single();
        Assert.Equal(["TipoFactura", "TipoRectificativa", "FacturasRectificadas"], fe.Elements().Take(3).Select(e => e.Name.LocalName));
        Assert.Equal("R1", fe.Element(I + "TipoFactura")!.Value);
        Assert.Equal("I", fe.Element(I + "TipoRectificativa")!.Value);
        Assert.Equal("-100.00", fe.Descendants(I + "BaseImponible").Single().Value);
    }

    [Fact]
    public void Recibida_con_inversion_del_sujeto_pasivo_y_adquisicion_intracomunitaria()
    {
        var doc = Sii.Mensaje("RECIBIDAS", "B12345678", "Mi Empresa", "A0",
        [
            F("RECIBIDAS", "F-77", "B22222222", "ES", [L1("INVERSION_SUJETO_PASIVO", 21, 4000), L1("INTERIOR", 21, 100)]),
            F("RECIBIDAS", "DE-9", "123456789", "DE", [L1("INTRACOM_ADQUISICION", 21, 1000)], ue: true),
        ]);
        var regs = doc.Descendants(L + "RegistroLRFacturasRecibidas").ToList();
        var fr = regs[0].Element(L + "FacturaRecibida")!;
        Assert.Equal(["TipoFactura", "ClaveRegimenEspecialOTrascendencia", "ImporteTotal", "DescripcionOperacion", "DesgloseFactura",
                      "Contraparte", "FechaRegContable", "CuotaDeducible"], fr.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["InversionSujetoPasivo", "DesgloseIVA"], fr.Element(I + "DesgloseFactura")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("12-03-2026", fr.Element(I + "FechaRegContable")!.Value);
        Assert.Equal("861.00", fr.Element(I + "CuotaDeducible")!.Value);
        Assert.Equal("B22222222", regs[0].Element(L + "IDFactura")!.Element(I + "IDEmisorFactura")!.Element(I + "NIF")!.Value);
        var fr2 = regs[1].Element(L + "FacturaRecibida")!;
        Assert.Equal("09", fr2.Element(I + "ClaveRegimenEspecialOTrascendencia")!.Value);
        Assert.Equal("DE123456789", regs[1].Descendants(I + "IDEmisorFactura").Single().Descendants(I + "ID").Single().Value);
    }

    [Fact]
    public void Lee_la_respuesta_y_los_fallos()
    {
        const string xml = """
            <env:Envelope xmlns:env="http://schemas.xmlsoap.org/soap/envelope/"><env:Body>
            <siiR:RespuestaLRFacturasEmitidas xmlns:siiR="https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/ssii/fact/ws/RespuestaSuministro.xsd"
              xmlns:sii="https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/ssii/fact/ws/SuministroInformacion.xsd">
              <siiR:CSV>ABCDEF1234567890</siiR:CSV>
              <siiR:EstadoEnvio>ParcialmenteCorrecto</siiR:EstadoEnvio>
              <siiR:RespuestaLinea><siiR:IDFactura><sii:IDEmisorFactura><sii:NIF>B12345678</sii:NIF></sii:IDEmisorFactura>
                <sii:NumSerieFacturaEmisor>A001</sii:NumSerieFacturaEmisor><sii:FechaExpedicionFacturaEmisor>10-03-2026</sii:FechaExpedicionFacturaEmisor></siiR:IDFactura>
                <siiR:EstadoRegistro>Correcto</siiR:EstadoRegistro></siiR:RespuestaLinea>
              <siiR:RespuestaLinea><siiR:IDFactura><sii:IDEmisorFactura><sii:NIF>B12345678</sii:NIF></sii:IDEmisorFactura>
                <sii:NumSerieFacturaEmisor>A002</sii:NumSerieFacturaEmisor><sii:FechaExpedicionFacturaEmisor>11-03-2026</sii:FechaExpedicionFacturaEmisor></siiR:IDFactura>
                <siiR:EstadoRegistro>Incorrecto</siiR:EstadoRegistro><siiR:CodigoErrorRegistro>1100</siiR:CodigoErrorRegistro>
                <siiR:DescripcionErrorRegistro>Valor o tipo incorrecto</siiR:DescripcionErrorRegistro></siiR:RespuestaLinea>
            </siiR:RespuestaLRFacturasEmitidas></env:Body></env:Envelope>
            """;
        var r = Sii.LeerRespuesta(xml);
        Assert.Equal(("ParcialmenteCorrecto", "ABCDEF1234567890"), (r.EstadoEnvio, r.Csv));
        Assert.Equal(2, r.Lineas.Count);
        Assert.Equal("ACEPTADA", Sii.Estado(r.Lineas[0].Estado));
        Assert.Equal(("RECHAZADA", "1100"), (Sii.Estado(r.Lineas[1].Estado), r.Lineas[1].Codigo));
        Assert.Equal(new DateOnly(2026, 3, 11), r.Lineas[1].FechaExpedicion);

        var fallo = Sii.LeerRespuesta("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Certificado no válido</faultstring></s:Fault></s:Body></s:Envelope>""");
        Assert.Equal("Certificado no válido", fallo.Fallo);
    }

    [Fact]
    public void Sin_facturas_o_con_un_tipo_de_comunicacion_raro_no_hay_mensaje()
    {
        Assert.Throws<ReglaContableException>(() => Sii.Mensaje("EMITIDAS", "B1", "X", "A0", []));
        Assert.Throws<ReglaContableException>(() => Sii.Mensaje("EMITIDAS", "B1", "X", "A9", [F("EMITIDAS", "1", "A1", "ES", [L1("INTERIOR", 21, 1)])]));
        Assert.Contains("prewww1.aeat.es", Sii.Direccion("EMITIDAS", pruebas: true));
        Assert.EndsWith("/fr/SiiFactFRV1SOAP", Sii.Direccion("RECIBIDAS", pruebas: false));
    }
}
