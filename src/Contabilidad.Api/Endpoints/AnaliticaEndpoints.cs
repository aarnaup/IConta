using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Analítica (dimensiones, elementos, reparto por cuenta, imputación de apuntes e informe) y presupuestos.
/// Lectura: módulo correspondiente o Contabilidad; escritura: módulo correspondiente.
/// </summary>
public static class AnaliticaEndpoints
{
    private sealed record EstadoCmd(string Estado);

    public static void MapAnalitica(this IEndpointRouteBuilder app)
    {
        var a = app.MapGroup("/api/empresas/{empresaId:long}/analitica").RequireAuthorization();

        a.MapGet("/dimensiones", (long empresaId, IAnaliticaRepositorio r, CancellationToken ct) => r.DimensionesAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Analitica, Modulo.Presupuestos, Modulo.Contabilidad);
        a.MapPost("/dimensiones", async (long empresaId, DimensionCmd cmd, IAnaliticaRepositorio r, CancellationToken ct)
            => Results.Ok(new { id = await r.CrearDimensionAsync(empresaId, cmd, ct) }))
            .Requiere(Nivel.Escritura, Modulo.Analitica);
        a.MapPut("/dimensiones/{dimensionId:long}", async (long empresaId, long dimensionId, DimensionCmd cmd, IAnaliticaRepositorio r, CancellationToken ct) =>
        {
            await r.ModificarDimensionAsync(empresaId, dimensionId, cmd, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Analitica);
        a.MapDelete("/dimensiones/{dimensionId:long}", async (long empresaId, long dimensionId, IAnaliticaRepositorio r, CancellationToken ct) =>
        {
            await r.EliminarDimensionAsync(empresaId, dimensionId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Analitica);

        a.MapPost("/dimensiones/{dimensionId:long}/elementos", async (long empresaId, long dimensionId, ElementoCmd cmd, IAnaliticaRepositorio r,
            CancellationToken ct) => Results.Ok(new { id = await r.CrearElementoAsync(empresaId, dimensionId, cmd, ct) }))
            .Requiere(Nivel.Escritura, Modulo.Analitica);
        a.MapPut("/elementos/{elementoId:long}", async (long empresaId, long elementoId, ElementoCmd cmd, IAnaliticaRepositorio r, CancellationToken ct) =>
        {
            await r.ModificarElementoAsync(empresaId, elementoId, cmd, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Analitica);
        a.MapDelete("/elementos/{elementoId:long}", async (long empresaId, long elementoId, IAnaliticaRepositorio r, CancellationToken ct) =>
        {
            await r.EliminarElementoAsync(empresaId, elementoId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Analitica);

        a.MapGet("/repartos", (long empresaId, IAnaliticaRepositorio r, CancellationToken ct) => r.RepartosAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Analitica, Modulo.Contabilidad);
        a.MapPut("/repartos", async (long empresaId, GuardarRepartoCuentaCmd cmd, IAnaliticaRepositorio r, CancellationToken ct) =>
        {
            await r.GuardarRepartoAsync(empresaId, cmd, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Analitica);

        a.MapGet("/apuntes/{apunteId:long}", async (long empresaId, long apunteId, IAnaliticaRepositorio r, CancellationToken ct)
            => Results.Ok(await r.ApunteAsync(empresaId, apunteId, ct)))
            .Requiere(Nivel.Lectura, Modulo.Analitica, Modulo.Contabilidad);
        a.MapPut("/apuntes/{apunteId:long}", async (long empresaId, long apunteId, List<ImputacionCmd> reparto, IAnaliticaRepositorio r,
            CancellationToken ct) =>
        {
            await r.ImputarApunteAsync(empresaId, apunteId, reparto, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Analitica, Modulo.Contabilidad);

        var ea = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/analitica").RequireAuthorization();
        ea.MapGet("/informe", async (long empresaId, long ejercicioId, long dimensionId, long? canalId, DateOnly? desde, DateOnly? hasta, bool? detalle,
            IAnaliticaRepositorio r, CancellationToken ct)
            => Results.Ok(await r.InformeAsync(empresaId, ejercicioId, dimensionId, canalId, desde, hasta, detalle ?? false, ct)))
            .Requiere(Nivel.Lectura, Modulo.Analitica, Modulo.Contabilidad);
        ea.MapGet("/sin-imputar", async (long empresaId, long ejercicioId, long dimensionId, DateOnly? desde, DateOnly? hasta,
            IAnaliticaRepositorio r, CancellationToken ct)
            => Results.Ok(await r.SinImputarAsync(empresaId, ejercicioId, dimensionId, desde, hasta, ct)))
            .Requiere(Nivel.Lectura, Modulo.Analitica, Modulo.Contabilidad);

        // ---------------------------------------------------------------- presupuestos
        var ep = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/presupuestos").RequireAuthorization();
        ep.MapGet("", (long empresaId, long ejercicioId, IPresupuestoRepositorio r, CancellationToken ct) => r.ListarAsync(empresaId, ejercicioId, ct))
            .Requiere(Nivel.Lectura, Modulo.Presupuestos, Modulo.Contabilidad);
        ep.MapPost("", async (long empresaId, long ejercicioId, CrearPresupuestoCmd cmd, IPresupuestoRepositorio r, CancellationToken ct)
            => Results.Ok(new { id = await r.CrearAsync(empresaId, ejercicioId, cmd, ct) }))
            .Requiere(Nivel.Escritura, Modulo.Presupuestos);

        var p = app.MapGroup("/api/empresas/{empresaId:long}/presupuestos/{presupuestoId:long}").RequireAuthorization();
        p.MapGet("", async (long empresaId, long presupuestoId, IPresupuestoRepositorio r, CancellationToken ct)
            => Results.Ok(await r.ObtenerAsync(empresaId, presupuestoId, ct)))
            .Requiere(Nivel.Lectura, Modulo.Presupuestos, Modulo.Contabilidad);
        p.MapPut("/lineas", async (long empresaId, long presupuestoId, GuardarLineasPresupuestoCmd cmd, IPresupuestoRepositorio r, CancellationToken ct) =>
        {
            await r.GuardarLineasAsync(empresaId, presupuestoId, cmd, ct);
            return Results.Ok(await r.ObtenerAsync(empresaId, presupuestoId, ct));
        }).Requiere(Nivel.Escritura, Modulo.Presupuestos);
        p.MapPost("/desde-real", async (long empresaId, long presupuestoId, GenerarDesdeRealCmd cmd, IPresupuestoRepositorio r, CancellationToken ct) =>
        {
            await r.GenerarDesdeRealAsync(empresaId, presupuestoId, cmd, ct);
            return Results.Ok(await r.ObtenerAsync(empresaId, presupuestoId, ct));
        }).Requiere(Nivel.Escritura, Modulo.Presupuestos);
        p.MapPost("/estado", async (long empresaId, long presupuestoId, EstadoCmd cmd, IPresupuestoRepositorio r, CancellationToken ct) =>
        {
            await r.CambiarEstadoAsync(empresaId, presupuestoId, cmd.Estado, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Presupuestos);
        p.MapPost("/nueva-version", async (long empresaId, long presupuestoId, IPresupuestoRepositorio r, CancellationToken ct)
            => Results.Ok(new { id = await r.NuevaVersionAsync(empresaId, presupuestoId, ct) }))
            .Requiere(Nivel.Escritura, Modulo.Presupuestos);
        p.MapDelete("", async (long empresaId, long presupuestoId, IPresupuestoRepositorio r, CancellationToken ct) =>
        {
            await r.EliminarAsync(empresaId, presupuestoId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Presupuestos);
        p.MapGet("/comparativa", async (long empresaId, long presupuestoId, int? hastaPeriodo, IPresupuestoRepositorio r, CancellationToken ct)
            => Results.Ok(await r.ComparativaAsync(empresaId, presupuestoId, hastaPeriodo, ct)))
            .Requiere(Nivel.Lectura, Modulo.Presupuestos, Modulo.Contabilidad);
    }
}
