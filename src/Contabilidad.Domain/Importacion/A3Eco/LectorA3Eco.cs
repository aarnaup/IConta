using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Contabilidad.Domain.Importacion.A3Eco;

/// <summary>Empresa del directorio de a3ECO (TECODIR.DAT).</summary>
public sealed record EmpresaA3(string Carpeta, string Nombre, string Nif);

/// <summary>Cliente, proveedor o acreedor del fichero común TCLIPRO.DAT.</summary>
public sealed record TerceroA3(int Codigo, string Nif, string Nombre, string? Direccion, string? CodigoPostal, string? Poblacion, string? Telefono);

/// <summary>
/// Cuenta del plan de un año (fichero «nnnnnaCU»). Las subcuentas (nivel 5) llevan un código de 12 dígitos: la cuenta de 4 y el
/// subnúmero de 8 (430000070000). Importes en céntimos: apertura y sumas del debe y del haber de cada mes del ejercicio.
/// </summary>
public sealed record CuentaA3(string Codigo, bool EsSubcuenta, string Nombre, int? Tercero, long AperturaDebe, long AperturaHaber,
    long[] DebeMes, long[] HaberMes);

/// <summary>Apunte de un asiento (ficheros mensuales «nnnnnamA»). Importe en céntimos.</summary>
public sealed record ApunteA3(DateOnly Fecha, int Asiento, string Cuenta, bool Debe, long Importe, string Concepto, string Documento);

public sealed record AsientoA3(int Numero, DateOnly Fecha, IReadOnlyList<ApunteA3> Apuntes)
{
    public long Debe => Apuntes.Where(a => a.Debe).Sum(a => a.Importe);
    public long Haber => Apuntes.Where(a => !a.Debe).Sum(a => a.Importe);
    public bool Cuadra => Debe == Haber;
}

public sealed record AnioA3(int Anio, DateOnly Inicio, IReadOnlyList<CuentaA3> Cuentas, IReadOnlyList<AsientoA3> Asientos)
{
    public DateOnly Fin => Inicio.AddYears(1).AddDays(-1);

    /// <summary>
    /// Subcuentas cuyas sumas mensuales de los asientos leídos no coinciden con las que guarda a3 en el plan de cuentas.
    /// Si la lista está vacía, se han leído todos los apuntes del año.
    /// </summary>
    public IReadOnlyList<string> Descuadres()
    {
        var calc = new Dictionary<string, (long[] D, long[] H)>();
        foreach (var a in Asientos.SelectMany(x => x.Apuntes))
        {
            if (!calc.TryGetValue(a.Cuenta, out var s)) calc[a.Cuenta] = s = (new long[12], new long[12]);
            var mes = ((a.Fecha.Year - Inicio.Year) * 12 + a.Fecha.Month - Inicio.Month) % 12;
            if (a.Debe) s.D[mes] += a.Importe; else s.H[mes] += a.Importe;
        }
        var subcuentas = Cuentas.Where(c => c.EsSubcuenta).ToDictionary(c => c.Codigo);
        var mal = new List<string>();
        foreach (var c in subcuentas.Values)
        {
            var s = calc.GetValueOrDefault(c.Codigo, (new long[12], new long[12]));
            if (!s.Item1.SequenceEqual(c.DebeMes) || !s.Item2.SequenceEqual(c.HaberMes)) mal.Add(c.Codigo);
        }
        mal.AddRange(calc.Keys.Where(k => !subcuentas.ContainsKey(k)));
        return mal.Order(StringComparer.Ordinal).ToList();
    }
}

public sealed record DatosA3(string Carpeta, EmpresaA3? Empresa, IReadOnlyList<TerceroA3> Terceros, IReadOnlyList<AnioA3> Anios,
    int DigitosCuenta, IReadOnlyList<string> Avisos);

