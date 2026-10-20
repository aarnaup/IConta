using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>Nóminas (asiento y retenciones del trabajo para el 111 y el 190) y datos de los trabajadores para el 190.</summary>
public static class NominasEndpoints
{
    public static void MapNominas(this IEndpointRouteBuilder app)
    {
        var emp = app.MapGroup("/api/empresas/{empresaId:long}").RequireAuthorization();
        var ej = emp.MapGroup("/ejercicios/{ejercicioId:long}");

        ej.MapGet("/nominas", (long empresaId, long ejercicioId, INominaRepositorio r, CancellationToken ct) => r.ListarAsync(empresaId, ejercicioId, ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad);

        ej.MapPost("/nominas", async (long empresaId, long ejercicioId, CrearNominaCmd cmd, HttpContext http, INominaRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var n = await r.CrearAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "NOMINA_CONTABILIZADA", "nomina", n.Id,
                new { n.TerceroId, n.Fecha, n.Bruto, n.Retencion, n.AsientoNumero }, ct);
            return Results.Created($"/api/empresas/{empresaId}/ejercicios/{ejercicioId}/nominas/{n.Id}", n);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);

        ej.MapDelete("/nominas/{nominaId:long}", async (long empresaId, long ejercicioId, long nominaId, HttpContext http, INominaRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAsync(empresaId, ejercicioId, nominaId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "NOMINA_ANULADA", "nomina", nominaId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);

        emp.MapGet("/empleados", (long empresaId, INominaRepositorio r, CancellationToken ct) => r.EmpleadosAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad, Modulo.Fiscal);

        emp.MapPut("/empleados/{terceroId:long}", async (long empresaId, long terceroId, EmpleadoDto e, HttpContext http, INominaRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            e.TerceroId = terceroId;
            await r.GuardarEmpleadoAsync(empresaId, e, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EMPLEADO_GUARDADO", "tercero", terceroId, null, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Contabilidad);
    }
}
