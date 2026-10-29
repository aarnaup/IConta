using System.Text;
using System.Text.RegularExpressions;
using Contabilidad.Domain;
using Contabilidad.Domain.Importacion.A3Eco;

namespace Contabilidad.Infrastructure.Importacion;

/// <summary>
/// Lee los ficheros de a3ECO directamente de su carpeta de instalación (en el ordenador donde se ejecuta IConta), en lugar de un zip.
/// TPARAGEN.DAT, en la carpeta de la aplicación, es una línea con la ruta de datos (\A3\A3ECO\); ahí está TECODIR.DAT, que guarda la
/// carpeta de cada empresa (\A3\A3ECO\E00001\). Un fichero que no esté en la carpeta de datos se busca en la de la aplicación.
/// Devuelve los ficheros con las mismas claves que un zip: «TECODIR.DAT», «TCLIPRO.DAT» y «E00001/000015CU.DAT».
/// </summary>
public static partial class CarpetaA3Eco
{
    private static readonly string[] Comunes = ["TECODIR.DAT", "TCLIPRO.DAT"];

    [GeneratedRegex(@"^E\d{5}$", RegexOptions.IgnoreCase)]
    private static partial Regex CarpetaEmpresa();

    /// <param name="soloCarpetas">Empresas que se leen (E00001…); null, todas.</param>
    public static Dictionary<string, byte[]> Leer(string ruta, IReadOnlyCollection<string>? soloCarpetas, long maxTotal)
    {
        var app = (ruta ?? "").Trim().Trim('"');
        if (app.Length == 0) throw new ReglaContableException("Indica la carpeta de a3ECO.");
        if (!Directory.Exists(app))
            throw new ReglaContableException($"No existe la carpeta «{app}».",
                "Es la carpeta del ordenador donde se ejecuta IConta (por ejemplo C:\\A3\\A3ECO). Si IConta va en Docker, la carpeta tiene que estar montada en el contenedor.");
        app = Path.GetFullPath(app);
        var datos = RutaDatos(app);

        var r = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void Anadir(string clave, string fichero)
        {
            var largo = new FileInfo(fichero).Length;
            if ((total += largo) > maxTotal)
                throw new ReglaContableException("Los ficheros de a3ECO son demasiado grandes para importarlos de una vez.",
                    "Importa menos empresas cada vez.");
            r[clave] = File.ReadAllBytes(fichero);
        }

        foreach (var nombre in Comunes)
            if ((Fichero(datos, nombre) ?? Fichero(app, nombre)) is { } f) Anadir(nombre, f);

        // Carpetas de las empresas: la que guarda TECODIR (resuelta desde la unidad de a3) o, si no existe, Ennnnn en la carpeta
        // de datos o en la de la aplicación; también las Ennnnn que haya en ellas aunque no estén en el directorio
        var carpetas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in LectorA3Eco.Empresas(r))
            if ((Resolver(e.Ruta, app) ?? Subcarpeta(datos, e.Carpeta) ?? Subcarpeta(app, e.Carpeta)) is { } d) carpetas.TryAdd(e.Carpeta, d);
        foreach (var raiz in new[] { datos, app }.Distinct())
            foreach (var d in Directory.EnumerateDirectories(raiz).Where(d => CarpetaEmpresa().IsMatch(Path.GetFileName(d))))
                carpetas.TryAdd(Path.GetFileName(d).ToUpperInvariant(), d);

        foreach (var (carpeta, dir) in carpetas.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (soloCarpetas is not null && !soloCarpetas.Contains(carpeta, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)))
                Anadir($"{carpeta}/{Path.GetFileName(f)}", f);
        }
        if (!r.Keys.Any(k => k.Contains('/')))
            throw new ReglaContableException($"En «{app}» no hay datos de empresas de a3ECO.",
                $"Se han buscado las carpetas Ennnnn en la ruta de datos ({datos}) y en la de la aplicación.");
        return r;
    }

    /// <summary>Ruta de datos de TPARAGEN.DAT o, si no está o no existe, la propia carpeta de la aplicación.</summary>
    public static string RutaDatos(string app)
    {
        if (Fichero(app, "TPARAGEN.DAT") is not { } par) return app;
        var linea = Encoding.Latin1.GetString(File.ReadAllBytes(par)).Split('\n')
            .Select(l => l.Trim().Trim('\0', '\r').Trim()).FirstOrDefault(l => l.Length > 0);
        return linea is null ? app : Resolver(linea, app) ?? app;
    }

    /// <summary>
    /// Carpeta de una ruta de a3: tal cual si existe; si empieza por «\», en la unidad de la carpeta de la aplicación
    /// (\A3\A3ECO → C:\A3\A3ECO). Devuelve null si no existe.
    /// </summary>
    public static string? Resolver(string? rutaA3, string app)
    {
        var p = (rutaA3 ?? "").Trim();
        if (p.Length == 0) return null;
        var sep = Path.DirectorySeparatorChar;
        var normal = p.Replace('\\', sep).Replace('/', sep).TrimEnd(sep);
        if (normal.Length == 0) return null;
        var candidatas = new List<string> { normal };
        if (normal[0] == sep && !normal.StartsWith($"{sep}{sep}"))
            candidatas.Add(Path.Combine(Path.GetPathRoot(app) ?? $"{sep}", normal.TrimStart(sep)));
        return candidatas.FirstOrDefault(c => Path.IsPathRooted(c) && Directory.Exists(c)) is { } ok ? Path.GetFullPath(ok) : null;
    }

    // Búsquedas sin distinguir mayúsculas (en Linux el sistema de ficheros sí las distingue)
    private static string? Fichero(string dir, string nombre)
        => Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(nombre, StringComparison.OrdinalIgnoreCase)) : null;

    private static string? Subcarpeta(string dir, string nombre)
        => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals(nombre, StringComparison.OrdinalIgnoreCase)) : null;
}
