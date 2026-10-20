using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
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
