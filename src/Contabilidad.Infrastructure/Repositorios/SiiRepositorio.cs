using System.Security.Cryptography.X509Certificates;
using System.Text;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public sealed class FacturaSiiDto
{
    public long Id { get; set; }
    public string NumSerie { get; set; } = "";
    public DateOnly FechaExpedicion { get; set; }
    public DateOnly FechaContable { get; set; }
    public string Nif { get; set; } = "";
    public string Nombre { get; set; } = "";
    public decimal Total { get; set; }
    public string Estado { get; set; } = "";
    public string? Csv { get; set; }
    public string? Error { get; set; }
}

public sealed class SiiCmd
{
    public string Libro { get; set; } = "EMITIDAS";
    /// <summary>Facturas a enviar; si no se indican, las pendientes o rechazadas del mes.</summary>
    public List<long>? FacturaIds { get; set; }
    public int? Anio { get; set; }
    public int? Mes { get; set; }
}

public sealed record EnvioSiiDto(long Id, string Libro, string TipoComunicacion, string Entorno, int Facturas, string? EstadoEnvio, string? Csv,
    IReadOnlyList<RespuestaLineaSii> Lineas, string? Fallo);

public interface ISiiRepositorio
{
    Task<IReadOnlyList<FacturaSiiDto>> FacturasAsync(long empresaId, string libro, int anio, int? mes, CancellationToken ct);
    Task<(string Xml, string Nombre)> MensajeAsync(long empresaId, SiiCmd cmd, CancellationToken ct);
    Task<IReadOnlyList<EnvioSiiDto>> EnviarAsync(long empresaId, long usuarioId, SiiCmd cmd, CancellationToken ct);
    Task<EnvioSiiDto> RegistrarRespuestaAsync(long empresaId, long usuarioId, string libro, string respuestaXml, CancellationToken ct);
    bool EnvioConfigurado { get; }
}

/// <summary>
/// Suministro Inmediato de Información: facturas del libro con su estado, mensaje XML para enviarlo con otra herramienta (o desde la
/// Sede con el certificado) y envío directo al servicio web de la AEAT si está configurado el certificado
/// (<c>Sii:Certificado</c>, <c>Sii:Clave</c>, <c>Sii:Entorno</c> PRUEBAS o PRODUCCION, <c>Sii:Sello</c>).
/// </summary>
/// <summary>Certificado para el envío directo al SII (PKCS#12) y entorno. Sin certificado, solo se descarga el XML.</summary>
public sealed record SiiOpciones(string? Certificado = null, string? Clave = null, bool Produccion = false, bool Sello = false);

public sealed class SiiRepositorio(IConexionFactory conexiones, SiiOpciones config) : ISiiRepositorio
{
    private sealed class FilaFactura
    {
        public long Id { get; set; }
        public string Libro { get; set; } = "";
        public string? Serie { get; set; }
        public string Numero { get; set; } = "";
        public DateOnly FechaExpedicion { get; set; }
        public DateOnly? FechaOperacion { get; set; }
        public DateOnly FechaContable { get; set; }
        public string TipoFactura { get; set; } = "";
        public string? Descripcion { get; set; }
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Pais { get; set; } = "ES";
        public bool PaisUe { get; set; }
        public string TipoDocumento { get; set; } = "NIF";
        public decimal Total { get; set; }
        public bool CriterioCaja { get; set; }
        public bool Servicios { get; set; }
        public string? RectificadaNumero { get; set; }
        public DateOnly? RectificadaFecha { get; set; }
        public string? SiiEstado { get; set; }
        public string? SiiCsv { get; set; }
        public string? SiiError { get; set; }
    }

    private sealed class FilaLinea
    {
        public long FacturaId { get; set; }
        public string TipoOperacion { get; set; } = "";
        public decimal Porcentaje { get; set; }
        public decimal Base { get; set; }
        public decimal Cuota { get; set; }
        public decimal PorcentajeRecargo { get; set; }
        public decimal CuotaRecargo { get; set; }
        public decimal CuotaDeducible { get; set; }
        public bool EsBienInversion { get; set; }
    }

    private sealed class EmpresaFila { public string Nif { get; set; } = ""; public string Nombre { get; set; } = ""; public string Impuesto { get; set; } = ""; }

    public bool EnvioConfigurado => !string.IsNullOrWhiteSpace(config.Certificado);

    private static string Libro(string? libro)
    {
        var l = (libro ?? "").Trim().ToUpperInvariant();
        return l is "EMITIDAS" or "RECIBIDAS" ? l : throw new ReglaContableException("Libro no válido: EMITIDAS o RECIBIDAS.");
    }

