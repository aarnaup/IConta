using Contabilidad.Domain.Facturacion;

namespace Contabilidad.Domain;

public sealed class FacturaLineaCmd
{
    public long TipoIvaId { get; set; }
    public decimal Base { get; set; }
    public string TipoOperacion { get; set; } = "INTERIOR";     // INTERIOR | EXENTA | NO_SUJETA
    public string ReglaDeduccion { get; set; } = "TOTAL";        // TOTAL | NINGUNA | PRORRATA | PORCENTAJE_AFECTACION
    public decimal? PorcentajeDeduccion { get; set; }
    public bool EsBienInversion { get; set; }
}

public sealed class CrearFacturaCmd
{
    public string Libro { get; set; } = "EMITIDAS";              // EMITIDAS | RECIBIDAS
    public string TipoFactura { get; set; } = "ORDINARIA";        // ORDINARIA | SIMPLIFICADA | RECTIFICATIVA
    public string? Serie { get; set; }
    public string Numero { get; set; } = "";
    public long TerceroId { get; set; }
    public long? ActividadId { get; set; }
    public long CanalId { get; set; }
    public long? PlantillaId { get; set; }
    public long? DiarioId { get; set; }
    public DateOnly FechaExpedicion { get; set; }
    public DateOnly? FechaOperacion { get; set; }                 // devengo: define el % de IVA/IGIC
    public DateOnly? FechaContable { get; set; }                  // por defecto = expedición
    public string? Descripcion { get; set; }
    public long? TipoRetencionId { get; set; }
    /// <summary>Forma de pago de esta factura; por defecto la del tercero. Sin ninguna, un único vencimiento a la fecha de expedición.</summary>
    public long? FormaPagoId { get; set; }
    public bool Borrador { get; set; }
    public List<FacturaLineaCmd> Lineas { get; set; } = [];
}

public sealed record ApuntePrevioDto(long CuentaId, string Cuenta, string CuentaNombre, long CanalId, decimal Debe, decimal Haber, string? Concepto);

public sealed record FacturaPrevisualizacion(
    FacturaCalculada Calculo, IReadOnlyList<ApuntePrevioDto> Asiento,
    long PlantillaId, string PlantillaNombre, long DiarioId, string Concepto);

public sealed record FacturaCreada(long Id, long? AsientoId, int? AsientoNumero, string Estado, TotalesFactura Totales);

public sealed class FacturaResumenDto
{
    public long Id { get; set; }
    public string Libro { get; set; } = "";
    public string? Serie { get; set; }
    public string Numero { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public string Tercero { get; set; } = "";
    public decimal BaseImponible { get; set; }
    public decimal Cuota { get; set; }
    public decimal Recargo { get; set; }
    public decimal Retencion { get; set; }
    public decimal Total { get; set; }
    public string Estado { get; set; } = "";
    public int? AsientoNumero { get; set; }
}

public sealed class TerceroDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Nif { get; set; } = "";
    public bool EsCliente { get; set; }
    public bool EsProveedor { get; set; }
    public bool RecargoEquivalencia { get; set; }
    public long? TipoRetencionDefectoId { get; set; }
    public long? TipoIvaDefectoId { get; set; }
}

public sealed class CrearTerceroCmd
{
    public string TipoDocumento { get; set; } = "NIF";            // NIF | NIE | CIF | OTRO
    public string Nif { get; set; } = "";
    public string Nombre { get; set; } = "";
    public bool EsCliente { get; set; }
    public bool EsProveedor { get; set; }
    public bool RecargoEquivalencia { get; set; }
    public long? TipoRetencionDefectoId { get; set; }
    public long? TipoIvaDefectoId { get; set; }
}

public sealed class TipoIvaDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Impuesto { get; set; } = "";
    public bool EsExento { get; set; }
    public decimal Porcentaje { get; set; }
    public decimal Recargo { get; set; }
}

public sealed class TipoRetencionDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Naturaleza { get; set; } = "";
    public decimal Porcentaje { get; set; }
}

public sealed class PlantillaDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string TipoOperacion { get; set; } = "";
    public bool EsGlobal { get; set; }
}
