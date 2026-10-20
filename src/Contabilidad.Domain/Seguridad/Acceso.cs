namespace Contabilidad.Domain.Seguridad;

public enum Modulo { Contabilidad, Facturacion, Tesoreria, Inmovilizado, Analitica, Presupuestos, Balances, Fiscal }
public enum Nivel { Ninguno = 0, Lectura = 1, Escritura = 2 }

/// <summary>
/// Lo que un usuario puede hacer en UNA empresa.
///  - Nivel por módulo: el explícito (core.usuario_empresa_permiso) o, si no hay, el que da el rol.
///  - Contabilizar directo: gestores, superadmin o usuarios con "salta_revision". El resto graba BORRADORES
///    que un revisor aprueba o rechaza.
/// </summary>
public sealed class AccesoEmpresa
{
    public static readonly IReadOnlyList<string> RolesValidos = ["ADMIN", "CONTABLE", "LECTURA"];

    private readonly IReadOnlyDictionary<Modulo, Nivel> _explicitos;

    public AccesoEmpresa(string rol, bool esGestor, bool saltaRevision, IReadOnlyDictionary<Modulo, Nivel>? explicitos = null)
    {
        Rol = rol;
        EsGestor = esGestor;
        SaltaRevision = saltaRevision;
        _explicitos = explicitos ?? new Dictionary<Modulo, Nivel>();
    }

    /// <summary>Acceso completo (superadmin).</summary>
    public static AccesoEmpresa Total() => new("ADMIN", esGestor: true, saltaRevision: true) { EsTotal = true };

    public string Rol { get; }
    public bool EsGestor { get; }
    public bool SaltaRevision { get; }
    public bool EsTotal { get; private init; }

    public bool PuedeContabilizarDirecto => EsTotal || EsGestor || SaltaRevision;

    public Nivel NivelEn(Modulo m)
    {
        if (EsTotal) return Nivel.Escritura;
        if (_explicitos.TryGetValue(m, out var n)) return n;
        return Rol switch
        {
            "ADMIN" or "CONTABLE" => Nivel.Escritura,
            "LECTURA" => Nivel.Lectura,
            _ => Nivel.Ninguno
        };
    }

    public bool Puede(Modulo m, Nivel minimo) => NivelEn(m) >= minimo;

    /// <summary>Puede aprobar o rechazar borradores de ese módulo.</summary>
    public bool PuedeRevisar(Modulo m) => Puede(m, Nivel.Escritura) && PuedeContabilizarDirecto;

    /// <summary>Niveles efectivos de todos los módulos (para que la UI muestre u oculte secciones).</summary>
    public IReadOnlyDictionary<string, string> Resumen()
        => Enum.GetValues<Modulo>().ToDictionary(m => m.ToString().ToUpperInvariant(), m => NivelEn(m).ToString().ToUpperInvariant());
}
