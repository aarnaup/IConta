namespace Contabilidad.Domain;

/// <summary>Error de regla de negocio (se traduce a HTTP 400).</summary>
public sealed class ReglaContableException(string mensaje, string? detalle = null) : Exception(mensaje)
{
    public string? Detalle { get; } = detalle;
}
