namespace Contabilidad.Domain;

/// <summary>Inmueble arrendado (datos que pide el modelo 180 por cada arrendador e inmueble).</summary>
public sealed class InmuebleDto
{
    public long Id { get; set; }
    /// <summary>Arrendador (tercero).</summary>
    public long TerceroId { get; set; }
    public string? Arrendador { get; set; }
    /// <summary>Situación (claves del modelo 180): 1 con referencia catastral en España salvo País Vasco y Navarra, 2 en el País Vasco,
    /// 3 en Navarra, 4 sin referencia catastral.</summary>
    public short Situacion { get; set; } = 1;
    public string? ReferenciaCatastral { get; set; }
    /// <summary>Tipo de vía (CL, AV, PZ…).</summary>
    public string? TipoVia { get; set; }
    public string NombreVia { get; set; } = "";
    public string? Numero { get; set; }
    /// <summary>Escalera, piso, puerta…</summary>
    public string? Complemento { get; set; }
    public string? Municipio { get; set; }
    /// <summary>Código INE del municipio (5 dígitos).</summary>
    public string? CodigoMunicipio { get; set; }
    public string? CodigoPostal { get; set; }
}
