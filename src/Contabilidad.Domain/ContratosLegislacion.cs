namespace Contabilidad.Domain;

public sealed class ParametroFiscalDto
{
    public long Id { get; set; }
    public string Clave { get; set; } = "";
    public DateOnly Desde { get; set; }
    public decimal Valor { get; set; }
    public string Descripcion { get; set; } = "";
}

public sealed class GuardarParametroCmd
{
    public string Clave { get; set; } = "";
    public DateOnly Desde { get; set; }
    public decimal Valor { get; set; }
    public string? Descripcion { get; set; }
}

public sealed class VersionFormularioDto
{
    public long Id { get; set; }
    public string Modelo { get; set; } = "";
    public int DesdeAnio { get; set; }
    public string? Comentario { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
}

public sealed class GuardarFormularioCmd
{
    public string Modelo { get; set; } = "";
    public int DesdeAnio { get; set; }
    /// <summary>JSON: {renombrar, resultado, apartados: [{titulo, columnas, filas: [{texto, casillas, total}]}], totales: [{casilla, terminos}]}.</summary>
    public string Definicion { get; set; } = "";
    public string? Comentario { get; set; }
}

/// <summary>Diseño de fichero de un modelo: los del programa y los guardados en la base de datos (id).</summary>
public sealed record DisenoResumenDto(long? Id, string Modelo, DateOnly Desde, DateOnly? Hasta, string Origen, int Longitud, string? Comentario);

public sealed class GuardarDisenoCmd
{
    public string Modelo { get; set; } = "";
    public DateOnly Desde { get; set; }
    public DateOnly? Hasta { get; set; }
    /// <summary>JSON: {elementos: [{campo, tipo, longitud, clave…} | {bloque, coleccion, condicion, elementos}]}.</summary>
    public string Definicion { get; set; } = "";
    public string? Comentario { get; set; }
}

// ---- Composición del balance y la PyG del modelo 200 (qué cuentas van a cada partida)

public sealed class CuentaPartidaDto
{
    public string Cuenta { get; set; } = "";
    /// <summary>Saldo, SaldoDeudor, SaldoAcreedor, Debe o Haber.</summary>
    public string Concepto { get; set; } = "Saldo";
    public bool Resta { get; set; }
    /// <summary>Solo de lectura: cómo entra en la partida («+ (Debe − Haber)», «− Haber»…).</summary>
    public string? Calculo { get; set; }
}

public sealed class PartidaEstadoDto
{
    public string Id { get; set; } = "";
    public string? Descripcion { get; set; }
    /// <summary>De pasivo, patrimonio neto o ingreso: el saldo acreedor sale en positivo.</summary>
    public bool Acreedora { get; set; }
    /// <summary>Si es un total: partidas que suma.</summary>
    public List<string>? Suma { get; set; }
    public List<CuentaPartidaDto> Cuentas { get; set; } = [];
}

public sealed class EstadoLegislacionDto
{
    public string Codigo { get; set; } = "";
    public int Anio { get; set; }
    /// <summary>Año desde el que rige la versión aplicada; null si es la del programa.</summary>
    public int? Version { get; set; }
    public List<PartidaEstadoDto> Partidas { get; set; } = [];
}

public sealed class GuardarEstadoCmd
{
    public string Codigo { get; set; } = "";
    public int DesdeAnio { get; set; }
    public string? Comentario { get; set; }
    public List<PartidaEstadoDto> Partidas { get; set; } = [];
}
