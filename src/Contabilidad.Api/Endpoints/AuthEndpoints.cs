using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;
using Serilog;

namespace Contabilidad.Api.Endpoints;

public static class AuthEndpoints
{
    private static IResult Rechazo() => Results.Json(new { error = "Credenciales incorrectas." }, statusCode: StatusCodes.Status401Unauthorized);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginCmd cmd, HttpContext http, IUsuarioRepositorio usuarios, JwtServicio jwt, CancellationToken ct) =>
        {
            var email = (cmd.Email ?? "").Trim();
            var password = cmd.Password ?? "";
            var ip = http.Connection.RemoteIpAddress?.ToString();
            if (email.Length is 0 or > 200 || password.Length is 0 or > HasheadorPassword.LongitudMaxima) return Rechazo();

            var u = await usuarios.ObtenerLoginPorEmailAsync(email, ct);
            if (u is null)
            {
                HasheadorPassword.VerificarFalso(password);          // mismo coste que un login real
                Log.Warning("Login fallido: usuario desconocido {Email} desde {Ip}", email, ip);
                return Rechazo();
            }
            if (!u.Activo || (u.BloqueadoHasta is { } hasta && hasta > DateTime.UtcNow))
            {
                Log.Warning("Login rechazado (cuenta inactiva o bloqueada) {Email} desde {Ip}", email, ip);
                return Rechazo();
            }
            if (!HasheadorPassword.Verificar(password, u.PasswordHash))
            {
                await usuarios.RegistrarFalloAsync(u.Id, ct);
                Log.Warning("Login fallido: contraseña incorrecta {Email} desde {Ip}", email, ip);
                return Rechazo();
            }

            await usuarios.RegistrarAccesoAsync(u.Id, ct);
            var dto = u.ADto();
            var (token, expira) = jwt.Crear(dto);
            Log.Information("Login correcto {Email} desde {Ip}", email, ip);
            return Results.Ok(new LoginRespuesta(token, expira, dto));
        }).RequireRateLimiting("login");

        auth.MapGet("/yo", async (HttpContext http, IUsuarioRepositorio usuarios, CancellationToken ct) =>
        {
            var u = await usuarios.ObtenerAsync(http.User.UsuarioId(), ct);
            return u is { Activo: true } ? Results.Ok(u) : Results.Unauthorized();
        }).RequireAuthorization();

        // Devuelve un token nuevo, ya sin la marca de "debe cambiar la contraseña"
        auth.MapPost("/cambiar-password", async (CambiarPasswordCmd cmd, HttpContext http, IUsuarioRepositorio usuarios,
            JwtServicio jwt, IAuditoriaRepositorio auditoria, CancellationToken ct) =>
        {
            var id = http.User.UsuarioId();
            var u = await usuarios.ObtenerLoginPorIdAsync(id, ct);
            if (u is not { Activo: true }) return Results.Unauthorized();
            if (!HasheadorPassword.Verificar(cmd.Actual ?? "", u.PasswordHash))
                return Results.Json(new { error = "La contraseña actual no es correcta." }, statusCode: StatusCodes.Status400BadRequest);

            var nueva = cmd.Nueva ?? "";
            HasheadorPassword.ValidarPolitica(nueva, u.Email);
            if (HasheadorPassword.Verificar(nueva, u.PasswordHash))
                throw new ReglaContableException("La contraseña nueva debe ser distinta de la actual.");

            await usuarios.CambiarPasswordAsync(id, HasheadorPassword.Hash(nueva), false, ct);
            await auditoria.RegistrarAsync(id, null, "PASSWORD_CAMBIADA", "usuario", id, null, ct);

            var dto = u.ADto();
            dto.DebeCambiarPassword = false;
            var (token, expira) = jwt.Crear(dto);
            return Results.Ok(new LoginRespuesta(token, expira, dto));
        }).RequireAuthorization();
    }
}
