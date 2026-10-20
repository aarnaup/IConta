namespace Contabilidad.Domain;

public sealed class ConfiguracionFiscalDto
{
    /// <summary>GENERAL, CRITERIO_CAJA, SIMPLIFICADO (módulos), RECARGO_EQUIVALENCIA (comercio minorista) AGRICULTURA_GYP (REAGP) o COMERCIANTE_MINORISTA (IGIC).</summary>
    public string RegimenIva { get; set; } = "GENERAL";
    public string ImpuestoIndirecto { get; set; } = "";
    /// <summary>Empresario persona física: DIRECTA_NORMAL, DIRECTA_SIMPLIFICADA u OBJETIVA; null para sociedades.</summary>
    public string? RegimenIrpf { get; set; }
    public bool Sii { get; set; }
    public bool TributaIs { get; set; }
    /// <summary>GENERAL, NUEVA_CREACION, REDUCIDA_DIMENSION, MICRO o PERSONALIZADO.</summary>
    public string IsTipo { get; set; } = "GENERAL";
    public decimal? IsPorcentaje { get; set; }
    /// <summary>ART_40_2 o ART_40_3.</summary>
    public string IsModalidad202 { get; set; } = "ART_40_2";
}

public sealed class GuardarConfiguracionFiscalCmd
{
    public string RegimenIva { get; set; } = "GENERAL";
    /// <summary>Si no se indica, no cambia. Cadena vacía: sin IRPF (sociedad).</summary>
    public string? RegimenIrpf { get; set; }
    public bool? Sii { get; set; }
    public bool? TributaIs { get; set; }
    public string? IsTipo { get; set; }
    public decimal? IsPorcentaje { get; set; }
    public string? IsModalidad202 { get; set; }
}

public sealed class TraspasoCajaCmd
{
    public int Anio { get; set; }
    public string Periodo { get; set; } = "";
    /// <summary>Por defecto, el último día del periodo.</summary>
    public DateOnly? Fecha { get; set; }
}

public sealed class LineaTraspasoCajaDto
{
    public long FacturaId { get; set; }
    public string Libro { get; set; } = "";
    public string Factura { get; set; } = "";
    public string Tercero { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Base { get; set; }
    public decimal Cuota { get; set; }
    public bool Traspasado { get; set; }
    public int? AsientoNumero { get; set; }
    public long? AsientoId { get; set; }
}

/// <summary>IVA de criterio de caja devengado (emitidas) o deducible (recibidas) en un periodo, por factura.</summary>
public sealed record DevengoCajaDto(int Anio, string Periodo, IReadOnlyList<LineaTraspasoCajaDto> Lineas, decimal Repercutido,
    decimal Deducible, decimal Pendiente, IReadOnlyList<string> Avisos);

public sealed record TraspasoCajaHechoDto(long AsientoId, int AsientoNumero, decimal Repercutido, decimal Deducible);

public sealed class CriterioCajaTerceroCmd { public bool CriterioCaja { get; set; } }
