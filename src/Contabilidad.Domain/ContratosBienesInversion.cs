namespace Contabilidad.Domain;

// ---- Prorrata (año natural y sector diferenciado) ----

public sealed class ProrrataDto
{
    public int Anio { get; set; }
    public int? SectorDiferenciado { get; set; }
    public decimal? OperacionesConDerecho { get; set; }
    public decimal? OperacionesTotales { get; set; }
    public int? PorcentajeProvisional { get; set; }
    public int? PorcentajeDefinitivo { get; set; }
    public string Estado { get; set; } = "";
}

public sealed class GuardarProrrataCmd
{
    public int Anio { get; set; }
    public int? SectorDiferenciado { get; set; }
    /// <summary>El que se aplica durante el año (normalmente el definitivo del año anterior).</summary>
    public int? PorcentajeProvisional { get; set; }
    /// <summary>Si se dan las operaciones del año, el definitivo se calcula (redondeado a la unidad superior, art. 104 LIVA).</summary>
    public decimal? OperacionesConDerecho { get; set; }
    public decimal? OperacionesTotales { get; set; }
    public int? PorcentajeDefinitivo { get; set; }
}

// ---- Registro de bienes de inversión ----

public sealed class AltaBienInversionCmd
{
    /// <summary>Inmuebles: 10 años de regularización; el resto, 5.</summary>
    public bool EsInmueble { get; set; }
    /// <summary>Por defecto, el año de inicio de amortización (puesta en funcionamiento).</summary>
    public int? AnioInicio { get; set; }
    /// <summary>Por defecto, las cuotas de las líneas «bien de inversión» de la factura del elemento.</summary>
    public decimal? CuotaSoportada { get; set; }
    /// <summary>Por defecto, la prorrata del año de inicio (definitiva o, si aún no hay, provisional) o el % de deducción de la factura.</summary>
    public int? ProrrataInicial { get; set; }
    public int? SectorDiferenciado { get; set; }
    /// <summary>Si se vende, la venta será exenta o no sujeta (p. ej., segunda entrega de una nave).</summary>
    public bool TransmisionExenta { get; set; }
}

public sealed class AnioBienInversionDto
{
    public int Anio { get; set; }
    /// <summary>INICIO, ANUAL o TRANSMISION.</summary>
    public string Tipo { get; set; } = "";
    public int? Prorrata { get; set; }
    public int AniosRegularizados { get; set; }
    public decimal Importe { get; set; }
    public string? Nota { get; set; }
    public bool Contabilizada { get; set; }
    public long? AsientoId { get; set; }
    public int? AsientoNumero { get; set; }
}

public sealed class BienInversionDto
{
    public long Id { get; set; }
    public long InmovilizadoId { get; set; }
    public string Codigo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public bool EsInmueble { get; set; }
    public int AnioInicio { get; set; }
    public int Anios { get; set; }
    public int UltimoAnio { get; set; }
    public decimal CuotaSoportada { get; set; }
    public int ProrrataInicial { get; set; }
    public decimal DeduccionInicial { get; set; }
    public int? SectorDiferenciado { get; set; }
    public bool TransmisionExenta { get; set; }
    public string Estado { get; set; } = "";
    public DateOnly? FechaBaja { get; set; }
    public List<AnioBienInversionDto> Cuadro { get; set; } = [];
}

/// <summary>Libro registro de bienes de inversión (art. 65 RIVA) con la regularización de cada año.</summary>
public sealed record LibroBienesInversionDto(int Anio, IReadOnlyList<BienInversionDto> Bienes, decimal RegularizacionAnio,
    decimal Contabilizado, IReadOnlyList<string> Avisos);

public sealed class RegularizarBienesCmd
{
    public int Anio { get; set; }
    /// <summary>Periodo de la declaración: el último del año para la anual; el de la venta para las ventas. Por defecto, 4T.</summary>
    public string? Periodo { get; set; }
    /// <summary>Por defecto, el último día del periodo.</summary>
    public DateOnly? Fecha { get; set; }
}

public sealed record RegularizacionContabilizadaDto(long AsientoId, int AsientoNumero, decimal Importe, IReadOnlyList<AnioBienInversionDto> Lineas);

// ---- Regularización por prorrata definitiva ----

public sealed class LineaRegularizacionProrrataDto
{
    public long FacturaId { get; set; }
    public string Factura { get; set; } = "";
    public string Tercero { get; set; } = "";
    public DateOnly FechaContable { get; set; }
    public decimal Cuota { get; set; }
    public decimal PorcentajeAplicado { get; set; }
    public int PorcentajeDefinitivo { get; set; }
    public decimal Importe { get; set; }
    public bool BienInversion { get; set; }
}

/// <summary>Regularización del año: (cuota × (% definitivo − % aplicado)) por línea con regla PRORRATA.</summary>
public sealed record RegularizacionProrrataDto(int Anio, IReadOnlyList<LineaRegularizacionProrrataDto> Lineas, decimal Importe,
    decimal Contabilizado, long? AsientoId, int? AsientoNumero, IReadOnlyList<string> Avisos);

public sealed class RegularizarProrrataCmd
{
    public int Anio { get; set; }
    /// <summary>Por defecto, el 31 de diciembre del año.</summary>
    public DateOnly? Fecha { get; set; }
}
