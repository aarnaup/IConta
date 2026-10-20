using System.Security.Claims;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Seguridad;

public static class UsuarioActual
{
    public const string ClaveAcceso = "acceso";

    public static bool TryUsuarioId(this ClaimsPrincipal u, out long id)
    {
        id = 0;
        return long.TryParse(u.FindFirst("sub")?.Value, out id);
    }

    public static long UsuarioId(this ClaimsPrincipal u)
        => u.TryUsuarioId(out var id) ? id : throw new UnauthorizedAccessException();

    /// <summary>Acceso ya comprobado por el filtro Requiere*; solo válido en endpoints que lo aplican.</summary>
    public static AccesoEmpresa Acceso(this HttpContext c)
        => c.Items[ClaveAcceso] as AccesoEmpresa ?? throw new InvalidOperationException("El endpoint no comprueba el acceso a la empresa.");
}

/// <summary>
/// Filtros de autorización de los endpoints. Todos consultan la base de datos (usuario activo, empresa asignada, permisos),
/// así que un cambio de permisos o una baja se aplica de inmediato, sin esperar a que caduque el token.
/// </summary>
public static class Filtros
{
    private static IResult Prohibido(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Solo exige tener acceso a la empresa de la ruta ({empresaId}).</summary>
    public static RouteHandlerBuilder RequiereAcceso(this RouteHandlerBuilder b) => Aplica(b, Nivel.Ninguno, false, []);

    /// <summary>Exige al menos ese nivel en ALGUNO de los módulos indicados.</summary>
    public static RouteHandlerBuilder Requiere(this RouteHandlerBuilder b, Nivel nivel, params Modulo[] modulos) => Aplica(b, nivel, false, modulos);

    /// <summary>Como Requiere, y además el usuario debe poder contabilizar sin revisión (gestor, superadmin o "salta revisión").</summary>
    public static RouteHandlerBuilder RequiereDirecto(this RouteHandlerBuilder b, Nivel nivel, params Modulo[] modulos) => Aplica(b, nivel, true, modulos);

    private static RouteHandlerBuilder Aplica(RouteHandlerBuilder b, Nivel nivel, bool directo, Modulo[] modulos)
        => b.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!http.User.TryUsuarioId(out var usuarioId)) return Results.Unauthorized();
            if (!long.TryParse(http.Request.RouteValues["empresaId"]?.ToString(), out var empresaId))
                return Results.BadRequest(new { error = "Empresa no válida." });

            var acceso = await http.RequestServices.GetRequiredService<IAccesoRepositorio>()
                .ObtenerAsync(usuarioId, empresaId, http.RequestAborted);
            if (acceso is null) return Prohibido("No tienes acceso a esta empresa.");
            if (modulos.Length > 0 && !modulos.Any(m => acceso.Puede(m, nivel)))
                return Prohibido("No tienes permiso para esta operación.");
            if (directo && !acceso.PuedeContabilizarDirecto)
                return Prohibido("Esta operación la realiza un usuario con permiso para contabilizar sin revisión.");

            http.Items[UsuarioActual.ClaveAcceso] = acceso;
            return await next(ctx);
        });

    public static RouteHandlerBuilder SoloGestor(this RouteHandlerBuilder b) => ExigeUsuario(b, superadmin: false);
    public static RouteHandlerBuilder SoloSuperadmin(this RouteHandlerBuilder b) => ExigeUsuario(b, superadmin: true);

    private static RouteHandlerBuilder ExigeUsuario(RouteHandlerBuilder b, bool superadmin)
        => b.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!http.User.TryUsuarioId(out var usuarioId)) return Results.Unauthorized();
            var u = await http.RequestServices.GetRequiredService<IUsuarioRepositorio>().ObtenerAsync(usuarioId, http.RequestAborted);
            if (u is not { Activo: true } || u.Tipo != "GESTOR" || (superadmin && !u.EsSuperadmin))
                return Prohibido("No tienes permiso para esta operación.");
            return await next(ctx);
        });
}
