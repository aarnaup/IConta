namespace Contabilidad.Domain;

public sealed class ImportarA3Cmd
{
    /// <summary>Fichero .zip en base64 con la carpeta de datos de a3ECO (o la de la empresa y TCLIPRO.DAT).</summary>
    public string Contenido { get; set; } = "";
    /// <summary>Carpeta de la empresa en a3 (E00001…); obligatoria si el zip trae varias.</summary>
    public string? Carpeta { get; set; }
    /// <summary>Años que se importan; en el análisis no se usa.</summary>
    public List<int>? Anios { get; set; }
}

public sealed record EmpresaA3Dto(string Carpeta, string Nombre, string Nif);

public sealed record AnioA3Dto(
    int Anio, string? Ejercicio, bool CrearEjercicio, int Subcuentas, int SubcuentasNuevas, int Asientos, int Apuntes,
    IReadOnlyList<int> AsientosDescuadrados, decimal AperturaDebe, decimal AperturaHaber, bool Comprobado,
    IReadOnlyList<string> CuentasNoCoinciden, bool Importable, string? Motivo);

public sealed record AnalisisA3Dto(
    string Carpeta, IReadOnlyList<EmpresaA3Dto> Empresas, EmpresaA3Dto? Empresa, int DigitosA3, int DigitosEmpresa,
    int Terceros, int TercerosNuevos, IReadOnlyList<AnioA3Dto> Anios, IReadOnlyList<string> Avisos);

public sealed record AnioImportadoA3Dto(int Anio, long EjercicioId, bool EjercicioCreado, int Asientos, int Borradores, bool Apertura);

public sealed record ImportacionA3Dto(int CuentasCreadas, int TercerosCreados, int TercerosActualizados,
    IReadOnlyList<AnioImportadoA3Dto> Anios, IReadOnlyList<string> Avisos);
