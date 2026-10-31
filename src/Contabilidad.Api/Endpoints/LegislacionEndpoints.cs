using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Legislación por ejercicio (solo superadmin): parámetros de cálculo con fecha «desde», versiones de las casillas de los modelos por
/// año y diseños de fichero de la AEAT con vigencia. Cada declaración usa lo vigente en su periodo.
/// </summary>
public static class LegislacionEndpoints
{
    public static void MapLegislacion(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/legislacion").RequireAuthorization();

        g.MapGet("/parametros", (ILegislacionRepositorio r, CancellationToken ct) => r.ParametrosAsync(ct)).SoloSuperadmin();
        g.MapPost("/parametros", async (GuardarParametroCmd cmd, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.GuardarParametroAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_PARAMETRO", "parametro_fiscal", null, new { cmd.Clave, cmd.Desde, cmd.Valor }, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
        g.MapDelete("/parametros/{id:long}", async (long id, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.BorrarParametroAsync(id, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_PARAMETRO_BORRADO", "parametro_fiscal", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        g.MapGet("/formularios", (ILegislacionRepositorio r, CancellationToken ct) => r.FormulariosAsync(ct)).SoloSuperadmin();
        g.MapGet("/formularios/{modelo}/{anio:int}", (string modelo, int anio, ILegislacionRepositorio r)
            => Results.Text(r.DefinicionFormulario(modelo, anio), "application/json")).SoloSuperadmin();
        g.MapPost("/formularios", async (GuardarFormularioCmd cmd, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.GuardarFormularioAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_FORMULARIO", "formulario_version", null, new { cmd.Modelo, cmd.DesdeAnio, cmd.Comentario }, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
        g.MapDelete("/formularios/{id:long}", async (long id, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.BorrarFormularioAsync(id, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_FORMULARIO_BORRADO", "formulario_version", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        // Cuentas de cada partida del balance y la PyG del 200 (BALANCE_200, PYG_200), por ejercicio
        g.MapGet("/estados", (ILegislacionRepositorio r, CancellationToken ct) => r.EstadosAsync(ct)).SoloSuperadmin();
        g.MapGet("/estados/{codigo}/{anio:int}", (string codigo, int anio, ILegislacionRepositorio r) => r.Estado(codigo, anio)).SoloSuperadmin();
        g.MapPost("/estados", async (GuardarEstadoCmd cmd, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.GuardarEstadoAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_ESTADO_200", "formulario_version", null, new { cmd.Codigo, cmd.DesdeAnio, cmd.Comentario }, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        g.MapGet("/disenos", (ILegislacionRepositorio r, CancellationToken ct) => r.DisenosAsync(ct)).SoloSuperadmin();
        g.MapGet("/disenos/{modelo}/{anio:int}", (string modelo, int anio, ILegislacionRepositorio r)
            => Results.Text(r.DisenoJson(modelo, anio), "application/json")).SoloSuperadmin();
        g.MapPost("/disenos", async (GuardarDisenoCmd cmd, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.GuardarDisenoAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_DISENO", "diseno_version", null, new { cmd.Modelo, cmd.Desde, cmd.Hasta, cmd.Comentario }, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
        g.MapDelete("/disenos/{id:long}", async (long id, HttpContext http, ILegislacionRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.BorrarDisenoAsync(id, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "LEGISLACION_DISENO_BORRADO", "diseno_version", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
    }
}
