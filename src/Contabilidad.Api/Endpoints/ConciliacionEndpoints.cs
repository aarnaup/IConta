using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Conciliación bancaria. Extractos y conciliaciones pertenecen a la empresa; contabilizar desde una línea del extracto genera un
/// asiento y va dentro del ejercicio elegido. Importar y conciliar: escritura en Tesorería. Contabilizar: además, sin revisión.
/// </summary>
public static class ConciliacionEndpoints
{
    public static void MapConciliacion(this IEndpointRouteBuilder app)
    {
        var t = app.MapGroup("/api/empresas/{empresaId:long}/tesoreria").RequireAuthorization();

        t.MapPost("/extractos", async (long empresaId, ImportarExtractoCmd cmd, HttpContext http, IConciliacionRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var i = await r.ImportarAsync(empresaId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EXTRACTO_IMPORTADO", "extracto", i.Extractos.FirstOrDefault()?.Id,
                new { cmd.Nombre, i.Formato, lineas = i.Extractos.Sum(e => e.Nuevas) }, ct);
            return Results.Ok(i);
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapGet("/extractos", (long empresaId, long? cuentaBancariaId, IConciliacionRepositorio r, CancellationToken ct)
            => r.ListarExtractosAsync(empresaId, cuentaBancariaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapDelete("/extractos/{extractoId:long}", async (long empresaId, long extractoId, HttpContext http, IConciliacionRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.EliminarExtractoAsync(empresaId, extractoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EXTRACTO_ELIMINADO", "extracto", extractoId, null, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapPost("/extractos/lineas/{lineaId:long}/ignorar", async (long empresaId, long lineaId, IConciliacionRepositorio r, CancellationToken ct) =>
        {
            await r.CambiarEstadoLineaAsync(empresaId, lineaId, true, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapPost("/extractos/lineas/{lineaId:long}/recuperar", async (long empresaId, long lineaId, IConciliacionRepositorio r, CancellationToken ct) =>
        {
            await r.CambiarEstadoLineaAsync(empresaId, lineaId, false, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapGet("/conciliacion", async (long empresaId, long cuentaBancariaId, DateOnly? hasta, IConciliacionRepositorio r, CancellationToken ct)
            => Results.Ok(await r.EstadoAsync(empresaId, cuentaBancariaId, hasta, ct)))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapPost("/conciliacion/automatica", async (long empresaId, ConciliacionAutomaticaCmd cmd, HttpContext http, IConciliacionRepositorio r,
            CancellationToken ct) => Results.Ok(await r.AutomaticaAsync(empresaId, http.User.UsuarioId(), cmd, ct)))
            .Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapPost("/conciliacion", async (long empresaId, ConciliarCmd cmd, HttpContext http, IConciliacionRepositorio r, CancellationToken ct)
            => Results.Ok(await r.ConciliarAsync(empresaId, http.User.UsuarioId(), cmd, ct)))
            .Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapDelete("/conciliacion/{conciliacionId:long}", async (long empresaId, long conciliacionId, IConciliacionRepositorio r, CancellationToken ct) =>
        {
            await r.DeshacerAsync(empresaId, conciliacionId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        // ---- Contabilizar desde una línea (dentro del ejercicio) ----
        var ej = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/tesoreria/extractos/lineas/{lineaId:long}").RequireAuthorization();

        ej.MapPost("/asiento", async (long empresaId, long ejercicioId, long lineaId, AsientoDesdeLineaCmd cmd, HttpContext http,
            IConciliacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await r.AsientoDesdeLineaAsync(empresaId, ejercicioId, http.User.UsuarioId(), lineaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "ASIENTO_DESDE_EXTRACTO", "asiento", c.AsientoId, new { lineaId }, ct);
            return Results.Ok(c);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);

        ej.MapPost("/liquidar", async (long empresaId, long ejercicioId, long lineaId, LiquidarDesdeLineaCmd cmd, HttpContext http,
            IConciliacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await r.LiquidarDesdeLineaAsync(empresaId, ejercicioId, http.User.UsuarioId(), lineaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "LIQUIDACION_DESDE_EXTRACTO", "vencimiento", cmd.VencimientoId,
                new { lineaId, c.AsientoId }, ct);
            return Results.Ok(c);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);

        // ---- Reglas por concepto ----
        t.MapGet("/reglas-conciliacion", (long empresaId, IConciliacionRepositorio r, CancellationToken ct) => r.ReglasAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);
        t.MapPost("/reglas-conciliacion", async (long empresaId, CrearReglaConciliacionCmd cmd, HttpContext http, IConciliacionRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var g = await r.CrearReglaAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "REGLA_CONCILIACION_CREADA", "regla_conciliacion", g.Id, new { g.Texto, g.Cuenta }, ct);
            return Results.Ok(g);
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);
        t.MapDelete("/reglas-conciliacion/{reglaId:long}", async (long empresaId, long reglaId, IConciliacionRepositorio r, CancellationToken ct) =>
        {
            await r.BorrarReglaAsync(empresaId, reglaId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);
        app.MapPost("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/tesoreria/conciliacion/reglas",
            async (long empresaId, long ejercicioId, AplicarReglasCmd cmd, HttpContext http, IConciliacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
            {
                var a = await r.AplicarReglasAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
                if (!cmd.Simular && a.Aplicadas > 0)
                    await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "REGLAS_CONCILIACION_APLICADAS", "cuenta_bancaria", cmd.CuentaBancariaId, new { a.Aplicadas }, ct);
                return Results.Ok(a);
            }).RequireAuthorization().RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);
    }
}
