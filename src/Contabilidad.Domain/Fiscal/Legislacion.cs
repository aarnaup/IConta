using System.Text.Json;
using System.Text.Json.Nodes;
using Contabilidad.Domain.Fiscal.Presentacion;

namespace Contabilidad.Domain.Fiscal;

/// <summary>Valor de un parámetro de cálculo desde una fecha (porcentaje, límite…); rige hasta el siguiente de la misma clave.</summary>
public sealed record ParametroFiscal(string Clave, DateOnly Desde, decimal Valor, string Descripcion);

/// <summary>Versión de las casillas y la disposición del formulario de un modelo, desde un año (JSON, ver <see cref="Legislacion"/>).</summary>
public sealed record VersionFormulario(string Modelo, int DesdeAnio, string Definicion);

/// <summary>Legislación guardada en la base de datos: parámetros, versiones de formularios y diseños de fichero.</summary>
public sealed record ContextoLegislacion(IReadOnlyList<ParametroFiscal> Parametros, IReadOnlyList<VersionFormulario> Formularios,
    IReadOnlyList<DisenoAeat> Disenos);

/// <summary>
/// Legislación vigente en cada fecha. Cada cálculo usa los valores del periodo que declara (una declaración de 2025 usa lo de 2025 aunque
/// se calcule después). Los valores vienen de la base de datos (Administración → Legislación); sin ella, los de <see cref="Defecto"/>.
/// El contexto se fija para cada petición (<see cref="Usar"/>) y llega a los cálculos sin pasarlo por parámetro.
/// </summary>
public static class Legislacion
{
    private static readonly AsyncLocal<ContextoLegislacion?> Actual = new();

    /// <summary>Valores de la ley tal como estaban al programarlos (tipos del IS de la Ley 7/2024 por año, etc.).</summary>
    public static readonly IReadOnlyList<ParametroFiscal> Defecto =
    [
        new("IRPF_130_PCT", new(2000, 1, 1), 20m, "130: porcentaje sobre el rendimiento neto acumulado"),
        new("IVA_347_UMBRAL", new(2000, 1, 1), 3005.06m, "347: importe a partir del cual se declara un tercero"),
        new("IVA_BIEN_INVERSION_MINIMO", new(2000, 1, 1), 3005.06m, "IVA: importe mínimo para que un bien sea de inversión (art. 108)"),
        new("IS_TIPO_GENERAL", new(2000, 1, 1), 25m, "Sociedades: tipo general"),
        new("IS_TIPO_NUEVA_CREACION", new(2000, 1, 1), 15m, "Sociedades: entidades de nueva creación"),
        new("IS_TIPO_REDUCIDA_DIMENSION", new(2000, 1, 1), 25m, "Sociedades: reducida dimensión"),
        new("IS_TIPO_REDUCIDA_DIMENSION", new(2025, 1, 1), 24m, "Sociedades: reducida dimensión"),
        new("IS_TIPO_REDUCIDA_DIMENSION", new(2026, 1, 1), 23m, "Sociedades: reducida dimensión"),
        new("IS_TIPO_REDUCIDA_DIMENSION", new(2027, 1, 1), 22m, "Sociedades: reducida dimensión"),
        new("IS_TIPO_REDUCIDA_DIMENSION", new(2028, 1, 1), 21m, "Sociedades: reducida dimensión"),
        new("IS_TIPO_REDUCIDA_DIMENSION", new(2029, 1, 1), 20m, "Sociedades: reducida dimensión"),
        new("IS_MICRO_TRAMO1", new(2000, 1, 1), 25m, "Sociedades: microempresas, tipo de los primeros euros de base"),
        new("IS_MICRO_TRAMO1", new(2023, 1, 1), 23m, "Sociedades: microempresas, tipo de los primeros euros de base"),
        new("IS_MICRO_TRAMO1", new(2025, 1, 1), 21m, "Sociedades: microempresas, tipo de los primeros euros de base"),
        new("IS_MICRO_TRAMO1", new(2026, 1, 1), 19m, "Sociedades: microempresas, tipo de los primeros euros de base"),
        new("IS_MICRO_TRAMO1", new(2027, 1, 1), 17m, "Sociedades: microempresas, tipo de los primeros euros de base"),
        new("IS_MICRO_RESTO", new(2000, 1, 1), 25m, "Sociedades: microempresas, tipo del resto de la base"),
        new("IS_MICRO_RESTO", new(2023, 1, 1), 23m, "Sociedades: microempresas, tipo del resto de la base"),
        new("IS_MICRO_RESTO", new(2025, 1, 1), 22m, "Sociedades: microempresas, tipo del resto de la base"),
        new("IS_MICRO_RESTO", new(2026, 1, 1), 21m, "Sociedades: microempresas, tipo del resto de la base"),
        new("IS_MICRO_RESTO", new(2027, 1, 1), 20m, "Sociedades: microempresas, tipo del resto de la base"),
        new("IS_MICRO_TRAMO", new(2000, 1, 1), 50000m, "Sociedades: microempresas, base del primer tramo (anual)"),
        new("IS_202_PCT_40_2", new(2000, 1, 1), 18m, "202 (art. 40.2): porcentaje sobre la cuota del último periodo"),
    ];

