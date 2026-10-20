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

// ---- Aplicación del resultado ----

public sealed class LineaAplicacionCmd
{
    /// <summary>RESERVA_LEGAL, RESERVA_VOLUNTARIA, REMANENTE, COMPENSAR_PERDIDAS, DIVIDENDOS, RESULTADOS_NEGATIVOS o CUENTA.</summary>
    public string Destino { get; set; } = "";
    /// <summary>IMPORTE, PORCENTAJE o RESTO.</summary>
    public string Modo { get; set; } = "IMPORTE";
    public decimal Valor { get; set; }
    /// <summary>Solo con destino CUENTA: subcuenta del grupo 1.</summary>
    public long? CuentaId { get; set; }
}

public sealed class AplicarResultadoCmd
{
    /// <summary>Fecha del acuerdo (junta general); debe estar en el ejercicio elegido. Por defecto, hoy.</summary>
    public DateOnly? Fecha { get; set; }
    /// <summary>Canal base; sin indicar, todos los canales con resultado pendiente.</summary>
    public long? CanalId { get; set; }
    public long? DiarioId { get; set; }
    /// <summary>Texto libre para el concepto (p. ej. «Junta general de 15/09/2027»).</summary>
    public string? Acuerdo { get; set; }
    public List<LineaAplicacionCmd> Lineas { get; set; } = [];
}

public sealed record SituacionCanalDto(long CanalId, string Canal, decimal Resultado, decimal Capital, decimal ReservaLegal,
    decimal ReservaLegalMinima, decimal PerdidasAnteriores, decimal DividendoACuenta);

public sealed record AsientoAplicacionDto(long AsientoId, int Numero, DateOnly Fecha, string Concepto);

/// <summary>Resultado pendiente de aplicar en cada canal y lo que pide la ley (reserva legal), más las aplicaciones ya hechas.</summary>
public sealed record PropuestaAplicacionDto(string Ejercicio, string? ResultadoDe, IReadOnlyList<SituacionCanalDto> Canales,
    IReadOnlyList<AsientoAplicacionDto> Aplicaciones, IReadOnlyList<string> Avisos);

public sealed record RepartoDto(string Destino, string Texto, string Cuenta, decimal Importe);

public sealed record AplicacionCanalDto(long CanalId, string Canal, decimal Resultado, IReadOnlyList<RepartoDto> Reparto);

public sealed record AplicacionResultadoVista(DateOnly Fecha, long DiarioId, string Concepto, IReadOnlyList<AplicacionCanalDto> Canales,
    IReadOnlyList<ApuntePrevioDto> Asiento, IReadOnlyList<string> Avisos, long? AsientoId, int? AsientoNumero);
