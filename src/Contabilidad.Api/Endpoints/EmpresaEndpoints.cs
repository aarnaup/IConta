using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Canales;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Área de empresa. Dos niveles de ruta:
///   /api/empresas/{empresaId}/...                           datos de la empresa (catálogos, terceros)
///   /api/empresas/{empresaId}/ejercicios/{ejercicioId}/...  trabajo contable dentro del ejercicio elegido
/// Quien no puede contabilizar sin revisión graba BORRADORES; un revisor los aprueba o rechaza.
/// </summary>
public static class EmpresaEndpoints
{
    public static void MapEmpresa(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/mis-empresas", (HttpContext http, IAccesoRepositorio a, CancellationToken ct)
            => a.ListarEmpresasAsync(http.User.UsuarioId(), ct)).RequireAuthorization();

        var emp = app.MapGroup("/api/empresas/{empresaId:long}").RequireAuthorization();

        emp.MapGet("/mi-acceso", (HttpContext http) =>
        {
            var a = http.Acceso();
            return Results.Ok(new { rol = a.Rol, puedeContabilizarDirecto = a.PuedeContabilizarDirecto, permisos = a.Resumen() });
        }).RequiereAcceso();

        emp.MapGet("/ejercicios", (long empresaId, ICatalogoRepositorio c, CancellationToken ct)
            => c.ListarEjerciciosAsync(empresaId, ct)).RequiereAcceso();

        // ---- Plan, diarios y canales ----
        emp.MapGet("/cuentas", (long empresaId, string? q, ICatalogoRepositorio c, CancellationToken ct)
            => c.ListarCuentasAsync(empresaId, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad, Modulo.Facturacion);

        emp.MapGet("/diarios", (long empresaId, ICatalogoRepositorio c, CancellationToken ct)
            => c.ListarDiariosAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad, Modulo.Facturacion);

        emp.MapGet("/canales", (long empresaId, ICanalRepositorio c, CancellationToken ct)
            => c.ListarAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad, Modulo.Facturacion);

        // Vista previa del reparto de un importe entre canales base (no guarda nada)
        emp.MapPost("/canales/simular", async (long empresaId, SimularCanalCmd cmd, ICanalRepositorio canales,
            RepartidorCanales repartidor, CancellationToken ct) =>
        {
            var cfg = await canales.ObtenerConfiguracionAsync(empresaId, cmd.CanalId, cmd.Fecha, ct);
            return Results.Ok(repartidor.Repartir(cfg, cmd.Debe, cmd.Haber));
        }).Requiere(Nivel.Lectura, Modulo.Contabilidad, Modulo.Facturacion);

        // Cambiar una fórmula altera cómo se reparte la contabilidad: solo quien contabiliza sin revisión
        emp.MapPost("/canales/{canalId:long}/versiones", async (long empresaId, long canalId, NuevaVersionCanalCmd cmd,
            HttpContext http, ICanalRepositorio canales, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var id = await canales.CrearVersionAsync(empresaId, canalId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "CANAL_VERSION_CREADA", "canal", canalId, new { cmd.VigenteDesde }, ct);
            return Results.Created($"/api/empresas/{empresaId}/canales/{canalId}/versiones/{id}", new { id });
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);

        // ---- Terceros y catálogos fiscales ----
        emp.MapGet("/terceros", (long empresaId, string? tipo, string? q, ITerceroRepositorio t, CancellationToken ct)
            => t.ListarAsync(empresaId, string.IsNullOrWhiteSpace(tipo) ? null : tipo, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), ct))
            .Requiere(Nivel.Lectura, Modulo.Facturacion, Modulo.Contabilidad);

        emp.MapPost("/terceros", async (long empresaId, CrearTerceroCmd cmd, HttpContext http, ITerceroRepositorio t,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var creado = await t.CrearAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "TERCERO_CREADO", "tercero", creado.Id, new { creado.Nif }, ct);
            return Results.Created($"/api/empresas/{empresaId}/terceros/{creado.Id}", creado);
        }).Requiere(Nivel.Escritura, Modulo.Facturacion);

        emp.MapPut("/terceros/{terceroId:long}/criterio-caja", async (long empresaId, long terceroId, CriterioCajaTerceroCmd cmd, HttpContext http,
            ITerceroRepositorio t, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await t.MarcarCriterioCajaAsync(empresaId, terceroId, cmd.CriterioCaja, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "TERCERO_CRITERIO_CAJA", "tercero", terceroId, new { cmd.CriterioCaja }, ct);
            return Results.NoContent();
        }).Requiere(Nivel.Escritura, Modulo.Facturacion);

        emp.MapGet("/tipos-iva", (long empresaId, DateOnly? fecha, ICatalogoFacturacionRepositorio c, CancellationToken ct)
            => c.ListarTiposIvaAsync(empresaId, fecha ?? DateOnly.FromDateTime(DateTime.Today), ct))
            .Requiere(Nivel.Lectura, Modulo.Facturacion);

        emp.MapGet("/tipos-retencion", (DateOnly? fecha, ICatalogoFacturacionRepositorio c, CancellationToken ct)
            => c.ListarTiposRetencionAsync(fecha ?? DateOnly.FromDateTime(DateTime.Today), ct))
            .Requiere(Nivel.Lectura, Modulo.Facturacion);

        emp.MapGet("/plantillas", (long empresaId, ICatalogoFacturacionRepositorio c, CancellationToken ct)
            => c.ListarPlantillasAsync(empresaId, ct))
            .Requiere(Nivel.Lectura, Modulo.Facturacion);

        // =====================================================================
        // Trabajo dentro del ejercicio elegido
        // =====================================================================
        var ej = emp.MapGroup("/ejercicios/{ejercicioId:long}");