    /// <summary>Fija la legislación de la base de datos para lo que se ejecute a continuación (una petición).</summary>
    public static IDisposable Usar(ContextoLegislacion contexto)
    {
        var anterior = Actual.Value;
        Actual.Value = contexto;
        return new Restaurar(() => Actual.Value = anterior);
    }

    private sealed class Restaurar(Action accion) : IDisposable { public void Dispose() => accion(); }

    /// <summary>Valor de <paramref name="clave"/> vigente en <paramref name="fecha"/>.</summary>
    public static decimal Parametro(string clave, DateOnly fecha)
    {
        var lista = Actual.Value?.Parametros is { Count: > 0 } p && p.Any(x => x.Clave == clave) ? p : Defecto;
        return lista.Where(x => x.Clave == clave && x.Desde <= fecha).OrderByDescending(x => x.Desde).FirstOrDefault()?.Valor
               ?? throw new ReglaContableException($"No hay valor del parámetro {clave} para {fecha:dd/MM/yyyy}.",
                   "Añádelo en Administración → Legislación → Parámetros de cálculo.");
    }

    /// <summary>Versión del formulario del modelo vigente en el año (la de «desde» más reciente que no pase del año).</summary>
    public static VersionFormulario? Formulario(string modelo, int anio)
        => Actual.Value?.Formularios.Where(f => f.Modelo == modelo && f.DesdeAnio <= anio).OrderByDescending(f => f.DesdeAnio).FirstOrDefault();

    /// <summary>Diseños de fichero guardados en la base de datos (se suman a los del programa y tienen preferencia).</summary>
    public static IReadOnlyList<DisenoAeat> Disenos => Actual.Value?.Disenos ?? [];

    /// <summary>Casillas renombradas por la versión del formulario del año ({vieja: nueva}), para cuando la AEAT cambia la numeración.</summary>
    public static IReadOnlyDictionary<string, string> Renombrar(string modelo, int anio)
    {
        if (Formulario(modelo, anio) is not { } v) return new Dictionary<string, string>();
        var n = JsonNode.Parse(v.Definicion)?["renombrar"] as JsonObject;
        return n is null ? new Dictionary<string, string>() : n.ToDictionary(x => x.Key, x => x.Value?.GetValue<string>() ?? x.Key);
    }

    // ================================================================================================ JSON de los formularios

    /// <summary>
    /// Aplica una versión a un formulario: sus apartados (si los trae) sustituyen a los del programa y sus totales (suma de términos con
    /// signo) sustituyen a los de la misma casilla o se añaden. Los totales que el programa calcula con una función (66 y 78 del 303,
    /// 04 del 130…) se mantienen salvo que la versión los defina.
    /// </summary>
    public static FormularioModelo Aplicar(FormularioModelo baseF, string definicion)
    {
        var raiz = JsonNode.Parse(definicion) as JsonObject ?? throw new ReglaContableException("La definición del formulario no es un objeto JSON.");
        var apartados = raiz["apartados"] is JsonArray aps
            ? aps.Select(a => new ApartadoFormulario(
                a?["titulo"]?.GetValue<string>() ?? "",
                (a?["columnas"] as JsonArray)?.Select(c => c?.GetValue<string>() ?? "").ToArray() ?? ["Importe"],
                ((a?["filas"] as JsonArray) ?? []).Select(f => new FilaFormulario(
                    f?["texto"]?.GetValue<string>() ?? "",
                    (f?["casillas"] as JsonArray)?.Select(c => c?.GetValue<string>() ?? "").ToArray() ?? [],
                    f?["total"]?.GetValue<bool>() ?? false)).ToList())).ToList()
            : baseF.Apartados;
        var totales = baseF.Totales.ToList();
        if (raiz["totales"] is JsonArray tots)
            foreach (var t in tots)
            {
                var casilla = t?["casilla"]?.GetValue<string>() ?? throw new ReglaContableException("Un total no tiene «casilla».");
                var terminos = ((t?["terminos"] as JsonArray) ?? []).Select(x => x is JsonArray par
                        ? (par[0]!.GetValue<string>(), par.Count > 1 ? par[1]!.GetValue<int>() : 1)
                        : (x!.GetValue<string>(), 1)).ToArray();
                var nuevo = new TotalFormulario(casilla, terminos);
                var i = totales.FindIndex(x => x.Casilla == casilla);
                if (i >= 0) totales[i] = nuevo; else totales.Add(nuevo);
            }
        return baseF with
        {
            Apartados = apartados, Totales = totales,
            CasillaResultado = raiz["resultado"]?.GetValue<string>() ?? baseF.CasillaResultado,
        };
    }