/// <summary>
/// Lee la contabilidad de una empresa de a3ECO (versión con ficheros ISAM de Micro Focus) a partir de su carpeta de datos
/// (Ennnnn) y de los ficheros comunes de la carpeta de a3ECO (TCLIPRO.DAT y TECODIR.DAT).
/// Los ficheros de la empresa se llaman «nnnnn» + último dígito del año + tipo: CU (plan de cuentas con saldos),
/// 1…9, O, N, I + «A» (asientos de cada mes). Se leen ejercicios naturales (de enero a diciembre).
/// </summary>
public static partial class LectorA3Eco
{
    private static readonly Encoding Texto = Encoding.Latin1;

    [GeneratedRegex(@"(^|/)(E\d{5})/[^/]+$", RegexOptions.IgnoreCase)]
    private static partial Regex RutaEmpresa();

    /// <summary>Carpetas de empresa (E00001, E00002…) que hay entre los ficheros.</summary>
    public static IReadOnlyList<string> Carpetas(IReadOnlyDictionary<string, byte[]> ficheros)
        => ficheros.Keys.Select(k => RutaEmpresa().Match(k.Replace('\\', '/'))).Where(m => m.Success)
            .Select(m => m.Groups[2].Value.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Todas las empresas del directorio de a3ECO (TECODIR.DAT), si viene en los ficheros.</summary>
    public static IReadOnlyList<EmpresaA3> Empresas(IReadOnlyDictionary<string, byte[]> ficheros)
    {
        var dir = Buscar(ficheros, "TECODIR.DAT");
        if (dir is null) return [];
        return FicheroIsam.Registros(dir, "TECODIR.DAT").Where(r => r.Length >= 141).Select(r =>
        {
            var ruta = Txt(r, 101, 40).Replace('\\', '/').TrimEnd('/');
            return new EmpresaA3(ruta[(ruta.LastIndexOf('/') + 1)..].ToUpperInvariant(), Txt(r, 3, 40), Nif(Txt(r, 47, 14)));
        }).ToList();
    }

    /// <param name="hoy">Para deducir el año de un fichero sin asientos ni fecha de inicio (solo lleva su último dígito).</param>
    public static DatosA3 Leer(IReadOnlyDictionary<string, byte[]> ficheros, string? carpeta, DateOnly hoy)
    {
        var carpetas = Carpetas(ficheros);
        if (carpetas.Count == 0)
            throw new ReglaContableException("No hay ninguna carpeta de empresa de a3ECO (E00001, E00002…).",
                "Comprime la carpeta de datos de a3ECO (la que contiene TCLIPRO.DAT y las carpetas Ennnnn) o la de la empresa.");
        carpeta = carpeta?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(carpeta))
        {
            if (carpetas.Count > 1)
                throw new ReglaContableException("Hay varias empresas de a3ECO: indica cuál importar.", string.Join(", ", carpetas));
            carpeta = carpetas[0];
        }
        else if (!carpetas.Contains(carpeta))
            throw new ReglaContableException($"No está la carpeta {carpeta}.", string.Join(", ", carpetas));

        var avisos = new List<string>();
        var prefijo = carpeta[1..];
        var deEmpresa = ficheros.Where(f => RutaEmpresa().Match(f.Key.Replace('\\', '/')) is { Success: true } m
                                            && m.Groups[2].Value.Equals(carpeta, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileName(f.Key.Replace('\\', '/')).ToUpperInvariant(), f => f.Value);

        var terceros = new List<TerceroA3>();
        if (Buscar(ficheros, "TCLIPRO.DAT") is { } tcli)
            terceros.AddRange(FicheroIsam.Registros(tcli, "TCLIPRO.DAT").Where(r => r.Length >= 177).Select(Tercero));
        else avisos.Add("No está TCLIPRO.DAT (en la carpeta de a3ECO): las subcuentas se importan sin NIF ni datos de cliente o proveedor.");

        var anios = new List<AnioA3>();
        foreach (var digito in deEmpresa.Keys
                     .Where(n => n.Length == 12 && n.StartsWith(prefijo, StringComparison.Ordinal) && n.EndsWith("CU.DAT", StringComparison.Ordinal))
                     .Select(n => n[5]).Where(char.IsAsciiDigit).Distinct().Order())
        {
            var cuentas = FicheroIsam.Registros(deEmpresa[$"{prefijo}{digito}CU.DAT"], $"{prefijo}{digito}CU.DAT")
                .Where(r => r.Length >= 211).Select(Cuenta).ToList();

            var apuntes = new List<(ApunteA3 A, int Orden)>();
            foreach (var (nombre, datos) in deEmpresa.Where(f => f.Key.Length == 12 && f.Key.StartsWith($"{prefijo}{digito}", StringComparison.Ordinal)
                                                                 && f.Key.EndsWith("A.DAT", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
                foreach (var r in FicheroIsam.Registros(datos, nombre))
                    if (Apunte(r) is { } a) apuntes.Add((a, apuntes.Count));

            var inicio = InicioEjercicio(digito, apuntes.Select(x => x.A.Fecha).ToList(), hoy);
            var fin = inicio.AddYears(1).AddDays(-1);
            var fuera = apuntes.Count(x => x.A.Fecha < inicio || x.A.Fecha > fin);
            if (fuera > 0) avisos.Add($"{inicio.Year}: {fuera} apunte(s) con fecha fuera del ejercicio; no se importan.");

            var asientos = apuntes.Where(x => x.A.Fecha >= inicio && x.A.Fecha <= fin)
                .GroupBy(x => x.A.Asiento)
                .Select(g => new AsientoA3(g.Key, g.Min(x => x.A.Fecha), g.OrderBy(x => x.Orden).Select(x => x.A).ToList()))
                .OrderBy(a => a.Fecha).ThenBy(a => a.Numero).ToList();
            anios.Add(new AnioA3(inicio.Year, inicio, cuentas, asientos));
        }
        if (anios.Count == 0)
            throw new ReglaContableException($"La carpeta {carpeta} no tiene planes de cuentas (ficheros {prefijo}?CU.DAT).");

        var subcuentas = anios.SelectMany(a => a.Cuentas).Where(c => c.EsSubcuenta).Select(c => c.Codigo)
            .Concat(anios.SelectMany(a => a.Asientos).SelectMany(a => a.Apuntes).Select(a => a.Cuenta));
        var empresa = Empresas(ficheros).FirstOrDefault(e => e.Carpeta == carpeta);
        return new DatosA3(carpeta, empresa, terceros, anios.OrderBy(a => a.Anio).ToList(), DigitosA3(subcuentas), avisos);
    }

    // ------------------------------------------------------------------------------------------------ cuentas

    /// <summary>
    /// Dígitos de las subcuentas en a3: los significativos del código de 12 en casi todas (98 %), como mínimo 5. Alguna subcuenta
    /// suelta con más dígitos (creada a mano) no cambia el resultado: al convertirla no cabe y se avisa.
    /// </summary>
    public static int DigitosA3(IEnumerable<string> subcuentas)
    {
        var largos = subcuentas.Distinct().Select(c => c.TrimEnd('0').Length).Order().ToList();
        return largos.Count == 0 ? 8 : Math.Max(5, largos[(int)Math.Ceiling(largos.Count * 0.98) - 1]);
    }

    /// <summary>
    /// Código de la subcuenta con los dígitos de la empresa de destino: los 4 primeros (la cuenta) y el subnúmero alineado a la
    /// derecha (43000007 → 4300000007 con 10 dígitos). Devuelve null si no cabe.
    /// </summary>
    public static string? Convertir(string codigo12, int digitosA3, int digitosDestino)
    {
        if (codigo12.Length != 12 || codigo12[digitosA3..].Any(c => c != '0')) return null;
        var significativo = codigo12[..digitosA3];
        if (digitosDestino == digitosA3) return significativo;
        var cuenta = significativo[..4];
        var sub = significativo[4..].TrimStart('0');
        return 4 + sub.Length > digitosDestino ? null : cuenta + sub.PadLeft(digitosDestino - 4, '0');
    }

    // ------------------------------------------------------------------------------------------------ registros

    /// <summary>Las subcuentas llevan la cuenta de 4 dígitos en los 2 primeros bytes; los grupos y cuentas (1 a 4 dígitos), un cero.</summary>
    private static CuentaA3 Cuenta(byte[] r)
    {
        var cuenta = BinaryPrimitives.ReadUInt16BigEndian(r);
        var sub = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(2));
        var codigo = cuenta > 0 ? $"{cuenta:D4}{sub:D8}" : sub.ToString();
        var tercero = (r[208] << 16) | (r[209] << 8) | r[210];
        return new CuentaA3(codigo, cuenta > 0, Txt(r, 6, 30), tercero == 0 ? null : tercero, Le6(r, 36), Le6(r, 42),
            Enumerable.Range(0, 12).Select(i => Le6(r, 54 + 6 * i)).ToArray(),
            Enumerable.Range(0, 12).Select(i => Le6(r, 126 + 6 * i)).ToArray());
    }

    private static ApunteA3? Apunte(byte[] r)
    {
        if (r.Length != 128) return null;
        var f = BinaryPrimitives.ReadInt32BigEndian(r);
        if (!DateOnly.TryParseExact(f.ToString(), "yyyyMMdd", out var fecha)) return null;
        if (r[58] is not ((byte)'D' or (byte)'H') || !r.AsSpan(59, 14).ToArray().All(b => b is >= (byte)'0' and <= (byte)'9')) return null;
        var cuenta = ((long)r[53] << 32) | BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(54));
        if (cuenta is < 100_000_000_000 or > 999_999_999_999) return null;
        return new ApunteA3(fecha, BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(108)), cuenta.ToString(), r[58] == 'D',
            long.Parse(Texto.GetString(r, 59, 14)), Txt(r, 13, 30), Txt(r, 43, 10));
    }

    private static TerceroA3 Tercero(byte[] r)
    {
        var via = string.Join(" ", new[] { Txt(r, 60, 5), Txt(r, 65, 30), Txt(r, 95, 5), Txt(r, 100, 6) }.Where(x => x.Length > 0));
        var cp = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(106));
        return new TerceroA3(BinaryPrimitives.ReadInt32BigEndian(r), Nif(Txt(r, 4, 14)), Txt(r, 18, 40), Nulo(via),
            cp is >= 1000 and <= 52999 ? cp.ToString("D5") : null, Nulo(Txt(r, 112, 20)), Nulo(Txt(r, 147, 15)));
    }

