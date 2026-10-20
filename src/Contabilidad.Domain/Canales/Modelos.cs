namespace Contabilidad.Domain.Canales;

public enum TipoCanal { Base, Derivado }
public enum ModoReparto { Porcentaje, Formula, Resto }

/// <summary>Línea de una versión de canal derivado: a qué canal base va y cómo se calcula.</summary>
public sealed record LineaCanal(
    long CanalBaseId, ModoReparto Modo, decimal? Porcentaje, string? Expresion, short Decimales, short Orden);

/// <summary>Canal con la versión de fórmula vigente a una fecha (Lineas vacío si es base).</summary>
public sealed record ConfiguracionCanal(
    long CanalId, string Codigo, TipoCanal Tipo, long? VersionId, IReadOnlyList<LineaCanal> Lineas);

/// <summary>Importe resultante en un canal base (se guarda en conta.apunte_canal).</summary>
public sealed record ImporteCanal(long CanalBaseId, decimal Debe, decimal Haber, long? CanalVersionId);
