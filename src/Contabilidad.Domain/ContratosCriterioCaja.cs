namespace Contabilidad.Domain;

public sealed class ConfiguracionFiscalDto
{
    /// <summary>GENERAL o CRITERIO_CAJA (los demás regímenes están pendientes).</summary>
    public string RegimenIva { get; set; } = "GENERAL";
    public string ImpuestoIndirecto { get; set; } = "";
}

public sealed class GuardarConfiguracionFiscalCmd
{
    public string RegimenIva { get; set; } = "GENERAL";
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
