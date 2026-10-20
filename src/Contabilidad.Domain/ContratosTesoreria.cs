namespace Contabilidad.Domain;

public sealed class FormaPagoDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
    public short NumPlazos { get; set; }
    public short DiasPrimerPlazo { get; set; }
    public short DiasEntrePlazos { get; set; }
}

public sealed class CrearFormaPagoCmd
{
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "TRANSFERENCIA";
    public short NumPlazos { get; set; } = 1;
    public short DiasPrimerPlazo { get; set; }
    public short DiasEntrePlazos { get; set; }
}

public sealed class AsignarFormaPagoCmd { public long? FormaPagoId { get; set; } }

public sealed class CuentaBancariaDto
{
    public long Id { get; set; }
    public string Alias { get; set; } = "";
    public string Iban { get; set; } = "";
    public string? Bic { get; set; }
    public string? Banco { get; set; }
    public long? CuentaContableId { get; set; }
    public string? CuentaContable { get; set; }
    public bool Activa { get; set; }
    /// <summary>Suma de los movimientos registrados desde la aplicación (no es el saldo del banco hasta conciliar con el extracto).</summary>
    public decimal Saldo { get; set; }
}

public sealed class CrearCuentaBancariaCmd
{
    public string Alias { get; set; } = "";
    public string Iban { get; set; } = "";
    public string? Bic { get; set; }
    public int? BancoId { get; set; }
    /// <summary>Subcuenta 572xxxx del plan de la empresa en la que se contabilizan los cobros y pagos.</summary>
    public long CuentaContableId { get; set; }
}

public sealed class VencimientoDto
{
    public long Id { get; set; }
    public string Tipo { get; set; } = "";
    public long TerceroId { get; set; }
    public string Tercero { get; set; } = "";
    public long? FacturaId { get; set; }
    public string? Factura { get; set; }
    public DateOnly FechaVencimiento { get; set; }
    public decimal Importe { get; set; }
    public decimal ImportePendiente { get; set; }
    public string Estado { get; set; } = "";
    public short NumPlazo { get; set; }
    public short TotalPlazos { get; set; }
    public string? FormaPago { get; set; }
    public bool Vencido { get; set; }
}

public sealed class FiltroVencimientos
{
    public string? Tipo { get; set; }          // COBRO | PAGO
    public string? Estado { get; set; }        // PENDIENTE | PARCIAL | LIQUIDADO | ANULADO | ABIERTOS (pendiente o parcial)
    public DateOnly? Desde { get; set; }
    public DateOnly? Hasta { get; set; }
    public long? TerceroId { get; set; }
    public int Limite { get; set; } = 200;
}

public sealed class LiquidarVencimientoCmd
{
    public DateOnly Fecha { get; set; }
    /// <summary>Si no se indica, se liquida todo lo pendiente.</summary>
    public decimal? Importe { get; set; }
    public long CuentaBancariaId { get; set; }
    /// <summary>Canal del asiento; por defecto el de la factura.</summary>
    public long? CanalId { get; set; }
    public long? DiarioId { get; set; }
}

public sealed record LiquidacionCreada(long CobroPagoId, long AsientoId, int AsientoNumero, string EstadoVencimiento, decimal Pendiente);

public sealed class PrevisionDto
{
    /// <summary>"VENCIDO" o el mes (yyyy-MM).</summary>
    public string Periodo { get; set; } = "";
    public decimal Cobros { get; set; }
    public decimal Pagos { get; set; }
    public decimal Neto => Cobros - Pagos;
}

/// <summary>Cobro o pago registrado de un vencimiento.</summary>
public sealed class CobroPagoDto
{
    public long Id { get; set; }
    public DateOnly Fecha { get; set; }
    public decimal Importe { get; set; }
    public long? AsientoId { get; set; }
    public int? AsientoNumero { get; set; }
    public string? Ejercicio { get; set; }
    public bool Conciliado { get; set; }
    public bool EnRemesa { get; set; }
}