    private static async Task<EmpresaFila> EmpresaAsync(NpgsqlConnection cn, long empresaId, CancellationToken ct)
    {
        var e = await cn.QuerySingleOrDefaultAsync<EmpresaFila>(new CommandDefinition("""
            SELECT n.numero AS nif, e.nombre, e.impuesto_indirecto AS impuesto FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id
            WHERE e.id = @empresaId
            """, new { empresaId }, cancellationToken: ct)) ?? throw new ReglaContableException("La empresa no existe.");
        if (e.Impuesto != "IVA")
            throw new ReglaContableException("El SII de la AEAT es para el IVA; las empresas de IGIC lo presentan a la Agencia Tributaria Canaria.");
        return e;
    }

    private const string SelectFacturas = """
        SELECT f.id, f.libro, f.serie, f.numero, f.fecha_expedicion, f.fecha_operacion, f.fecha_contable, f.tipo_factura, f.descripcion,
               n.numero AS nif, t.nombre, pa.iso2 AS pais, pa.es_ue AS pais_ue, n.tipo_documento, f.total, f.criterio_caja,
               COALESCE(f.clave_operacion_349 IN ('S', 'I'), false) AS servicios,
               NULLIF(COALESCE(r.serie, '') || COALESCE(r.numero, ''), '') AS rectificada_numero, r.fecha_expedicion AS rectificada_fecha,
               f.sii_estado, f.sii_csv, f.sii_error
        FROM fiscal.factura f
        JOIN conta.tercero t ON t.id = f.tercero_id
        JOIN core.nif n ON n.id = t.nif_id
        JOIN cat.pais pa ON pa.id = n.pais_id
        LEFT JOIN fiscal.factura r ON r.id = f.factura_rectificada_id
        """;

    public async Task<IReadOnlyList<FacturaSiiDto>> FacturasAsync(long empresaId, string libro, int anio, int? mes, CancellationToken ct)
    {
        libro = Libro(libro);
        var (desde, hasta) = Rango(anio, mes);
        await using var cn = await conexiones.AbrirAsync(ct);
        await EmpresaAsync(cn, empresaId, ct);
        var filas = await cn.QueryAsync<FilaFactura>(new CommandDefinition(SelectFacturas + """
             WHERE f.empresa_id = @empresaId AND f.libro = @libro AND f.estado = 'CONTABILIZADA' AND f.fecha_contable BETWEEN @desde AND @hasta
             ORDER BY f.fecha_contable, f.id
            """, new { empresaId, libro, desde, hasta }, cancellationToken: ct));
        return filas.Select(f => new FacturaSiiDto
        {
            Id = f.Id, NumSerie = (f.Serie ?? "") + f.Numero, FechaExpedicion = f.FechaExpedicion, FechaContable = f.FechaContable, Nif = f.Nif,
            Nombre = f.Nombre, Total = f.Total, Estado = f.SiiEstado ?? "PENDIENTE", Csv = f.SiiCsv, Error = f.SiiError
        }).ToList();
    }

    private static (DateOnly, DateOnly) Rango(int anio, int? mes)
    {
        if (anio is < 2017 or > 2100) throw new ReglaContableException("El año no es válido (el SII empezó en 2017).");
        if (mes is { } m && m is < 1 or > 12) throw new ReglaContableException("El mes va de 1 a 12.");
        return mes is { } x ? (new DateOnly(anio, x, 1), new DateOnly(anio, x, 1).AddMonths(1).AddDays(-1)) : (new DateOnly(anio, 1, 1), new DateOnly(anio, 12, 31));
    }

