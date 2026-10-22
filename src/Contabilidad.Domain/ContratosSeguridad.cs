namespace Contabilidad.Domain;

// ---- Autenticación ----
public sealed class LoginCmd { public string? Email { get; set; } public string? Password { get; set; } }
public sealed class CambiarPasswordCmd { public string? Actual { get; set; } public string? Nueva { get; set; } }

public sealed class UsuarioDto
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "EMPRESA";           // GESTOR | EMPRESA
    public bool EsSuperadmin { get; set; }
    public bool Activo { get; set; }
    public bool DebeCambiarPassword { get; set; }
}

/// <summary>Datos necesarios para validar el login (incluye el hash: no sale nunca por la API).</summary>
public sealed class UsuarioLogin
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "EMPRESA";
    public bool EsSuperadmin { get; set; }
    public bool Activo { get; set; }
    public bool DebeCambiarPassword { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime? BloqueadoHasta { get; set; }

    public UsuarioDto ADto() => new()
    {
        Id = Id, Email = Email, Nombre = Nombre, Tipo = Tipo, EsSuperadmin = EsSuperadmin,
        Activo = Activo, DebeCambiarPassword = DebeCambiarPassword
    };
}

public sealed record LoginRespuesta(string Token, DateTime Expira, UsuarioDto Usuario);

// ---- Administración de usuarios ----
public sealed class CrearUsuarioCmd
{
    public string Email { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "EMPRESA";
    public string Password { get; set; } = "";              // temporal: el usuario debe cambiarla al entrar
}

public sealed class UsuarioAdminDto
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
    public bool EsSuperadmin { get; set; }
    public bool Activo { get; set; }
    public int NumEmpresas { get; set; }
    public DateTime? UltimoAcceso { get; set; }
}

public sealed class ActivarUsuarioCmd { public bool Activo { get; set; } }
public sealed class RestablecerPasswordCmd { public string Password { get; set; } = ""; }

public sealed class AsignarEmpresaCmd
{
    public string Rol { get; set; } = "LECTURA";                               // ADMIN | CONTABLE | LECTURA
    public bool SaltaRevision { get; set; }
    public Dictionary<string, string> Permisos { get; set; } = new();          // MODULO -> NINGUNO|LECTURA|ESCRITURA
}

public sealed class AsignacionDto
{
    public long EmpresaId { get; set; }
    public string EmpresaNombre { get; set; } = "";
    public string Rol { get; set; } = "";
    public bool SaltaRevision { get; set; }
    public Dictionary<string, string> Permisos { get; set; } = new();
}

// ---- Administración de empresas ----
public sealed class PlanModeloDto { public long Id { get; set; } public string Codigo { get; set; } = ""; public string Nombre { get; set; } = ""; public string Tipo { get; set; } = ""; }

public sealed class EmpresaAdminDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Nif { get; set; } = "";
    public string FormaJuridica { get; set; } = "";
    public string Plan { get; set; } = "";
    public string ImpuestoIndirecto { get; set; } = "";
    public bool Activa { get; set; }
}

/// <summary>
/// Datos editables de una empresa. NIF, plan contable, dígitos de cuenta e impuesto indirecto se muestran pero no se cambian (con
/// contabilidad hecha la descuadrarían). La provincia sale de los dos primeros dígitos del código postal.
/// </summary>
public sealed class EmpresaDatosDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nif { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? NombreComercial { get; set; }
    public string FormaJuridica { get; set; } = "";
    public string? Direccion { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Poblacion { get; set; }
    public string? Provincia { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    /// <summary>Sufijo del identificador de acreedor SEPA (3 caracteres).</summary>
    public string SepaSufijo { get; set; } = "000";
    public bool Activa { get; set; } = true;
    public string Plan { get; set; } = "";
    public string ImpuestoIndirecto { get; set; } = "";
    public int DigitosCuenta { get; set; }
    public List<ActividadEmpresaDto> Actividades { get; set; } = [];
}

/// <summary>Actividad de la empresa (epígrafe del IAE): base de la retención, la prorrata por sectores y los datos de los modelos.</summary>
public sealed class ActividadEmpresaDto
{
    public long? Id { get; set; }
    public int EpigrafeId { get; set; }
    public short Seccion { get; set; }
    public string EpigrafeCodigo { get; set; } = "";
    public string EpigrafeDescripcion { get; set; } = "";
    public string? ClaveActividad { get; set; }
    /// <summary>Nombre propio de la actividad (si no, la descripción del epígrafe).</summary>
    public string? Nombre { get; set; }
    public bool EsPrincipal { get; set; }
    /// <summary>Sector diferenciado a efectos de la prorrata (vacío = ninguno).</summary>
    public short? SectorDiferenciado { get; set; }
    public DateOnly FechaAlta { get; set; }
    public DateOnly? FechaBaja { get; set; }
    /// <summary>Tiene facturas, módulos o inmovilizado: no se puede borrar, solo dar de baja.</summary>
    public bool EnUso { get; set; }
}

public sealed class CrearEjercicioCmd
{
    public string Codigo { get; set; } = "";
    public DateOnly FechaInicio { get; set; }
    public DateOnly FechaFin { get; set; }
}

public sealed class CrearEmpresaCmd
{
    public string TipoDocumento { get; set; } = "CIF";
    public string Nif { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Codigo { get; set; } = "";
    public string FormaJuridica { get; set; } = "SL";
    public long PlanModeloId { get; set; }
    public int DigitosCuenta { get; set; } = 8;
    public string ImpuestoIndirecto { get; set; } = "IVA";     // IVA | IGIC | IPSI | EXENTO
    public bool TributaIs { get; set; }
    public CrearEjercicioCmd? PrimerEjercicio { get; set; }
}

public sealed record ResultadoAltaEmpresa(long EmpresaId, IReadOnlyList<string> Avisos);

// ---- Contexto de empresa ----
public sealed class PendienteDto
{
    public string Tipo { get; set; } = "";          // ASIENTO | FACTURA
    public long Id { get; set; }
    public string Referencia { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public string Descripcion { get; set; } = "";
    public decimal Importe { get; set; }
    public string CreadoPor { get; set; } = "";
}

public sealed class MotivoCmd { public string? Motivo { get; set; } }
