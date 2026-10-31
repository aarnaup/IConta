using System.Text.Json.Nodes;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;

namespace Contabilidad.Domain.Balances;

/// <summary>
/// Composición de los estados (qué cuentas van a cada partida) por ejercicio: la del programa o la versión guardada en Legislación
/// desde un año. Sirve para el balance y la PyG de las cuentas anuales (PYMES/abreviado y normal) y para los del modelo 200.
/// </summary>
public static class EstadosLegislados
{
    /// <summary>Estados cuyas cuentas se pueden cambiar por ejercicio en Legislación.</summary>
    public static IReadOnlyList<ModeloEstado> Todos =>
    [
        ModelosEstados.Balance, ModelosEstados.PerdidasGanancias, ModelosNormales.Balance, ModelosNormales.PerdidasGanancias,
        Estados200.Balance, Estados200.PerdidasGanancias,
    ];

    public static IEnumerable<string> Codigos => Todos.Select(m => m.Codigo);

    public static ModeloEstado DelPrograma(string codigo)
        => Todos.FirstOrDefault(m => m.Codigo == codigo)
           ?? throw new ReglaContableException($"Estado no válido: {codigo}.", string.Join(", ", Codigos));

    /// <summary>El estado vigente en el ejercicio: la versión de Legislación de ese año o la del programa.</summary>
    public static ModeloEstado Modelo(string codigo, int anio)
        => Legislacion.Formulario(codigo, anio) is { } v ? Importar(DelPrograma(codigo), v.Definicion) : DelPrograma(codigo);

    /// <summary>JSON de un estado: {"partidas": [{"id", "acreedora", "cuentas": ["201", "-2801:HABER"…], "suma": ["00102"…]}]}.</summary>
    public static string Exportar(ModeloEstado m) => new JsonObject
    {
        ["partidas"] = new JsonArray(m.Partidas.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["texto"] = p.Texto,
            ["nivel"] = p.Nivel,
            ["acreedora"] = p.Acreedora,
            ["cuentas"] = p.Suma is null ? new JsonArray(ReglaCuenta.Lista(p.Cuentas).Select(r => (JsonNode)r.ToString()).ToArray()) : null,
            ["suma"] = p.Suma is null ? null : new JsonArray(p.Suma.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode)x).ToArray()),
        }).ToArray()),
    }.ToJsonString();

    /// <summary>Lee un estado guardado y comprueba que sea coherente (partidas únicas, totales que existen y sin ciclos, cuentas válidas).</summary>
    public static ModeloEstado Importar(ModeloEstado baseM, string json)
    {
        JsonArray filas;
        try { filas = JsonNode.Parse(json)?["partidas"] as JsonArray ?? throw new ReglaContableException("El estado no tiene «partidas»."); }
        catch (System.Text.Json.JsonException ex) { throw new ReglaContableException("El estado no es un JSON válido.", ex.Message); }
        string Lista(JsonNode? n) => string.Join(' ', (n as JsonArray ?? []).Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0));
        var previas = baseM.Partidas.ToDictionary(p => p.Id);
        var partidas = filas.OfType<JsonObject>().Select(o =>
        {
            var id = o["id"]?.GetValue<string>()?.Trim() ?? throw new ReglaContableException("Hay una partida sin «id».");
            var esTotal = o["suma"] is JsonArray;
            var previa = previas.GetValueOrDefault(id);
            return new PartidaModelo(id, o["texto"]?.GetValue<string>() ?? previa?.Texto ?? id,
                o["nivel"]?.GetValue<int>() ?? previa?.Nivel ?? (esTotal ? 1 : 2), o["acreedora"]?.GetValue<bool>() ?? false,
                esTotal ? null : Lista(o["cuentas"]), esTotal ? Lista(o["suma"]) : null);
        }).ToList();
        var m = baseM with { Partidas = partidas };
        Validar(m);
        return m;
    }

    public static void Validar(ModeloEstado m)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in m.Partidas)
            if (!ids.Add(p.Id)) throw new ReglaContableException($"La partida {p.Id} está repetida.");
        var vistas = new Dictionary<string, string>();
        foreach (var p in m.Partidas.Where(p => p.Suma is null))
            foreach (var r in ReglaCuenta.Lista(p.Cuentas))
            {
                var k = r.Prefijo + ":" + r.Concepto;
                var que = r.Concepto switch
                {
                    ConceptoCuenta.SaldoDeudor => "saldo deudor", ConceptoCuenta.SaldoAcreedor => "saldo acreedor",
                    ConceptoCuenta.Debe => "debe", ConceptoCuenta.Haber => "haber", _ => "saldo",
                };
                if (vistas.TryGetValue(k, out var otra))
                    throw new ReglaContableException(otra == p.Id
                        ? $"La cuenta {r.Prefijo} ({que}) está repetida en la partida {p.Id}."
                        : $"La cuenta {r.Prefijo} ({que}) está en las partidas {otra} y {p.Id}: se contaría dos veces.");
                vistas[k] = p.Id;
            }
        var def = m.Partidas.ToDictionary(p => p.Id);
        var estado = new Dictionary<string, int>();       // 1 visitando, 2 hecho
        void Recorrer(string id, string desde)
        {
            if (!def.TryGetValue(id, out var p)) throw new ReglaContableException($"El total {desde} suma la partida {id}, que no existe.");
            if (estado.GetValueOrDefault(id) == 2) return;
            if (estado.GetValueOrDefault(id) == 1) throw new ReglaContableException($"El total {id} se suma a sí mismo (directa o indirectamente).");
            estado[id] = 1;
            foreach (var x in (p.Suma ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) Recorrer(x, id);
            estado[id] = 2;
        }
        foreach (var p in m.Partidas) Recorrer(p.Id, p.Id);
    }

}