    /// <summary>Facturas elegidas (o pendientes y rechazadas del periodo) separadas en altas (A0) y modificaciones (A1).</summary>
    private static async Task<(List<FacturaSii> Altas, List<FacturaSii> Modificaciones)> SeleccionAsync(NpgsqlConnection cn, long empresaId,
        SiiCmd cmd, string libro, CancellationToken ct)
    {
        List<FilaFactura> filas;
        if (cmd.FacturaIds is { Count: > 0 } ids)
        {
            filas = (await cn.QueryAsync<FilaFactura>(new CommandDefinition(SelectFacturas + """
                 WHERE f.empresa_id = @empresaId AND f.libro = @libro AND f.estado = 'CONTABILIZADA' AND f.id = ANY(@ids)
                 ORDER BY f.fecha_contable, f.id
                """, new { empresaId, libro, ids = ids.ToArray() }, cancellationToken: ct))).AsList();
            if (filas.Count != ids.Distinct().Count())
                throw new ReglaContableException("Alguna factura no existe, no es de este libro o no está contabilizada.");
        }
        else
        {
            var (desde, hasta) = Rango(cmd.Anio ?? throw new ReglaContableException("Indica las facturas o el año y el mes."), cmd.Mes);
            filas = (await cn.QueryAsync<FilaFactura>(new CommandDefinition(SelectFacturas + """
                 WHERE f.empresa_id = @empresaId AND f.libro = @libro AND f.estado = 'CONTABILIZADA' AND f.fecha_contable BETWEEN @desde AND @hasta
                   AND COALESCE(f.sii_estado, 'PENDIENTE') IN ('PENDIENTE', 'RECHAZADA')
                 ORDER BY f.fecha_contable, f.id
                """, new { empresaId, libro, desde, hasta }, cancellationToken: ct))).AsList();
        }
        if (filas.Count == 0) throw new ReglaContableException("No hay facturas que enviar.");

        var lineas = (await cn.QueryAsync<FilaLinea>(new CommandDefinition("""
            SELECT factura_id, tipo_operacion, porcentaje, base, cuota, porcentaje_recargo, cuota_recargo, cuota_deducible, es_bien_inversion
            FROM fiscal.factura_iva WHERE factura_id = ANY(@ids) ORDER BY factura_id, porcentaje DESC, id
            """, new { ids = filas.Select(f => f.Id).ToArray() }, cancellationToken: ct))).ToLookup(l => l.FacturaId);

        var facturas = filas.Select(f => new FacturaSii(f.Id, f.Libro, f.Serie, f.Numero, f.FechaExpedicion, f.FechaOperacion, f.FechaContable,
            f.TipoFactura, f.Descripcion, f.Nif, f.Nombre, f.Pais, f.PaisUe, f.TipoDocumento, f.Total, f.CriterioCaja, f.Servicios,
            f.RectificadaNumero, f.RectificadaFecha,
            lineas[f.Id].Select(l => new LineaSii(l.TipoOperacion, l.Porcentaje, l.Base, l.Cuota, l.PorcentajeRecargo, l.CuotaRecargo,
                l.CuotaDeducible, l.EsBienInversion)).ToList(), f.SiiEstado)).ToList();
        // Ya aceptadas (aunque sea con errores): se envían como modificación
        var modificaciones = facturas.Where(f => f.Estado is "ACEPTADA" or "ACEPTADA_CON_ERRORES").ToList();
        return (facturas.Except(modificaciones).ToList(), modificaciones);
    }

    public async Task<(string Xml, string Nombre)> MensajeAsync(long empresaId, SiiCmd cmd, CancellationToken ct)
    {
        var libro = Libro(cmd.Libro);
        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await EmpresaAsync(cn, empresaId, ct);
        var (altas, modif) = await SeleccionAsync(cn, empresaId, cmd, libro, ct);
        if (altas.Count > 0 && modif.Count > 0)
            throw new ReglaContableException("Hay facturas nuevas y otras ya enviadas: descárgalas por separado (alta A0 y modificación A1).");
        var (lista, tipo) = altas.Count > 0 ? (altas, "A0") : (modif, "A1");
        var xml = Sii.Mensaje(libro, e.Nif, e.Nombre, tipo, lista);
        return (Texto(xml), $"SII_{Sii.NifEspanol(e.Nif)}_{libro.ToLowerInvariant()}_{tipo}_{DateTime.Now:yyyyMMddHHmmss}.xml");
    }