    /// <summary>Definición JSON de un formulario (para empezar una versión nueva desde la vigente).</summary>
    public static string Exportar(FormularioModelo f, IReadOnlyDictionary<string, string>? renombrar = null)
    {
        var raiz = new JsonObject
        {
            ["renombrar"] = new JsonObject((renombrar ?? new Dictionary<string, string>()).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
            ["resultado"] = f.CasillaResultado,
            ["apartados"] = new JsonArray(f.Apartados.Select(a => (JsonNode)new JsonObject
            {
                ["titulo"] = a.Titulo,
                ["columnas"] = new JsonArray(a.Columnas.Select(c => (JsonNode)c).ToArray()),
                ["filas"] = new JsonArray(a.Filas.Select(r => (JsonNode)new JsonObject
                {
                    ["texto"] = r.Texto, ["casillas"] = new JsonArray(r.Casillas.Select(c => (JsonNode)c).ToArray()), ["total"] = r.Total,
                }).ToArray()),
            }).ToArray()),
            ["totales"] = new JsonArray(f.Totales.Select(t => (JsonNode)new JsonObject
            {
                ["casilla"] = t.Casilla,
                ["terminos"] = t.Funcion is null ? new JsonArray(t.Terminos.Select(x => (JsonNode)new JsonArray(x.Casilla, x.Signo)).ToArray()) : null,
                ["calculo"] = t.Funcion is null ? null : "interno (del programa; si escribes «terminos» se sustituye por la suma)",
            }).ToArray()),
        };
        // Los totales con función interna no se exportan como suma (al importar se mantendrían igual)
        foreach (var t in ((JsonArray)raiz["totales"]!).OfType<JsonObject>().Where(t => t["terminos"] is null).ToList())
            ((JsonArray)raiz["totales"]!).Remove(t);
        return raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // ================================================================================================ JSON de los diseños de fichero

    public static string ExportarDiseno(DisenoAeat d)
        => new JsonObject { ["modelo"] = d.Modelo, ["elementos"] = Elementos(d.Elementos) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonArray Elementos(IEnumerable<ElementoAeat> es) => new(es.Select(e => (JsonNode)(e switch
    {
        CampoAeat c => new JsonObject
        {
            ["campo"] = c.Nombre, ["tipo"] = c.Tipo.ToString(), ["longitud"] = c.Longitud,
            ["decimales"] = c.Decimales == 0 ? null : c.Decimales, ["signo"] = c.Signo ? true : null,
            ["positivo"] = c.Positivo == '0' ? null : c.Positivo.ToString(), ["negativo"] = c.Negativo == 'N' ? null : c.Negativo.ToString(),
            ["fijo"] = c.Fijo, ["plantilla"] = c.Plantilla, ["clave"] = c.Clave, ["derecha"] = c.Derecha ? true : null,
            ["si"] = c.Si == 'X' ? null : c.Si.ToString(), ["no"] = c.No == ' ' ? null : c.No.ToString(), ["literal"] = c.Literal,
        },
        BloqueAeat b => new JsonObject { ["bloque"] = b.Nombre, ["condicion"] = b.Condicion, ["coleccion"] = b.Coleccion, ["elementos"] = Elementos(b.Elementos) },
        _ => throw new InvalidOperationException(),
    })).ToArray());

    public static DisenoAeat ImportarDiseno(string modelo, DateOnly desde, DateOnly? hasta, string json)
    {
        JsonObject raiz;
        try { raiz = JsonNode.Parse(json) as JsonObject ?? throw new FormatException(); }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new ReglaContableException("El diseño no es un JSON válido.", ex.Message); }
        var elementos = LeerElementos(raiz["elementos"] as JsonArray ?? throw new ReglaContableException("El diseño no tiene «elementos»."));
        if (elementos.Count == 0) throw new ReglaContableException("El diseño no tiene campos.");
        return new DisenoAeat(modelo, desde, hasta, elementos);
    }

    private static List<ElementoAeat> LeerElementos(JsonArray a) => a.Select(x =>
    {
        var o = x as JsonObject ?? throw new ReglaContableException("Cada elemento del diseño debe ser un objeto.");
        string? S(string k) => o[k]?.GetValue<string>();
        char C(string k, char def) => S(k) is { Length: > 0 } v ? v[0] : def;
        if (o["bloque"] is not null)
            return (ElementoAeat)new BloqueAeat(S("bloque")!, LeerElementos(o["elementos"] as JsonArray ?? []), S("condicion"), S("coleccion"));
        var nombre = S("campo") ?? throw new ReglaContableException("Un elemento no es «campo» ni «bloque».");
        if (!Enum.TryParse<TipoCampoAeat>(S("tipo") ?? "Alfanumerico", out var tipo))
            throw new ReglaContableException($"Tipo no válido en el campo «{nombre}».", "Alfanumerico, Alfabetico, Numerico o Booleano.");
        var longitud = o["longitud"]?.GetValue<int>() ?? 0;
        if (longitud <= 0 && S("literal") is null) throw new ReglaContableException($"El campo «{nombre}» no tiene longitud.");
        return new CampoAeat(nombre, tipo, longitud, o["decimales"]?.GetValue<int>() ?? 0, o["signo"]?.GetValue<bool>() ?? false,
            C("positivo", '0'), C("negativo", 'N'), S("fijo"), S("plantilla"), S("clave"), o["derecha"]?.GetValue<bool>() ?? false,
            C("si", 'X'), C("no", ' '), S("literal"));
    }).ToList();
}
