using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Modelos fiscales. Pertenecen a la empresa y a un año natural (no a un ejercicio contable), por eso cuelgan de
/// /api/empresas/{id}/fiscal. Calcular y consultar: lectura del módulo Fiscal. Guardar: escritura.
/// Presentar y descartar fijan cifras oficiales: solo quien puede contabilizar sin revisión.
/// </summary>
public static class FiscalEndpoints
{
    public sealed record RespuestaSiiCmd(string Libro, string Xml);

    public static void MapFiscal(this IEndpointRouteBuilder app)
    {
        var f = app.MapGroup("/api/empresas/{empresaId:long}/fiscal").RequireAuthorization();

        f.MapGet("/modelos", (long empresaId, IFiscalRepositorio r, CancellationToken ct)
            => r.ModelosDisponiblesAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        // Calcula sin guardar nada
        f.MapPost("/calcular", async (long empresaId, CalcularModeloCmd cmd, IFiscalRepositorio r, CancellationToken ct)
            => Results.Ok(await r.CalcularAsync(empresaId, cmd, ct)))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        f.MapGet("/declaraciones", (long empresaId, int? anio, IFiscalRepositorio r, CancellationToken ct)
            => r.ListarAsync(empresaId, anio, ct))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        f.MapGet("/declaraciones/{id:long}", async (long empresaId, long id, IFiscalRepositorio r, CancellationToken ct)
            => Results.Ok(await r.ObtenerAsync(empresaId, id, ct)))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        f.MapPost("/declaraciones", async (long empresaId, CalcularModeloCmd cmd, HttpContext http, IFiscalRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var v = await r.GuardarAsync(empresaId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "DECLARACION_GUARDADA", "declaracion", v.Id,
                new { v.Modelo, v.Anio, v.Periodo, v.Resultado }, ct);
            return Results.Ok(v);
        }).Requiere(Nivel.Escritura, Modulo.Fiscal);

        // ---- Libros registro de IVA/IGIC (por fecha contable) ----
        f.MapGet("/libros/{libro}", async (long empresaId, string libro, int anio, string periodo, ILibrosRegistroRepositorio r, CancellationToken ct)
            => Results.Ok(await r.ObtenerAsync(empresaId, libro, anio, periodo, ct)))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        f.MapGet("/libros/{libro}/csv", async (long empresaId, string libro, int anio, string periodo, ILibrosRegistroRepositorio r, CancellationToken ct) =>
        {
            var l = await r.ObtenerAsync(empresaId, libro, anio, periodo, ct);
            var nombre = $"libro-{(l.Libro == "EMITIDAS" ? "expedidas" : "recibidas")}-{l.Impuesto.ToLowerInvariant()}-{l.Anio}-{l.Periodo}.csv";
            return Results.File(LibrosRegistro.Csv(l.Libro, l.Impuesto, l.Lineas), "text/csv; charset=utf-8", nombre);
        }).Requiere(Nivel.Lectura, Modulo.Fiscal);

        // Formato oficial de la AEAT: un XLSX con las hojas EXPEDIDAS y RECIBIDAS
        f.MapGet("/libros-aeat", async (long empresaId, int anio, string periodo, ILibrosRegistroRepositorio r, CancellationToken ct) =>
        {
            var (contenido, nombre) = await r.FormatoAeatAsync(empresaId, anio, periodo, ct);
            return Results.File(contenido, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
        }).Requiere(Nivel.Lectura, Modulo.Fiscal);

        // Fichero con el diseño de registro de la AEAT, para importarlo en el formulario de la Sede o presentarlo por fichero
        f.MapPost("/declaraciones/{id:long}/fichero", async (long empresaId, long id, FicheroAeatCmd cmd, IPresentacionAeatRepositorio r,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var (contenido, nombre) = await r.FicheroAsync(empresaId, id, cmd, cfg["Aeat:NifDesarrollador"], ct);
            return Results.File(contenido, "text/plain; charset=iso-8859-1", nombre);
        }).Requiere(Nivel.Lectura, Modulo.Fiscal);

        // ---- SII: estado de las facturas, mensaje XML, envío (si hay certificado) y registro de respuestas de otra herramienta ----
        f.MapGet("/sii/facturas", async (long empresaId, string libro, int anio, int? mes, ISiiRepositorio r, CancellationToken ct)
            => Results.Ok(new { envioConfigurado = r.EnvioConfigurado, facturas = await r.FacturasAsync(empresaId, libro, anio, mes, ct) }))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);
        f.MapPost("/sii/xml", async (long empresaId, SiiCmd cmd, ISiiRepositorio r, CancellationToken ct) =>
        {
            var (xml, nombre) = await r.MensajeAsync(empresaId, cmd, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(xml), "application/xml; charset=utf-8", nombre);
        }).Requiere(Nivel.Lectura, Modulo.Fiscal);
        f.MapPost("/sii/enviar", async (long empresaId, SiiCmd cmd, HttpContext http, ISiiRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var envios = await r.EnviarAsync(empresaId, http.User.UsuarioId(), cmd, ct);
            foreach (var e in envios)
                await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "SII_ENVIADO", "envio_sii", e.Id,
                    new { e.Libro, e.TipoComunicacion, e.Entorno, e.Facturas, e.EstadoEnvio, e.Csv }, ct);
            return Results.Ok(envios);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);
        f.MapPost("/sii/respuesta", async (long empresaId, RespuestaSiiCmd cmd, HttpContext http, ISiiRepositorio r, CancellationToken ct)
            => Results.Ok(await r.RegistrarRespuestaAsync(empresaId, http.User.UsuarioId(), cmd.Libro, cmd.Xml, ct)))
            .RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        f.MapPost("/declaraciones/{id:long}/presentar", async (long empresaId, long id, PresentarDeclaracionCmd cmd, HttpContext http,
            IFiscalRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var v = await r.PresentarAsync(empresaId, id, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "DECLARACION_PRESENTADA", "declaracion", id,
                new { v.Modelo, v.Anio, v.Periodo, v.Resultado, cmd.FechaPresentacion, cmd.Justificante }, ct);
            return Results.Ok(v);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        f.MapPost("/declaraciones/{id:long}/descartar", async (long empresaId, long id, HttpContext http,
            IFiscalRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAsync(empresaId, id, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "DECLARACION_DESCARTADA", "declaracion", id, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);
    }
}
