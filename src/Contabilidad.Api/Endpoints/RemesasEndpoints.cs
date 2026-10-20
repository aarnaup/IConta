using System.Text;
using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Remesas SEPA: cuentas bancarias de clientes y proveedores, mandatos de domiciliación, generación del fichero XML, contabilización
/// y devoluciones. Generar y anular: escritura en Tesorería. Contabilizar y devolver generan asientos: además, sin revisión.
/// </summary>
public static class RemesasEndpoints
{
    public static void MapRemesas(this IEndpointRouteBuilder app)
    {
        var emp = app.MapGroup("/api/empresas/{empresaId:long}").RequireAuthorization();

        emp.MapGet("/terceros/{terceroId:long}/cuentas-bancarias", (long empresaId, long terceroId, IRemesasRepositorio r, CancellationToken ct)
            => r.ListarCuentasTerceroAsync(empresaId, terceroId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria, Modulo.Facturacion);

        emp.MapPost("/terceros/{terceroId:long}/cuentas-bancarias", async (long empresaId, long terceroId, CrearCuentaTerceroCmd cmd, HttpContext http,
            IRemesasRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await r.CrearCuentaTerceroAsync(empresaId, terceroId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "CUENTA_TERCERO_CREADA", "tercero", terceroId, new { c.Iban }, ct);
            return Results.Created($"/api/empresas/{empresaId}/terceros/{terceroId}/cuentas-bancarias/{c.Id}", c);
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria, Modulo.Facturacion);

        var t = emp.MapGroup("/tesoreria");

        t.MapGet("/mandatos", (long empresaId, long? terceroId, IRemesasRepositorio r, CancellationToken ct) => r.ListarMandatosAsync(empresaId, terceroId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapPost("/mandatos", async (long empresaId, CrearMandatoCmd cmd, HttpContext http, IRemesasRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var m = await r.CrearMandatoAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "MANDATO_CREADO", "mandato", m.Id, new { m.Referencia, m.TerceroId }, ct);
            return Results.Created($"/api/empresas/{empresaId}/tesoreria/mandatos/{m.Id}", m);
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapPost("/mandatos/{mandatoId:long}/desactivar", async (long empresaId, long mandatoId, IRemesasRepositorio r, CancellationToken ct) =>
        {
            await r.DesactivarMandatoAsync(empresaId, mandatoId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapGet("/remesas/candidatos", (long empresaId, string tipo, DateOnly? hasta, IRemesasRepositorio r, CancellationToken ct)
            => r.CandidatosAsync(empresaId, tipo, hasta, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapGet("/remesas", (long empresaId, IRemesasRepositorio r, CancellationToken ct) => r.ListarAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapPost("/remesas", async (long empresaId, CrearRemesaCmd cmd, HttpContext http, IRemesasRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var d = await r.CrearAsync(empresaId, http.User.UsuarioId(), cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "REMESA_GENERADA", "remesa", d.Remesa.Id,
                new { d.Remesa.Referencia, d.Remesa.Tipo, d.Remesa.Importe, d.Remesa.NumOperaciones }, ct);
            return Results.Created($"/api/empresas/{empresaId}/tesoreria/remesas/{d.Remesa.Id}", d);
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        t.MapGet("/remesas/{remesaId:long}", async (long empresaId, long remesaId, IRemesasRepositorio r, CancellationToken ct)
            => Results.Ok(await r.DetalleAsync(empresaId, remesaId, ct)))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapGet("/remesas/{remesaId:long}/fichero", async (long empresaId, long remesaId, IRemesasRepositorio r, CancellationToken ct) =>
        {
            var (nombre, xml) = await r.FicheroAsync(empresaId, remesaId, ct);
            return Results.File(new UTF8Encoding(false).GetBytes(xml), "application/xml", nombre);
        }).Requiere(Nivel.Lectura, Modulo.Tesoreria);

        t.MapPost("/remesas/{remesaId:long}/anular", async (long empresaId, long remesaId, HttpContext http, IRemesasRepositorio r,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await r.AnularAsync(empresaId, remesaId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "REMESA_ANULADA", "remesa", remesaId, null, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        var ej = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/tesoreria/remesas").RequireAuthorization();

        ej.MapPost("/{remesaId:long}/contabilizar", async (long empresaId, long ejercicioId, long remesaId, ContabilizarRemesaCmd cmd, HttpContext http,
            IRemesasRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var a = await r.ContabilizarAsync(empresaId, ejercicioId, http.User.UsuarioId(), remesaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "REMESA_CONTABILIZADA", "remesa", remesaId, new { a.AsientoId }, ct);
            return Results.Ok(a);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);

        ej.MapPost("/lineas/{lineaId:long}/devolver", async (long empresaId, long ejercicioId, long lineaId, DevolucionReciboCmd cmd, HttpContext http,
            IRemesasRepositorio r, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var a = await r.DevolverAsync(empresaId, ejercicioId, http.User.UsuarioId(), lineaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "RECIBO_DEVUELTO", "remesa_vencimiento", lineaId, new { cmd.Motivo, cmd.Gastos, a.AsientoId }, ct);
            return Results.Ok(a);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);
    }
}
