using Contabilidad.Domain.Inmovilizado;

namespace Contabilidad.Domain;

public sealed class CategoriaInmovilizadoDto
{
    public long Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
    public long? CuentaInmovilizadoId { get; set; }
    public string? CuentaInmovilizado { get; set; }
    public long? CuentaAmortAcumuladaId { get; set; }
    public string? CuentaAmortAcumulada { get; set; }
    public long? CuentaGastoAmortId { get; set; }
    public string? CuentaGastoAmort { get; set; }
    public string MetodoDefecto { get; set; } = "";
    public int? VidaUtilMesesDefecto { get; set; }
}

public sealed class CrearCategoriaCmd
{
    public string Nombre { get; set; } = "";
    /// <summary>MATERIAL, INTANGIBLE o INVERSION_INMOBILIARIA.</summary>
    public string Tipo { get; set; } = "MATERIAL";
    public long CuentaInmovilizadoId { get; set; }
    public long? CuentaAmortAcumuladaId { get; set; }
    public long? CuentaGastoAmortId { get; set; }
    /// <summary>LINEAL o NO_AMORTIZA.</summary>
    public string MetodoDefecto { get; set; } = "LINEAL";
    public int? VidaUtilMesesDefecto { get; set; }
}

public sealed class AltaInmovilizadoCmd
{
    /// <summary>Si se deja vacío, se numera solo (INM-0001…).</summary>
    public string? Codigo { get; set; }
    public string Descripcion { get; set; } = "";
    public long CategoriaId { get; set; }
    public long CanalId { get; set; }
    public DateOnly FechaAdquisicion { get; set; }
    /// <summary>Por defecto, la de adquisición (puesta en funcionamiento).</summary>
    public DateOnly? FechaInicioAmortizacion { get; set; }
    public decimal ValorAdquisicion { get; set; }
    public decimal ValorResidual { get; set; }
    /// <summary>Por defecto, los de la categoría.</summary>
    public string? Metodo { get; set; }
    public int? VidaUtilMeses { get; set; }
    /// <summary>Amortización ya registrada antes de llevar el elemento aquí (traspasos desde otro programa).</summary>
    public decimal AmortizacionPrevia { get; set; }
    public long? FacturaId { get; set; }
    public long? ProveedorId { get; set; }
    /// <summary>
    /// Si se indica, se contabiliza la adquisición: cuenta de inmovilizado (D) contra esta cuenta (H), p. ej. 523 o 572.
    /// Sin ella no se genera asiento (lo normal es que la compra ya esté contabilizada con su factura).
    /// </summary>
    public long? CuentaContrapartidaId { get; set; }
}

public sealed class InmovilizadoResumenDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Categoria { get; set; } = "";
    public string Canal { get; set; } = "";
    public DateOnly FechaAdquisicion { get; set; }
    public DateOnly FechaInicioAmortizacion { get; set; }
    public decimal ValorAdquisicion { get; set; }
    public decimal ValorResidual { get; set; }
    public string Metodo { get; set; } = "";
    public int? VidaUtilMeses { get; set; }
    public decimal AmortizacionPrevia { get; set; }
    public string Estado { get; set; } = "";
    public DateOnly? FechaBaja { get; set; }
    public decimal Amortizado { get; set; }
    public DateOnly? AmortizadoHasta { get; set; }
    public decimal ValorNeto => ValorAdquisicion - Amortizado;
}

public sealed record MovimientoAmortizacionDto(DateOnly? Desde, DateOnly? Hasta, decimal Importe, decimal Acumulada, long? AsientoId, int? AsientoNumero,
    string? Ejercicio);

public sealed record FichaInmovilizadoDto(InmovilizadoResumenDto Elemento, DateOnly? FinAmortizacion, IReadOnlyList<TramoCuadro> Cuadro,
    IReadOnlyList<MovimientoAmortizacionDto> Movimientos);

public sealed record LineaAmortizacionDto(long InmovilizadoId, string Codigo, string Descripcion, string Canal, decimal Importe, decimal Acumulada,
    decimal ValorNeto);

public sealed record AmortizacionVista(DateOnly Hasta, long DiarioId, string Concepto, IReadOnlyList<LineaAmortizacionDto> Lineas, decimal Total,
    IReadOnlyList<string> Avisos, long? AsientoId, int? AsientoNumero);

public sealed record EjecucionAmortizacionDto(long AsientoId, int Numero, DateOnly Fecha, string Concepto, int Elementos, decimal Total);

public sealed class BajaInmovilizadoCmd
{
    public DateOnly Fecha { get; set; }
    /// <summary>BAJA (sin cobro: robo, desguace…) o VENTA.</summary>
    public string Tipo { get; set; } = "BAJA";
    /// <summary>Precio de venta sin impuestos.</summary>
    public decimal ImporteVenta { get; set; }
    /// <summary>Cuenta donde se cobra la venta (543, 572…).</summary>
    public long? CuentaCobroId { get; set; }
}

public sealed record BajaRealizadaDto(long AsientoId, int AsientoNumero, ResultadoBaja Resultado, IReadOnlyList<ApuntePrevioDto> Asiento);
