namespace Contabilidad.Domain.Fiscal;

public sealed record ModeloSoportado(string Administracion, string Codigo, string Nombre, string Impuesto, string[] Periodos);

/// <summary>Modelos que la aplicación sabe calcular. El resto del catálogo (fiscal.modelo_fiscal) está pendiente.</summary>
public static class ModelosFiscales
{
    public static readonly IReadOnlyList<ModeloSoportado> Soportados =
    [
        new("AEAT", "303", "IVA - Autoliquidación",                  "IVA",  ["1T", "2T", "3T", "4T", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12"]),
        new("AEAT", "390", "IVA - Resumen anual (simplificado)",     "IVA",  ["0A"]),
        new("AEAT", "309", "IVA - Declaración no periódica (recargo de equivalencia, REAGP)", "IVA_NP", ["1T", "2T", "3T", "4T"]),
        new("ATC",  "420", "IGIC - Autoliquidación",                 "IGIC", ["1T", "2T", "3T", "4T", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12"]),
        new("ATC",  "425", "IGIC - Resumen anual (simplificado)",    "IGIC", ["0A"]),
        new("AEAT", "130", "IRPF - Pago fraccionado estimación directa", "IRPF", ["1T", "2T", "3T", "4T"]),
        new("AEAT", "131", "IRPF - Pago fraccionado estimación objetiva (módulos)", "IRPF", ["1T", "2T", "3T", "4T"]),
        new("AEAT", "347", "Operaciones con terceras personas",      "TERCEROS", ["0A"]),
        new("AEAT", "349", "Declaración recapitulativa de operaciones intracomunitarias", "INTRACOM",
            ["1T", "2T", "3T", "4T", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12"]),
        new("AEAT", "111", "Retenciones de trabajo y actividades económicas", "RETENCIONES",
            ["1T", "2T", "3T", "4T", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12"]),
        new("AEAT", "115", "Retenciones de arrendamientos de inmuebles urbanos", "RETENCIONES",
            ["1T", "2T", "3T", "4T", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12"]),
        new("AEAT", "190", "Resumen anual de retenciones de trabajo y actividades económicas", "RETENCIONES", ["0A"]),
        new("AEAT", "180", "Resumen anual de retenciones de arrendamientos", "RETENCIONES", ["0A"]),
        new("ATC",  "415", "IGIC - Operaciones con terceras personas", "TERCEROS", ["0A"]),
        new("AEAT", "200", "Impuesto sobre Sociedades (periodo impositivo que empieza en el año)", "IS", ["0A"]),
        new("AEAT", "202", "Impuesto sobre Sociedades - Pago fraccionado", "IS", ["1P", "2P", "3P"]),
    ];

    public static ModeloSoportado Obtener(string? codigo)
        => Soportados.FirstOrDefault(m => m.Codigo == codigo)
           ?? throw new ReglaContableException($"El modelo '{codigo}' no está disponible todavía.",
               $"Disponibles: {string.Join(", ", Soportados.Select(m => m.Codigo))}.");

    public static PeriodoFiscal ResolverPeriodo(ModeloSoportado m, int anio, string? periodo)
    {
        var p = PeriodoFiscal.Resolver(anio, periodo);
        if (!m.Periodos.Contains(p.Codigo))
            throw new ReglaContableException($"El modelo {m.Codigo} no se presenta con el periodo {p.Codigo}.",
                $"Periodos válidos: {string.Join(", ", m.Periodos)}.");
        return p;
    }
}

public sealed record Casilla(string Clave, string Descripcion, decimal Importe);

/// <summary>Resultado de calcular un modelo. No se guarda hasta que el usuario lo pida.</summary>
public sealed class DeclaracionCalculada
{
    public string Modelo { get; init; } = "";
    public int Anio { get; init; }
    public string Periodo { get; init; } = "";
    public DateOnly FechaInicio { get; set; }
    public DateOnly FechaFin { get; set; }
    public List<Casilla> Casillas { get; init; } = [];
    public decimal Resultado { get; set; }
    public decimal AIngresar { get; set; }
    public decimal ADevolver { get; set; }
    public decimal ACompensar { get; set; }
    public List<string> Avisos { get; init; } = [];
    /// <summary>Detalle adicional (filas del 347…), serializable a JSON.</summary>
    public object? Detalle { get; set; }
    public DateOnly PlazoDesde { get; set; }
    public DateOnly PlazoHasta { get; set; }

    /// <summary>Periodo impositivo del Impuesto sobre Sociedades (200/202).</summary>
    public long? EjercicioId { get; set; }

    public decimal this[string clave] => Casillas.FirstOrDefault(c => c.Clave == clave)?.Importe ?? 0m;
}
