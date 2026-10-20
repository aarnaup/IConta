using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Contabilidad.Api;

/// <summary>
/// Documento OpenAPI (/openapi/v1.json) y Swagger UI (/swagger). El documento se importa en Postman tal cual:
/// lleva el esquema Bearer (JWT de POST /api/auth/login) y agrupa las rutas por módulo.
/// </summary>
public static class DocumentacionApi
{
    public static IServiceCollection AddDocumentacionApi(this IServiceCollection services)
        => services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer<CabeceraYSeguridad>();
            o.AddOperationTransformer((op, ctx, _) =>
            {
                op.Tags = new HashSet<OpenApiTagReference> { new(Modulo(ctx.Description.RelativePath ?? "")) };
                // Rutas públicas (login…): sin candado
                var meta = ctx.Description.ActionDescriptor.EndpointMetadata;
                if (meta.OfType<IAllowAnonymous>().Any() || !meta.OfType<IAuthorizeData>().Any()) op.Security = [];
                return Task.CompletedTask;
            });
        });

    public static void MapDocumentacionApi(this WebApplication app)
    {
        app.MapOpenApi("/openapi/{documentName}.json");
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("/openapi/v1.json", "Contabilidad API v1");
            o.RoutePrefix = "swagger";
            o.DocumentTitle = "Contabilidad · API";
            o.EnablePersistAuthorization();
        });
    }

    /// <summary>Etiqueta por módulo: lo que sigue a /api, /api/empresas/{id} o /api/empresas/{id}/ejercicios/{ej}.</summary>
    internal static string Modulo(string ruta)
    {
        var s = ruta.Trim('/').Split('/');
        int i = s.Length > 1 && s[0] == "api" ? 1 : 0;
        if (s.Length > i + 2 && s[i] == "empresas" && s[i + 1].StartsWith('{'))
        {
            i += 2;
            if (s.Length > i + 2 && s[i] == "ejercicios" && s[i + 1].StartsWith('{')) i += 2;
        }
        var m = i < s.Length && !s[i].StartsWith('{') ? s[i] : "empresas";
        return m.Length == 0 ? "general" : char.ToUpperInvariant(m[0]) + m[1..];
    }

    private sealed class CabeceraYSeguridad : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument doc, OpenApiDocumentTransformerContext context, CancellationToken ct)
        {
            doc.Info = new OpenApiInfo
            {
                Title = "Contabilidad API",
                Version = "v1",
                Description = "Contabilidad, facturación, tesorería, fiscal e inmovilizado. Autenticación: POST /api/auth/login con " +
                              "{ email, password } devuelve { token }; envíalo como «Authorization: Bearer <token>». Los errores de negocio " +
                              "son 400 con { error, detalle }."
            };
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "Token de POST /api/auth/login."
            };
            doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] }];
            return Task.CompletedTask;
        }
    }
}
