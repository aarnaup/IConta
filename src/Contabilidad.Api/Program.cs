using System.Threading.RateLimiting;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Contabilidad.Api;
using Contabilidad.Api.Endpoints;
using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ---- Autofac ----
    var cs = builder.Configuration.GetConnectionString("Contabilidad")
             ?? throw new InvalidOperationException("Falta ConnectionStrings:Contabilidad");
    builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
    builder.Host.ConfigureContainer<ContainerBuilder>(cb => cb.RegisterModule(new InfrastructureModule(cs)));

    // ---- Autenticación JWT ----
    var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOpciones>() ?? new JwtOpciones();
    jwt.Validar();
    builder.Services.AddSingleton(jwt);
    builder.Services.AddSingleton<JwtServicio>();
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;                 // conserva los nombres de claim: sub, name, role...
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                ValidateAudience = true, ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(jwt.KeyBytes),
                ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "name", RoleClaimType = "role"
            };
        });
    builder.Services.AddAuthorization();

    // ---- Limitación de intentos en el login (por IP) ----
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });

    var app = builder.Build();

    // ---- Orden de la CLI: crea el primer superadmin y termina ----
    if (args is ["crear-superadmin", var emailAdmin, var nombreAdmin])
    {
        Environment.ExitCode = await Cli.CrearSuperadminAsync(app.Services, emailAdmin, nombreAdmin);
        return;
    }

    app.UseSerilogRequestLogging();

    app.UseExceptionHandler(h => h.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        switch (ex)
        {
            case ReglaContableException rc:
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = rc.Message, detalle = rc.Detalle });
                break;
            case BadHttpRequestException:
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "Solicitud no válida." });
                break;
            case UnauthorizedAccessException:
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "No autenticado." });
                break;
            default:
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await ctx.Response.WriteAsJsonAsync(new { error = "Error interno del servidor." });
                break;
        }
    }));

    if (!app.Environment.IsDevelopment()) app.UseHsts();

    app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
        await next();
    });

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    // Mientras la contraseña sea temporal, solo se puede usar /api/auth (para cambiarla)
    app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true
            && ctx.User.FindFirst("cpw")?.Value == "1"
            && ctx.Request.Path.StartsWithSegments("/api")
            && !ctx.Request.Path.StartsWithSegments("/api/auth"))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Debes cambiar la contraseña antes de continuar." });
            return;
        }
        await next();
    });

    app.MapAuth();
    app.MapAdmin();
    app.MapEmpresa();
    app.MapTesoreria();
    app.MapFiscal();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "La aplicación terminó de forma inesperada");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
