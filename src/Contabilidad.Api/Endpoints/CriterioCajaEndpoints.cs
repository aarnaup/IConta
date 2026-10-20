using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>Régimen de IVA de la empresa y criterio de caja: impuesto devengado por periodo y su traspaso contable.</summary>
public static class CriterioCajaEndpoints
{
    public static void MapCriterioCaja(this IEndpointRouteBuilder app)
    {
        var fis = app.MapGroup("/api/empresas/{empresaId:long}/fiscal").RequireAuthorization();
        fis.MapGet("/configuracion", (long empresaId, ICriterioCajaRepositorio r, CancellationToken ct) => r.ConfiguracionAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Fiscal, Modulo.Facturacion);
        fis.MapPut("/configuracion", async (long empresaId, GuardarConfiguracionFiscalCmd cmd, HttpContext http, ICriterioCajaRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await r.GuardarConfiguracionAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "REGIMEN_IVA_CAMBIADO", "empresa", empresaId, new { c.RegimenIva }, ct);
            return Results.Ok(c);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        fis.MapGet("/criterio-caja", async (long empresaId, int anio, string periodo, ICriterioCajaRepositorio r, CancellationToken ct)
            => Results.Ok(await r.DelPeriodoAsync(empresaId, anio, periodo, ct)))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        var ej = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/fiscal/criterio-caja").RequireAuthorization();
        ej.MapPost("/traspasar", async (long empresaId, long ejercicioId, TraspasoCajaCmd cmd, HttpContext http, ICriterioCajaRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var t = await r.TraspasarAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "CRITERIO_CAJA_TRASPASO", "asiento", t.AsientoId,
                new { cmd.Anio, cmd.Periodo, t.Repercutido, t.Deducible }, ct);
            return Results.Ok(t);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        ej.MapDelete("/{asientoId:long}", async (long empresaId, long ejercicioId, long asientoId, HttpContext http, ICriterioCajaRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAsync(empresaId, ejercicioId, asientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "CRITERIO_CAJA_TRASPASO_ANULADO", "asiento", asientoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);
    }
}
