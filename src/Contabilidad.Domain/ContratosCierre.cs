namespace Contabilidad.Domain;

// ---- Asiento de liquidación del IVA/IGIC ----

public sealed class LiquidacionIvaCmd
{
    /// <summary>Fecha del asiento; por defecto, el último día del periodo de la declaración.</summary>
    public DateOnly? Fecha { get; set; }
    /// <summary>Por defecto, el diario general.</summary>
    public long? DiarioId { get; set; }
}

public sealed record LiquidacionCanalDto(long CanalId, string Canal, decimal Repercutido, decimal Soportado, decimal Compensado,
    decimal AIngresar, decimal AFavor);

public sealed record LiquidacionIvaVista(
    long DeclaracionId, string Modelo, int Anio, string Periodo, DateOnly Fecha, long DiarioId, string Concepto,
    decimal ResultadoDeclaracion, IReadOnlyList<LiquidacionCanalDto> Canales, IReadOnlyList<ApuntePrevioDto> Asiento,
    IReadOnlyList<string> Avisos, long? AsientoId, int? AsientoNumero);

// ---- Cierre de ejercicio ----

public sealed record ResultadoCanalDto(long CanalId, string Canal, decimal Ingresos, decimal Gastos, decimal Resultado);

public sealed record EjercicioRefDto(long Id, string Codigo, string Estado);

/// <summary>Lo que pasaría al cerrar: si se puede, qué lo impide, avisos y el resultado del ejercicio por canal.</summary>
public sealed record EstadoCierreDto(
    long EjercicioId, string Codigo, string Estado, bool PuedeCerrar, IReadOnlyList<string> Bloqueos, IReadOnlyList<string> Avisos,
    IReadOnlyList<ResultadoCanalDto> Resultados, EjercicioRefDto? Siguiente);

public sealed record AsientoGeneradoDto(string Tipo, long Id, int Numero, long EjercicioId, DateOnly Fecha, int Apuntes);

public sealed record CierreRealizadoDto(IReadOnlyList<AsientoGeneradoDto> Asientos, IReadOnlyList<ResultadoCanalDto> Resultados,
    IReadOnlyList<string> Avisos);
