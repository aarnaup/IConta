using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Domain;

public sealed class CalcularModeloCmd
{
    public string Modelo { get; set; } = "";
    public int Anio { get; set; }
    /// <summary>1T–4T, 01–12 o 0A.</summary>
    public string Periodo { get; set; } = "";
    /// <summary>Si el resultado es a favor del contribuyente en el último periodo: pedir devolución en vez de compensar.</summary>
    public bool SolicitaDevolucion { get; set; }
}

public sealed class PresentarDeclaracionCmd
{
    public DateOnly FechaPresentacion { get; set; }
    public string? Justificante { get; set; }
}

public sealed class DeclaracionResumenDto
{
    public long Id { get; set; }
    public string Modelo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public int Anio { get; set; }
    public string Periodo { get; set; } = "";
    public short NumeroPresentacion { get; set; }
    public string Estado { get; set; } = "";
    public decimal? Resultado { get; set; }
    public decimal? AIngresar { get; set; }
    public decimal? ADevolver { get; set; }
    public decimal? ACompensar { get; set; }
    public DateOnly? FechaPresentacion { get; set; }
    public string? Justificante { get; set; }
    /// <summary>Asiento de liquidación (303/420), si está contabilizado.</summary>
    public long? AsientoId { get; set; }
    public int? AsientoNumero { get; set; }
}

/// <summary>Declaración calculada o guardada, tal y como la ve la interfaz.</summary>
public sealed record DeclaracionVista(
    long? Id, string Modelo, string Nombre, string Estado, int Anio, string Periodo, DateOnly FechaInicio, DateOnly FechaFin,
    IReadOnlyList<Casilla> Casillas, decimal Resultado, decimal AIngresar, decimal ADevolver, decimal ACompensar,
    IReadOnlyList<string> Avisos, object? Detalle, DateOnly PlazoDesde, DateOnly PlazoHasta);

/// <summary>Libro registro de facturas expedidas o recibidas de un periodo (por fecha contable, como las autoliquidaciones).</summary>
public sealed record LibroRegistroDto(
    string Libro, string Impuesto, int Anio, string Periodo, DateOnly Desde, DateOnly Hasta, int Facturas,
    IReadOnlyList<LineaLibroDto> Lineas, IReadOnlyList<TotalLibro> Totales, decimal TotalBase, decimal TotalCuota, decimal TotalRecargo,
    decimal TotalCuotaDeducible, decimal TotalRetencion, decimal TotalFacturas, IReadOnlyList<string> Avisos);
