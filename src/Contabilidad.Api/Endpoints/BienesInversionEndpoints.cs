using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Prorrata del IVA/IGIC, registro de bienes de inversión y regularización de sus deducciones (arts. 107-110 LIVA).
/// La regularización entra sola en el 303/420; aquí se consulta el libro y se contabiliza el asiento.
/// </summary>
public static class BienesInversionEndpoints
{
    public static void MapBienesInversion(this IEndpointRouteBuilder app)
    {
        var fis = app.MapGroup("/api/empresas/{empresaId:long}/fiscal").RequireAuthorization();
        fis.MapGet("/prorratas", (long empresaId, IBienesInversionRepositorio r, CancellationToken ct) => r.ProrratasAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);
        fis.MapPut("/prorratas", async (long empresaId, GuardarProrrataCmd cmd, HttpContext http, IBienesInversionRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var p = await r.GuardarProrrataAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "PRORRATA_GUARDADA", "prorrata", null,
                new { p.Anio, p.SectorDiferenciado, p.PorcentajeProvisional, p.PorcentajeDefinitivo }, ct);
            return Results.Ok(p);
        }).Requiere(Nivel.Escritura, Modulo.Fiscal);

        fis.MapGet("/prorratas/{anio:int}/regularizacion", async (long empresaId, int anio, IBienesInversionRepositorio r, CancellationToken ct)
            => Results.Ok(await r.RegularizacionProrrataAsync(empresaId, anio, ct)))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);
        var ejf = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/fiscal/prorratas").RequireAuthorization();
        ejf.MapPost("/regularizar", async (long empresaId, long ejercicioId, RegularizarProrrataCmd cmd, HttpContext http, IBienesInversionRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var v = await r.ContabilizarProrrataAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "PRORRATA_REGULARIZADA", "asiento", v.AsientoId, new { cmd.Anio, v.Importe }, ct);
            return Results.Ok(v);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);
        ejf.MapDelete("/regularizacion/{asientoId:long}", async (long empresaId, long ejercicioId, long asientoId, HttpContext http,
            IBienesInversionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularProrrataAsync(empresaId, ejercicioId, asientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "PRORRATA_REGULARIZACION_ANULADA", "asiento", asientoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        var inm = app.MapGroup("/api/empresas/{empresaId:long}/inmovilizado").RequireAuthorization();
        inm.MapGet("/bienes-inversion", async (long empresaId, int? anio, IBienesInversionRepositorio r, CancellationToken ct)
            => Results.Ok(await r.LibroAsync(empresaId, anio ?? DateTime.Today.Year, ct)))
            .Requiere(Nivel.Lectura, Modulo.Inmovilizado, Modulo.Fiscal);

        inm.MapPost("/{inmovilizadoId:long}/bien-inversion", async (long empresaId, long inmovilizadoId, AltaBienInversionCmd cmd, HttpContext http,
            IBienesInversionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var b = await r.AltaAsync(empresaId, inmovilizadoId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "BIEN_INVERSION_ALTA", "inmovilizado", inmovilizadoId,
                new { b.AnioInicio, b.Anios, b.CuotaSoportada, b.ProrrataInicial }, ct);
            return Results.Ok(b);
        }).Requiere(Nivel.Escritura, Modulo.Inmovilizado);

        inm.MapDelete("/{inmovilizadoId:long}/bien-inversion", async (long empresaId, long inmovilizadoId, HttpContext http,
            IBienesInversionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.QuitarAsync(empresaId, inmovilizadoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "BIEN_INVERSION_QUITADO", "inmovilizado", inmovilizadoId, null, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Inmovilizado);

        var ej = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/inmovilizado/bienes-inversion").RequireAuthorization();
        ej.MapPost("/regularizar", async (long empresaId, long ejercicioId, RegularizarBienesCmd cmd, HttpContext http,
            IBienesInversionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var v = await r.ContabilizarAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "BIENES_INVERSION_REGULARIZADOS", "asiento", v.AsientoId,
                new { cmd.Anio, cmd.Periodo, v.Importe }, ct);
            return Results.Ok(v);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Inmovilizado);

        ej.MapDelete("/regularizacion/{asientoId:long}", async (long empresaId, long ejercicioId, long asientoId, HttpContext http,
            IBienesInversionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAsync(empresaId, ejercicioId, asientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "BIENES_INVERSION_REGULARIZACION_ANULADA", "asiento", asientoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Inmovilizado);
    }
}