    /// <summary>Ejercicio natural: el año que indican los asientos o, sin asientos, el último año acabado en ese dígito.</summary>
    private static DateOnly InicioEjercicio(char digito, List<DateOnly> fechas, DateOnly hoy)
    {
        var anio = fechas.Count > 0
            ? fechas.GroupBy(f => f.Year).OrderByDescending(g => g.Count()).First().Key
            : hoy.Year - ((hoy.Year - (digito - '0')) % 10 + 10) % 10;
        return new DateOnly(anio, 1, 1);
    }

    private static byte[]? Buscar(IReadOnlyDictionary<string, byte[]> ficheros, string nombre)
        => ficheros.Where(f => Path.GetFileName(f.Key.Replace('\\', '/')).Equals(nombre, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key.Length).Select(f => f.Value).FirstOrDefault();

    private static long Le6(byte[] r, int o)
    {
        long v = 0;
        for (var i = 5; i >= 0; i--) v = (v << 8) | r[o + i];
        return (v << 16) >> 16;                                     // signo de 48 bits
    }

    private static string Txt(byte[] r, int o, int n) => Texto.GetString(r, o, Math.Min(n, r.Length - o)).TrimEnd(' ', '\0').Trim();
    private static string? Nulo(string s) => s.Length == 0 ? null : s;
    private static string Nif(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
