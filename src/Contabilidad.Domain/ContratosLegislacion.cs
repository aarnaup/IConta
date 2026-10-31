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
