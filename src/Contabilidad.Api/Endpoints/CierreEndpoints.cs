using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Asientos de fin de periodo, dentro del ejercicio elegido:
///   - Liquidación del IVA/IGIC de una declaración 303/420 presentada.
///   - Cierre del ejercicio (regularización, cierre y apertura del siguiente) y su deshacer.
/// Consultar es lectura; contabilizar, cerrar y deshacer exigen poder contabilizar sin revisión.
/// </summary>
public static class CierreEndpoints
{
    public static void MapCierre(this IEndpointRouteBuilder app)
    {
        var ej = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}").RequireAuthorization();

        // ---- Liquidación del IVA/IGIC ----
        ej.MapPost("/fiscal/declaraciones/{declaracionId:long}/asiento/previsualizar", async (long empresaId, long ejercicioId,
            long declaracionId, LiquidacionIvaCmd? cmd, ILiquidacionIvaRepositorio r, CancellationToken ct)
            => Results.Ok(await r.PrevisualizarAsync(empresaId, ejercicioId, declaracionId, cmd ?? new(), ct)))
            .Requiere(Nivel.Lectura, Modulo.Fiscal);

        ej.MapPost("/fiscal/declaraciones/{declaracionId:long}/asiento", async (long empresaId, long ejercicioId, long declaracionId,
            LiquidacionIvaCmd? cmd, HttpContext http, ILiquidacionIvaRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var v = await r.ContabilizarAsync(empresaId, ejercicioId, http.User.UsuarioId(), declaracionId, cmd ?? new(), ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "LIQUIDACION_IVA_CONTABILIZADA", "declaracion", declaracionId,
                new { v.Modelo, v.Anio, v.Periodo, v.AsientoId, v.AsientoNumero }, ct);
            return Results.Ok(v);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        ej.MapDelete("/fiscal/declaraciones/{declaracionId:long}/asiento", async (long empresaId, long ejercicioId, long declaracionId,
            HttpContext http, ILiquidacionIvaRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAsync(empresaId, ejercicioId, declaracionId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "LIQUIDACION_IVA_ANULADA", "declaracion", declaracionId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Fiscal);

        // ---- Cierre del ejercicio ----
        ej.MapGet("/cierre", async (long empresaId, long ejercicioId, ICierreEjercicioRepositorio r, CancellationToken ct)
            => Results.Ok(await r.EstadoAsync(empresaId, ejercicioId, ct)))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad);

        ej.MapPost("/cierre", async (long empresaId, long ejercicioId, HttpContext http, ICierreEjercicioRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await r.CerrarAsync(empresaId, ejercicioId, http.User.UsuarioId(), ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EJERCICIO_CERRADO", "ejercicio", ejercicioId,
                new { asientos = c.Asientos.Select(a => new { a.Tipo, a.Id, a.Numero }) }, ct);
            return Results.Ok(c);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);

        ej.MapPost("/cierre/deshacer", async (long empresaId, long ejercicioId, HttpContext http, ICierreEjercicioRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.ReabrirAsync(empresaId, ejercicioId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EJERCICIO_REABIERTO", "ejercicio", ejercicioId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);
    }
}
