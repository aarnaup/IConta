using System.Globalization;
using System.Threading.RateLimiting;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Contabilidad.Api;
using Contabilidad.Api.Endpoints;
using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Infrastructure;
using Contabilidad.Infrastructure.Repositorios;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// Importes de los mensajes en formato español (1.234,56). El JSON no depende de la cultura.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

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
    // SII: certificado PKCS#12 de la empresa (o de un apoderado) para el envío directo; sin él solo se descarga el XML
    var sii = new SiiOpciones(builder.Configuration["Sii:Certificado"], builder.Configuration["Sii:Clave"],
        string.Equals(builder.Configuration["Sii:Entorno"], "PRODUCCION", StringComparison.OrdinalIgnoreCase),
        string.Equals(builder.Configuration["Sii:Sello"], "true", StringComparison.OrdinalIgnoreCase));
    builder.Host.ConfigureContainer<ContainerBuilder>(cb => cb.RegisterModule(new InfrastructureModule(cs, sii)));

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

    // ---- Documentación OpenAPI / Swagger (siempre en Development; en otros entornos con OpenApi:Habilitar=true) ----
    var documentar = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("OpenApi:Habilitar");
    if (documentar) builder.Services.AddDocumentacionApi();

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

    // Las reglas de negocio incumplidas y las peticiones mal formadas son respuestas 400 normales: no se registran como errores
    app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        SuppressDiagnosticsCallback = c => c.Exception is ReglaContableException or BadHttpRequestException,
        ExceptionHandler = async ctx =>
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
    }
    });

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

    if (documentar) app.MapDocumentacionApi();

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
    app.MapCierre();
    app.MapBalances();
    app.MapInmovilizado();
    app.MapConciliacion();
    app.MapRemesas();
    app.MapBienesInversion();
    app.MapCriterioCaja();
    app.MapAnalitica();

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
