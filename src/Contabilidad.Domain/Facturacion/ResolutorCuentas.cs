namespace Contabilidad.Domain.Facturacion;

/// <summary>
/// Resuelve la cuenta de una línea de plantilla: cuenta fija, o por rol.
/// CLIENTE/PROVEEDOR salen del tercero; el resto de conta.cuenta_automatica
/// (la regla más específica por tipo de IVA/retención gana sobre la genérica).
/// </summary>
public sealed class ResolutorCuentas(
    IReadOnlyList<CuentaAutomatica> reglas, long? cuentaCliente, long? cuentaProveedor)
{
    public long Resolver(long? cuentaFija, string? rol, long? tipoIvaId = null, long? tipoRetencionId = null)
    {
        if (cuentaFija is not null) return cuentaFija.Value;
        if (string.IsNullOrWhiteSpace(rol))
            throw new ReglaContableException("Una línea de plantilla no tiene cuenta ni rol.");

        if (rol == "CLIENTE")
            return cuentaCliente ?? throw new ReglaContableException("El tercero no tiene cuenta de cliente.");
        if (rol == "PROVEEDOR")
            return cuentaProveedor ?? throw new ReglaContableException("El tercero no tiene cuenta de proveedor.");

        var regla = reglas
            .Where(r => r.Rol == rol
                     && (r.TipoIvaId is null || r.TipoIvaId == tipoIvaId)
                     && (r.TipoRetencionId is null || r.TipoRetencionId == tipoRetencionId))
            .OrderBy(r => r.TipoIvaId is null ? 1 : 0)
            .ThenBy(r => r.TipoRetencionId is null ? 1 : 0)
            .FirstOrDefault();

        return regla?.CuentaId
               ?? throw new ReglaContableException($"No hay cuenta configurada para el rol '{rol}'.",
                   "Revisa conta.cuenta_automatica de la empresa.");
    }
}
