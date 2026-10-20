namespace Contabilidad.Domain.Facturacion;

public enum LibroFactura { Emitidas, Recibidas }

/// <summary>Línea de IVA/IGIC ya con el % vigente resuelto (y recargo, si aplica).</summary>
public sealed record LineaIvaEntrada(
    long TipoIvaId, long? VigenciaId, string TipoOperacion, decimal Base,
    decimal Porcentaje, decimal PorcentajeRecargo,
    string ReglaDeduccion, decimal? PorcentajeDeduccion, bool EsBienInversion);

public sealed record LineaIvaCalculada(
    long TipoIvaId, long? VigenciaId, string TipoOperacion, decimal Base,
    decimal Porcentaje, decimal Cuota, decimal PorcentajeRecargo, decimal CuotaRecargo,
    string ReglaDeduccion, decimal PorcentajeDeduccion, decimal CuotaDeducible, bool EsBienInversion);

public sealed record RetencionEntrada(long TipoRetencionId, long? VigenciaId, decimal Porcentaje);
public sealed record RetencionCalculada(long TipoRetencionId, long? VigenciaId, decimal Base, decimal Porcentaje, decimal Importe);

/// <summary>
/// Total = líquido a cobrar/pagar = base + cuota + recargo − retención − cuota autorrepercutida
/// (la de adquisiciones intracomunitarias e inversión del sujeto pasivo, que no se paga al proveedor).
/// </summary>
public sealed record TotalesFactura(
    decimal Base, decimal Cuota, decimal CuotaDeducible, decimal CuotaNoDeducible,
    decimal Recargo, decimal Retencion, decimal Total, decimal CuotaAutorrepercutida = 0);

public sealed record FacturaCalculada(
    IReadOnlyList<LineaIvaCalculada> Lineas, RetencionCalculada? Retencion, TotalesFactura Totales);

/// <summary>Línea de plantilla de asiento (conta.plantilla_asiento_linea).</summary>
public sealed record LineaPlantilla(
    short Orden, long? CuentaId, string? CuentaRol, string Lado, string OrigenImporte,
    string? Expresion, bool UsaTercero, long? CanalId, string? Concepto);

/// <summary>Regla de cuenta por rol (conta.cuenta_automatica).</summary>
public sealed record CuentaAutomatica(string Rol, long? TipoIvaId, long? TipoRetencionId, long CuentaId);
