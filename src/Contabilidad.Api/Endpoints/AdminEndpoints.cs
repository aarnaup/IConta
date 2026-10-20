using Contabilidad.Api.Seguridad;
using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>Área de administración (gestores). Las altas de usuarios y empresas solo las hace un superadmin.</summary>
public static class AdminEndpoints
{
    private static readonly string[] Tipos = ["GESTOR", "EMPRESA"];

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization();

        admin.MapGet("/planes-modelo", (IEmpresaAdminRepositorio e, CancellationToken ct) => e.ListarPlanesAsync(ct)).SoloGestor();

        // ---- Empresas ----
        admin.MapGet("/empresas", (IEmpresaAdminRepositorio e, CancellationToken ct) => e.ListarAsync(ct)).SoloSuperadmin();

        admin.MapPost("/empresas", async (CrearEmpresaCmd cmd, HttpContext http, IEmpresaAdminRepositorio e,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var r = await e.CrearAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), r.EmpresaId, "EMPRESA_CREADA", "empresa", r.EmpresaId, new { cmd.Codigo, cmd.Nombre }, ct);
            return Results.Created($"/api/admin/empresas/{r.EmpresaId}", r);
        }).SoloSuperadmin();

        admin.MapPost("/empresas/{empresaId:long}/ejercicios", async (long empresaId, CrearEjercicioCmd cmd, HttpContext http,
            IEmpresaAdminRepositorio e, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var id = await e.CrearEjercicioAsync(empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EJERCICIO_CREADO", "ejercicio", id, new { cmd.Codigo, cmd.FechaInicio, cmd.FechaFin }, ct);
            return Results.Created($"/api/empresas/{empresaId}/ejercicios/{id}", new { id });
        }).SoloSuperadmin();

        // ---- Usuarios ----
        admin.MapGet("/usuarios", (IUsuarioRepositorio u, CancellationToken ct) => u.ListarAsync(ct)).SoloSuperadmin();

        admin.MapPost("/usuarios", async (CrearUsuarioCmd cmd, HttpContext http, IUsuarioRepositorio u, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var email = (cmd.Email ?? "").Trim();
            var nombre = (cmd.Nombre ?? "").Trim();
            if (email.Length is 0 or > 200 || !email.Contains('@') || email.Contains(' '))
                throw new ReglaContableException("El email no es válido.");
            if (nombre.Length is 0 or > 150) throw new ReglaContableException("El nombre es obligatorio (máximo 150 caracteres).");
            if (!Tipos.Contains(cmd.Tipo)) throw new ReglaContableException("El tipo debe ser GESTOR o EMPRESA.");
            HasheadorPassword.ValidarPolitica(cmd.Password, email);

            var id = await u.CrearAsync(email, nombre, cmd.Tipo, false, HasheadorPassword.Hash(cmd.Password), debeCambiar: true, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "USUARIO_CREADO", "usuario", id, new { email, cmd.Tipo }, ct);
            return Results.Created($"/api/admin/usuarios/{id}", new { id });
        }).SoloSuperadmin();

        admin.MapPut("/usuarios/{id:long}/activo", async (long id, ActivarUsuarioCmd cmd, HttpContext http, IUsuarioRepositorio u,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            if (id == http.User.UsuarioId() && !cmd.Activo) throw new ReglaContableException("No puedes desactivar tu propio usuario.");
            await u.SetActivoAsync(id, cmd.Activo, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, cmd.Activo ? "USUARIO_ACTIVADO" : "USUARIO_DESACTIVADO", "usuario", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        admin.MapPost("/usuarios/{id:long}/password", async (long id, RestablecerPasswordCmd cmd, HttpContext http, IUsuarioRepositorio u,
            IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var destino = await u.ObtenerAsync(id, ct) ?? throw new ReglaContableException("El usuario no existe.");
            HasheadorPassword.ValidarPolitica(cmd.Password, destino.Email);
            await u.CambiarPasswordAsync(id, HasheadorPassword.Hash(cmd.Password), debeCambiar: true, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "PASSWORD_RESTABLECIDA", "usuario", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        admin.MapGet("/usuarios/{id:long}/empresas", (long id, IUsuarioRepositorio u, CancellationToken ct) => u.ListarAsignacionesAsync(id, ct)).SoloSuperadmin();

        admin.MapPut("/usuarios/{id:long}/empresas/{empresaId:long}", async (long id, long empresaId, AsignarEmpresaCmd cmd, HttpContext http,
            IUsuarioRepositorio u, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await u.AsignarEmpresaAsync(id, empresaId, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "PERMISOS_ASIGNADOS", "usuario", id,
                new { cmd.Rol, cmd.SaltaRevision, cmd.Permisos }, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        admin.MapDelete("/usuarios/{id:long}/empresas/{empresaId:long}", async (long id, long empresaId, HttpContext http,
            IUsuarioRepositorio u, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await u.DesasignarEmpresaAsync(id, empresaId, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), empresaId, "EMPRESA_DESASIGNADA", "usuario", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        // ---- Catálogos comunes ----
        var cat = admin.MapGroup("/catalogos");
        cat.MapGet("/tipos-iva", (ICatalogosAdminRepositorio c, CancellationToken ct) => c.TiposImpuestoAsync(ct)).SoloGestor();
        cat.MapPost("/tipos-iva", async (TipoImpuestoCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var id = await c.CrearTipoImpuestoAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "TIPO_IVA_CREADO", "tipo_iva", id, cmd, ct);
            return Results.Created($"/api/admin/catalogos/tipos-iva/{id}", new { id });
        }).SoloSuperadmin();
        cat.MapPut("/tipos-iva/{id:int}", async (int id, TipoImpuestoCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.ModificarTipoImpuestoAsync(id, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "TIPO_IVA_MODIFICADO", "tipo_iva", id, cmd, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
        cat.MapPost("/tipos-iva/{id:int}/vigencias", async (int id, NuevaVigenciaCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.NuevaVigenciaImpuestoAsync(id, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "TIPO_IVA_VIGENCIA", "tipo_iva", id, cmd, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        cat.MapGet("/tipos-retencion", (ICatalogosAdminRepositorio c, CancellationToken ct) => c.TiposRetencionAsync(ct)).SoloGestor();
        cat.MapPost("/tipos-retencion", async (TipoRetencionCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var id = await c.CrearTipoRetencionAsync(cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "TIPO_RETENCION_CREADO", "tipo_retencion", id, cmd, ct);
            return Results.Created($"/api/admin/catalogos/tipos-retencion/{id}", new { id });
        }).SoloSuperadmin();
        cat.MapPut("/tipos-retencion/{id:int}", async (int id, TipoRetencionCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.ModificarTipoRetencionAsync(id, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "TIPO_RETENCION_MODIFICADO", "tipo_retencion", id, cmd, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
        cat.MapPost("/tipos-retencion/{id:int}/vigencias", async (int id, NuevaVigenciaCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.NuevaVigenciaRetencionAsync(id, cmd, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "TIPO_RETENCION_VIGENCIA", "tipo_retencion", id, cmd, ct);
            return Results.NoContent();
        }).SoloSuperadmin();

        cat.MapGet("/paises", (ICatalogosAdminRepositorio c, CancellationToken ct) => c.PaisesAsync(ct)).SoloGestor();
        cat.MapPut("/paises", async (PaisDto p, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.GuardarPaisAsync(p, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "PAIS_GUARDADO", "pais", null, p, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
        cat.MapGet("/bancos", (ICatalogosAdminRepositorio c, CancellationToken ct) => c.BancosAsync(ct)).SoloGestor();
        cat.MapPut("/bancos", async (BancoDto b, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var id = await c.GuardarBancoAsync(b, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "BANCO_GUARDADO", "banco", id, b, ct);
            return Results.Ok(new { id });
        }).SoloSuperadmin();
        cat.MapGet("/epigrafes", (string? q, ICatalogosAdminRepositorio c, CancellationToken ct) => c.EpigrafesAsync(q, ct)).SoloGestor();
        cat.MapPut("/epigrafes", async (EpigrafeDto e, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            var id = await c.GuardarEpigrafeAsync(e, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, "EPIGRAFE_GUARDADO", "epigrafe_iae", id, e, ct);
            return Results.Ok(new { id });
        }).SoloSuperadmin();
        cat.MapGet("/modelos-fiscales", (ICatalogosAdminRepositorio c, CancellationToken ct) => c.ModelosFiscalesAsync(ct)).SoloGestor();
        cat.MapPut("/modelos-fiscales/{id:int}/activo", async (int id, ActivarUsuarioCmd cmd, HttpContext http, ICatalogosAdminRepositorio c, IAuditoriaRepositorio aud, CancellationToken ct) =>
        {
            await c.ActivarModeloFiscalAsync(id, cmd.Activo, ct);
            await aud.RegistrarAsync(http.User.UsuarioId(), null, cmd.Activo ? "MODELO_ACTIVADO" : "MODELO_DESACTIVADO", "modelo_fiscal", id, null, ct);
            return Results.NoContent();
        }).SoloSuperadmin();
    }
}