    private static string Texto(System.Xml.Linq.XDocument xml)
    {
        using var sw = new Utf8StringWriter();
        xml.Save(sw);
        return sw.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter { public override Encoding Encoding => Encoding.UTF8; }

    public async Task<IReadOnlyList<EnvioSiiDto>> EnviarAsync(long empresaId, long usuarioId, SiiCmd cmd, CancellationToken ct)
    {
        if (!EnvioConfigurado)
            throw new ReglaContableException("El envío directo al SII no está configurado.",
                "Indica el certificado en Sii:Certificado (y Sii:Clave, Sii:Entorno) o descarga el XML y envíalo con tu herramienta.");
        var libro = Libro(cmd.Libro);
        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await EmpresaAsync(cn, empresaId, ct);
        var (altas, modif) = await SeleccionAsync(cn, empresaId, cmd, libro, ct);
        var pruebas = !config.Produccion;
        var sello = config.Sello;
        var resultado = new List<EnvioSiiDto>();
        foreach (var (lista, tipo) in new[] { (altas, "A0"), (modif, "A1") })
        {
            if (lista.Count == 0) continue;
            var xml = Texto(Sii.Mensaje(libro, e.Nif, e.Nombre, tipo, lista));
            string respuesta;
            try { respuesta = await PostAsync(Sii.Direccion(libro, pruebas, sello), xml, ct); }
            catch (HttpRequestException ex)
            {
                throw new ReglaContableException("No se ha podido conectar con el SII de la AEAT.", ex.Message);
            }
            resultado.Add(await GuardarRespuestaAsync(cn, empresaId, usuarioId, libro, tipo, pruebas ? "PRUEBAS" : "PRODUCCION", lista, xml, respuesta, ct));
        }
        return resultado;
    }

    private async Task<string> PostAsync(string url, string xml, CancellationToken ct)
    {
        var cert = X509CertificateLoader.LoadPkcs12FromFile(config.Certificado!, config.Clave);
        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(cert);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        using var contenido = new StringContent(xml, Encoding.UTF8, "text/xml");
        contenido.Headers.Add("SOAPAction", "\"\"");
        using var r = await http.PostAsync(url, contenido, ct);
        return await r.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Respuesta de un envío hecho con otra herramienta: actualiza el estado de las facturas que nombra.</summary>
    public async Task<EnvioSiiDto> RegistrarRespuestaAsync(long empresaId, long usuarioId, string libro, string respuestaXml, CancellationToken ct)
    {
        libro = Libro(libro);
        if (string.IsNullOrWhiteSpace(respuestaXml)) throw new ReglaContableException("Pega la respuesta XML de la AEAT.");
        RespuestaSii r;
        try { r = Sii.LeerRespuesta(respuestaXml); }
        catch (System.Xml.XmlException ex) { throw new ReglaContableException("La respuesta no es un XML válido.", ex.Message); }
        await using var cn = await conexiones.AbrirAsync(ct);
        await EmpresaAsync(cn, empresaId, ct);
        var nums = r.Lineas.Select(l => l.NumSerie).Distinct().ToArray();
        var filas = (await cn.QueryAsync<FilaFactura>(new CommandDefinition(SelectFacturas + """
             WHERE f.empresa_id = @empresaId AND f.libro = @libro AND f.estado = 'CONTABILIZADA'
               AND COALESCE(f.serie, '') || f.numero = ANY(@nums)
            """, new { empresaId, libro, nums }, cancellationToken: ct))).AsList();
        var facturas = filas.Select(f => new FacturaSii(f.Id, f.Libro, f.Serie, f.Numero, f.FechaExpedicion, f.FechaOperacion, f.FechaContable,
            f.TipoFactura, f.Descripcion, f.Nif, f.Nombre, f.Pais, f.PaisUe, f.TipoDocumento, f.Total, f.CriterioCaja, f.Servicios,
            f.RectificadaNumero, f.RectificadaFecha, [])).ToList();
        return await GuardarRespuestaAsync(cn, empresaId, usuarioId, libro, "A0", "MANUAL", facturas, "(enviado con otra herramienta)", respuestaXml, ct);
    }

    private static async Task<EnvioSiiDto> GuardarRespuestaAsync(NpgsqlConnection cn, long empresaId, long usuarioId, string libro, string tipo,
        string entorno, IReadOnlyList<FacturaSii> facturas, string mensaje, string respuestaXml, CancellationToken ct)
    {
        RespuestaSii r;
        try { r = Sii.LeerRespuesta(respuestaXml); }
        catch (Exception ex) when (ex is System.Xml.XmlException or ReglaContableException)
        {
            r = new RespuestaSii("Fallo", null, [], "Respuesta no reconocida: " + (respuestaXml.Length > 300 ? respuestaXml[..300] : respuestaXml));
        }
        await using var tx = await cn.BeginTransactionAsync(ct);
        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO fiscal.envio_sii (empresa_id, libro, tipo_comunicacion, entorno, facturas, estado_envio, csv, mensaje, respuesta, creado_por)
            VALUES (@empresaId, @libro, @tipo, @entorno, @n, @estado, @csv, @mensaje, @respuestaXml, @usuarioId) RETURNING id
            """, new { empresaId, libro, tipo, entorno, n = facturas.Count, estado = r.EstadoEnvio, csv = r.Csv, mensaje, respuestaXml, usuarioId },
            tx, cancellationToken: ct));
        // En pruebas no se marca nada: las facturas siguen pendientes para el envío real
        if (entorno != "PRUEBAS")
            foreach (var l in r.Lineas)
            {
                var f = facturas.FirstOrDefault(x => x.NumSerie == l.NumSerie && (l.FechaExpedicion is null || x.FechaExpedicion == l.FechaExpedicion));
                if (f is null) continue;
                var error = l.Codigo is null ? null : $"{l.Codigo}: {l.Descripcion}";
                await cn.ExecuteAsync(new CommandDefinition("""
                    UPDATE fiscal.factura SET sii_estado = @estado, sii_csv = COALESCE(@csv, sii_csv), sii_enviada_en = now(), sii_error = left(@error, 500)
                    WHERE id = @id
                    """, new { estado = Sii.Estado(l.Estado), csv = l.Estado == "Incorrecto" ? null : r.Csv, error, id = f.Id }, tx, cancellationToken: ct));
            }
        await tx.CommitAsync(ct);
        return new EnvioSiiDto(id, libro, tipo, entorno, facturas.Count, r.EstadoEnvio, r.Csv, r.Lineas, r.Fallo);
    }
}
