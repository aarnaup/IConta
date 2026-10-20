using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Tesorería. Los vencimientos pertenecen a la empresa (cruzan ejercicios) y se consultan sin ejercicio;
/// registrar un cobro o pago genera un asiento, así que va dentro del ejercicio elegido.
/// Cobrar y pagar contabiliza directamente: solo quien puede contabilizar sin revisión.
/// </summary>
public static class TesoreriaEndpoints
{
    public static void MapTesoreria(this IEndpointRouteBuilder app)
    {
        var emp = app.MapGroup("/api/empresas/{empresaId:long}").RequireAuthorization();

        // ---- Formas de pago ----
        emp.MapGet("/formas-pago", (long empresaId, ITesoreriaRepositorio t, CancellationToken ct)
            => t.ListarFormasPagoAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria, Modulo.Facturacion);

        emp.MapPost("/formas-pago", async (long empresaId, CrearFormaPagoCmd cmd, HttpContext http, ITesoreriaRepositorio t,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var f = await t.CrearFormaPagoAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "FORMA_PAGO_CREADA", "forma_pago", f.Id, new { f.Codigo }, ct);
            return Results.Created($"/api/empresas/{empresaId}/formas-pago/{f.Id}", f);
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        emp.MapPut("/formas-pago/{id:long}", async (long empresaId, long id, FormaPagoDto f, HttpContext http, ICatalogosAdminRepositorio c,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            f.Id = id;
            await c.GuardarFormaPagoAsync(empresaId, f, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "FORMA_PAGO_MODIFICADA", "forma_pago", id, new { f.Codigo }, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        emp.MapDelete("/formas-pago/{id:long}", async (long empresaId, long id, HttpContext http, ICatalogosAdminRepositorio c,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.EliminarFormaPagoAsync(empresaId, id, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "FORMA_PAGO_ELIMINADA", "forma_pago", id, null, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Tesoreria);

        emp.MapPut("/terceros/{terceroId:long}/forma-pago", async (long empresaId, long terceroId, AsignarFormaPagoCmd cmd,
            ITesoreriaRepositorio t, CancellationToken ct) =>
        {
            await t.AsignarFormaPagoTerceroAsync(empresaId, terceroId, cmd.FormaPagoId, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Facturacion);

        // ---- Bancos ----
        emp.MapGet("/tesoreria/cuentas-bancarias", (long empresaId, ITesoreriaRepositorio t, CancellationToken ct)
            => t.ListarCuentasBancariasAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        // Enlaza un banco con una cuenta 572 del plan: es configuración contable, no un borrador
        emp.MapPost("/tesoreria/cuentas-bancarias", async (long empresaId, CrearCuentaBancariaCmd cmd, HttpContext http,
            ITesoreriaRepositorio t, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var c = await t.CrearCuentaBancariaAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "CUENTA_BANCARIA_CREADA", "cuenta_bancaria", c.Id, new { c.Iban }, ct);
            return Results.Created($"/api/empresas/{empresaId}/tesoreria/cuentas-bancarias/{c.Id}", c);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);

        // ---- Vencimientos ----
        emp.MapGet("/tesoreria/vencimientos", (long empresaId, string? tipo, string? estado, DateOnly? desde, DateOnly? hasta, long? terceroId,
            int? limite, ITesoreriaRepositorio t, CancellationToken ct)
            => t.ListarVencimientosAsync(empresaId, new FiltroVencimientos
            {
                Tipo = string.IsNullOrWhiteSpace(tipo) ? null : tipo, Estado = string.IsNullOrWhiteSpace(estado) ? null : estado,
                Desde = desde, Hasta = hasta, TerceroId = terceroId, Limite = limite ?? 200
            }, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        emp.MapGet("/tesoreria/prevision", (long empresaId, int? meses, ITesoreriaRepositorio t, CancellationToken ct)
            => t.PrevisionAsync(empresaId, DateOnly.FromDateTime(DateTime.Today), meses ?? 6, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        emp.MapGet("/tesoreria/vencimientos/{vencimientoId:long}/cobros", (long empresaId, long vencimientoId, ITesoreriaRepositorio t, CancellationToken ct)
            => t.CobrosAsync(empresaId, vencimientoId, ct))
            .Requiere(Nivel.Lectura, Modulo.Tesoreria);

        emp.MapPost("/ejercicios/{ejercicioId:long}/tesoreria/cobros/{cobroPagoId:long}/extornar", async (long empresaId, long ejercicioId,
            long cobroPagoId, HttpContext http, ITesoreriaRepositorio t, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await t.ExtornarAsync(empresaId, ejercicioId, cobroPagoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "COBRO_PAGO_EXTORNADO", "cobro_pago", cobroPagoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);

        emp.MapPost("/tesoreria/vencimientos/{vencimientoId:long}/anular", async (long empresaId, long vencimientoId, HttpContext http,
            ITesoreriaRepositorio t, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await t.AnularVencimientoAsync(empresaId, vencimientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "VENCIMIENTO_ANULADO", "vencimiento", vencimientoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);

        // ---- Cobro / pago: genera el asiento en el ejercicio elegido ----
        emp.MapPost("/ejercicios/{ejercicioId:long}/tesoreria/vencimientos/{vencimientoId:long}/liquidar",
            async (long empresaId, long ejercicioId, long vencimientoId, LiquidarVencimientoCmd cmd, HttpContext http,
                   ITesoreriaRepositorio t, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var r = await t.LiquidarAsync(empresaId, ejercicioId, http.User.UsuarioId(), vencimientoId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "VENCIMIENTO_LIQUIDADO", "vencimiento", vencimientoId,
                new { cmd.Fecha, importe = cmd.Importe, r.AsientoNumero }, ct);
            return Results.Ok(r);
        }).RequiereDirecto(Nivel.Escritura, Modulo.Tesoreria);
    }
}
