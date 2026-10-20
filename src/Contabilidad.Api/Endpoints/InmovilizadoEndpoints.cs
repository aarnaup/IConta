using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Inmovilizado. Categorías y fichas pertenecen a la empresa; lo que genera asientos (alta con asiento, amortización, bajas)
/// va dentro del ejercicio elegido. Amortizar y dar de baja contabilizan directamente: solo quien puede hacerlo sin revisión.
/// </summary>
public static class InmovilizadoEndpoints
{
    public static void MapInmovilizado(this IEndpointRouteBuilder app)
    {
        var emp = app.MapGroup("/api/empresas/{empresaId:long}/inmovilizado").RequireAuthorization();

        emp.MapGet("/categorias", (long empresaId, IInmovilizadoRepositorio r, CancellationToken ct) => r.ListarCategoriasAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Inmovilizado);

        emp.MapPost("/categorias", async (long empresaId, CrearCategoriaCmd cmd, HttpContext http, IInmovilizadoRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await r.CrearCategoriaAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "CATEGORIA_INMOVILIZADO_CREADA", "categoria", c.Id, new { c.Nombre }, ct);
            return Results.Created($"/api/empresas/{empresaId}/inmovilizado/categorias/{c.Id}", c);
        }).Requiere(Nivel.Escritura, Modulo.Inmovilizado);

        emp.MapGet("", (long empresaId, string? estado, IInmovilizadoRepositorio r, CancellationToken ct)
            => r.ListarAsync(empresaId, string.IsNullOrWhiteSpace(estado) ? null : estado.ToUpperInvariant(), ct))
            .Requiere(Nivel.Lectura, Modulo.Inmovilizado);

        emp.MapGet("/{inmovilizadoId:long}", async (long empresaId, long inmovilizadoId, IInmovilizadoRepositorio r, CancellationToken ct)
            => Results.Ok(await r.FichaAsync(empresaId, inmovilizadoId, ct)))
            .Requiere(Nivel.Lectura, Modulo.Inmovilizado);

        // ---- Dentro del ejercicio ----
        var ej = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/inmovilizado").RequireAuthorization();

        ej.MapPost("", async (long empresaId, long ejercicioId, AltaInmovilizadoCmd cmd, HttpContext http, IInmovilizadoRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            // El asiento de alta, si se pide, contabiliza directamente
            if (cmd.CuentaContrapartidaId is not null && !http.Acceso().PuedeContabilizarDirecto)
                return Results.Json(new { error = "Contabilizar el alta lo hace un usuario con permiso para contabilizar sin revisión." }, statusCode: 403);
            var f = await r.AltaAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "INMOVILIZADO_ALTA", "inmovilizado", f.Elemento.Id,
                new { f.Elemento.Codigo, f.Elemento.ValorAdquisicion }, ct);
            return Results.Created($"/api/empresas/{empresaId}/inmovilizado/{f.Elemento.Id}", f);
        }).Requiere(Nivel.Escritura, Modulo.Inmovilizado);

        ej.MapGet("/amortizacion", (long empresaId, long ejercicioId, IInmovilizadoRepositorio r, CancellationToken ct)
            => r.ListarEjecucionesAsync(empresaId, ejercicioId, ct))
            .Requiere(Nivel.Lectura, Modulo.Inmovilizado);

        ej.MapPost("/amortizacion/previsualizar", async (long empresaId, long ejercicioId, FechaCmd? cmd, IInmovilizadoRepositorio r, CancellationToken ct)
            => Results.Ok(await r.PrevisualizarAmortizacionAsync(empresaId, ejercicioId, cmd?.Hasta, ct)))
            .Requiere(Nivel.Lectura, Modulo.Inmovilizado);

        ej.MapPost("/amortizacion", async (long empresaId, long ejercicioId, FechaCmd? cmd, HttpContext http, IInmovilizadoRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var v = await r.AmortizarAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd?.Hasta, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "AMORTIZACION_CONTABILIZADA", "asiento", v.AsientoId,
                new { v.Hasta, v.Total, elementos = v.Lineas.Count }, ct);
            return Results.Ok(v);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Inmovilizado);

        ej.MapDelete("/amortizacion/{asientoId:long}", async (long empresaId, long ejercicioId, long asientoId, HttpContext http,
            IInmovilizadoRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAmortizacionAsync(empresaId, ejercicioId, asientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "AMORTIZACION_ANULADA", "asiento", asientoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Inmovilizado);

        ej.MapPost("/{inmovilizadoId:long}/baja", async (long empresaId, long ejercicioId, long inmovilizadoId, BajaInmovilizadoCmd cmd,
            HttpContext http, IInmovilizadoRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var b = await r.BajaAsync(empresaId, ejercicioId, http.User.UsuarioId(), inmovilizadoId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "INMOVILIZADO_BAJA", "inmovilizado", inmovilizadoId,
                new { cmd.Tipo, cmd.Fecha, cmd.ImporteVenta, b.Resultado.Resultado }, ct);
            return Results.Ok(b);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Inmovilizado);

        ej.MapDelete("/{inmovilizadoId:long}/baja", async (long empresaId, long ejercicioId, long inmovilizadoId, HttpContext http,
            IInmovilizadoRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularBajaAsync(empresaId, ejercicioId, inmovilizadoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "INMOVILIZADO_BAJA_ANULADA", "inmovilizado", inmovilizadoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Inmovilizado);
    }

    public sealed class FechaCmd { public DateOnly? Hasta { get; set; } }
}