        // ---- Asientos ----
        ej.MapPost("/asientos", async (long empresaId, long ejercicioId, CrearAsientoCmd cmd, HttpContext http,
            IAsientoRepositorio asientos, CancellationToken ct) =>
        {
            var forzado = !cmd.Borrador && !http.Acceso().PuedeContabilizarDirecto;
            if (forzado) cmd.Borrador = true;      // sin permiso para contabilizar directo => borrador a revisar
            var creado = await asientos.CrearAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            return Results.Created($"/api/empresas/{empresaId}/ejercicios/{ejercicioId}/asientos/{creado.Id}",
                new { creado.Id, creado.Numero, creado.Estado, forzadoBorrador = forzado });
        }).Requiere(Nivel.Escritura, Modulo.Contabilidad);

        ej.MapGet("/asientos", (long empresaId, long ejercicioId, int? limite, IAsientoRepositorio a, CancellationToken ct)
            => a.ListarAsync(empresaId, ejercicioId, Math.Clamp(limite ?? 50, 1, 500), ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad);

        ej.MapGet("/mayor", (long empresaId, long ejercicioId, string cuenta, long? canalBaseId, IAsientoRepositorio a, CancellationToken ct)
            => a.MayorAsync(empresaId, ejercicioId, cuenta, canalBaseId, ct))
            .Requiere(Nivel.Lectura, Modulo.Contabilidad);

        // ---- Facturas ----
        ej.MapPost("/facturas/previsualizar", async (long empresaId, long ejercicioId, CrearFacturaCmd cmd, IFacturaRepositorio f, CancellationToken ct)
            => Results.Ok(await f.PrevisualizarAsync(empresaId, ejercicioId, cmd, ct)))
            .Requiere(Nivel.Lectura, Modulo.Facturacion);

        ej.MapPost("/facturas", async (long empresaId, long ejercicioId, CrearFacturaCmd cmd, HttpContext http,
            IFacturaRepositorio f, CancellationToken ct) =>
        {
            var forzado = !cmd.Borrador && !http.Acceso().PuedeContabilizarDirecto;
            if (forzado) cmd.Borrador = true;
            var creada = await f.CrearAsync(empresaId, ejercicioId, http.User.UsuarioId(), cmd, ct);
            return Results.Created($"/api/empresas/{empresaId}/ejercicios/{ejercicioId}/facturas/{creada.Id}",
                new { creada.Id, creada.AsientoId, creada.AsientoNumero, creada.Estado, creada.Totales, forzadoBorrador = forzado });
        }).Requiere(Nivel.Escritura, Modulo.Facturacion);

        ej.MapGet("/facturas", (long empresaId, long ejercicioId, string? libro, int? limite, IFacturaRepositorio f, CancellationToken ct)
            => f.ListarAsync(empresaId, ejercicioId, string.IsNullOrWhiteSpace(libro) ? null : libro, Math.Clamp(limite ?? 50, 1, 500), ct))
            .Requiere(Nivel.Lectura, Modulo.Facturacion);

        // ---- Revisión de borradores ----
        ej.MapGet("/pendientes", async (long empresaId, long ejercicioId, HttpContext http,
            IAsientoRepositorio asientos, IFacturaRepositorio facturas, CancellationToken ct) =>
        {
            var acceso = http.Acceso();
            var lista = new List<PendienteDto>();
            if (acceso.Puede(Modulo.Contabilidad, Nivel.Lectura)) lista.AddRange(await asientos.ListarPendientesAsync(empresaId, ejercicioId, ct));
            if (acceso.Puede(Modulo.Facturacion, Nivel.Lectura)) lista.AddRange(await facturas.ListarPendientesAsync(empresaId, ejercicioId, ct));
            return lista.OrderBy(p => p.Fecha).ThenBy(p => p.Id).ToList();
        }).RequiereAcceso();

        ej.MapPost("/asientos/{asientoId:long}/aprobar", async (long empresaId, long ejercicioId, long asientoId, HttpContext http,
            IAsientoRepositorio a, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await a.AprobarAsync(empresaId, ejercicioId, asientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "ASIENTO_APROBADO", "asiento", asientoId, null, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);

        ej.MapPost("/asientos/{asientoId:long}/rechazar", async (long empresaId, long ejercicioId, long asientoId, MotivoCmd? motivo, HttpContext http,
            IAsientoRepositorio a, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await a.RechazarAsync(empresaId, ejercicioId, asientoId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "ASIENTO_RECHAZADO", "asiento", asientoId, new { motivo = motivo?.Motivo }, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Contabilidad);

        ej.MapPost("/facturas/{facturaId:long}/aprobar", async (long empresaId, long ejercicioId, long facturaId, HttpContext http,
            IFacturaRepositorio f, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var r = await f.AprobarAsync(empresaId, ejercicioId, facturaId, http.User.UsuarioId(), ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "FACTURA_APROBADA", "factura", facturaId, new { r.AsientoNumero }, ct);
            return Results.Ok(new { r.Id, r.AsientoId, r.AsientoNumero, r.Estado });
        }).RequiereDirecto(Nivel.Escritura, Modulo.Facturacion);

        ej.MapPost("/facturas/{facturaId:long}/rechazar", async (long empresaId, long ejercicioId, long facturaId, MotivoCmd? motivo, HttpContext http,
            IFacturaRepositorio f, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await f.RechazarAsync(empresaId, ejercicioId, facturaId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "FACTURA_RECHAZADA", "factura", facturaId, new { motivo = motivo?.Motivo }, ct);
            return Results.NoContent();
        }).RequiereDirecto(Nivel.Escritura, Modulo.Facturacion);
    }
}
