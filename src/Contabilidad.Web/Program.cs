using System.Text.Json;
using Serilog;

// Aplicación web: sirve index.html, app.js y app.css, y /config.js con la dirección de la API (Api:Url).
// Si Api:Url está vacía, la web llama a la API en su mismo origen (por ejemplo, detrás de un proxy que envía /api a la API).
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext());

    var apiUrl = (builder.Configuration["Api:Url"] ?? "").Trim().TrimEnd('/');
    string? apiOrigen = null;
    if (apiUrl.Length > 0)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Api:Url debe ser una dirección http(s) absoluta: «{apiUrl}».");
        apiOrigen = u.GetLeftPart(UriPartial.Authority);
    }
    var configJs = $"window.ICONTA_CONFIG = {JsonSerializer.Serialize(new { api = apiUrl })};\n";
    // En desarrollo, la recarga automática del navegador (Visual Studio / dotnet watch) abre un WebSocket a un puerto local aleatorio
    var recarga = builder.Environment.IsDevelopment() ? " ws://localhost:* wss://localhost:*" : "";
    var csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
              $"connect-src 'self'{(apiOrigen is null ? "" : " " + apiOrigen)}{recarga}; frame-ancestors 'none'";

    var app = builder.Build();
    app.UseSerilogRequestLogging();
    if (!app.Environment.IsDevelopment()) app.UseHsts();

    app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = csp;
        await next();
    });

    app.MapGet("/config.js", (HttpContext ctx) =>
    {
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.Text(configJs, "text/javascript; charset=utf-8");
    });

    app.UseDefaultFiles();
    // El navegador comprueba siempre si hay versión nueva (con ETag: si no ha cambiado, responde 304 sin reenviar el fichero),
    // para que tras actualizar la aplicación no se quede con el app.js o el index.html anteriores
    app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

    Log.Information("API en {Api}", apiUrl.Length > 0 ? apiUrl : "el mismo origen");
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "La aplicación web terminó de forma inesperada");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
