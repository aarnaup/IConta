namespace Contabilidad.Domain;

// ---- Cuentas bancarias de clientes y proveedores ----

public sealed class CuentaTerceroDto
{
    public long Id { get; set; }
    public long TerceroId { get; set; }
    public string? Alias { get; set; }
    public string Iban { get; set; } = "";
    public string? Bic { get; set; }
    public bool Activa { get; set; }
}

public sealed class CrearCuentaTerceroCmd
{
    public string Iban { get; set; } = "";
    public string? Bic { get; set; }
    public string? Alias { get; set; }
}

// ---- Mandatos de domiciliación ----

public sealed class MandatoDto
{
    public long Id { get; set; }
    public long TerceroId { get; set; }
    public string Tercero { get; set; } = "";
    public long CuentaBancariaId { get; set; }
    public string Iban { get; set; } = "";
    public string Referencia { get; set; } = "";
    public DateOnly FechaFirma { get; set; }
    public string Esquema { get; set; } = "";
    public bool Recurrente { get; set; }
    public bool Usado { get; set; }
    public bool Activo { get; set; }
}

public sealed class CrearMandatoCmd
{
    public long TerceroId { get; set; }
    /// <summary>Cuenta bancaria del cliente (de sus cuentas dadas de alta).</summary>
    public long CuentaBancariaId { get; set; }
    /// <summary>Si se deja vacía, se genera (MND-…).</summary>
    public string? Referencia { get; set; }
    public DateOnly FechaFirma { get; set; }
    /// <summary>CORE (consumidores y empresas) o B2B (solo empresas, sin derecho a devolución).</summary>
    public string Esquema { get; set; } = "CORE";
    public bool Recurrente { get; set; } = true;
}

// ---- Remesas ----

/// <summary>Vencimiento que se puede incluir en una remesa; <see cref="Problema"/> explica por qué no, si es el caso.</summary>
public sealed class CandidatoRemesaDto
{
    public long VencimientoId { get; set; }
    public long TerceroId { get; set; }
    public string Tercero { get; set; } = "";
    public string? Factura { get; set; }
    public DateOnly FechaVencimiento { get; set; }
    public decimal ImportePendiente { get; set; }
    public string? FormaPago { get; set; }
    public string? Iban { get; set; }
    public long? MandatoId { get; set; }
    public string? Mandato { get; set; }
    public string? Esquema { get; set; }
    public string? Problema { get; set; }
}

public sealed class CrearRemesaCmd
{
    /// <summary>COBRO (adeudos domiciliados) o PAGO (transferencias).</summary>
    public string Tipo { get; set; } = "COBRO";
    public long CuentaBancariaId { get; set; }
    /// <summary>Fecha de cobro de los recibos o de ejecución de las transferencias.</summary>
    public DateOnly FechaCargo { get; set; }
    public List<long> VencimientoIds { get; set; } = [];
}

public sealed class RemesaDto
{
    public long Id { get; set; }
    public string Tipo { get; set; } = "";
    public string Referencia { get; set; } = "";
    public long CuentaBancariaId { get; set; }
    public string CuentaBancaria { get; set; } = "";
    public DateOnly FechaCargo { get; set; }
    public decimal Importe { get; set; }
    public int NumOperaciones { get; set; }
    public string Estado { get; set; } = "";
    public int? AsientoNumero { get; set; }
    public int Devueltos { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
}

public sealed class RemesaLineaDto
{
    public long Id { get; set; }
    public long VencimientoId { get; set; }
    public string Tercero { get; set; } = "";
    public string? Factura { get; set; }
    public decimal Importe { get; set; }
    public string Iban { get; set; } = "";
    public string? Secuencia { get; set; }
    public string? Mandato { get; set; }
    public string EndToEndId { get; set; } = "";
    public string Estado { get; set; } = "";
    public DateOnly? FechaDevolucion { get; set; }
    public string? MotivoDevolucion { get; set; }
}

public sealed record RemesaDetalleDto(RemesaDto Remesa, IReadOnlyList<RemesaLineaDto> Lineas);

public sealed class ContabilizarRemesaCmd
{
    /// <summary>Fecha en que el banco abona (cobros) o carga (pagos) la remesa.</summary>
    public DateOnly Fecha { get; set; }
    /// <summary>Canal para los vencimientos que no vienen de una factura con canal.</summary>
    public long? CanalId { get; set; }
}

public sealed class DevolucionReciboCmd
{
    public DateOnly Fecha { get; set; }
    public string? Motivo { get; set; }
    /// <summary>Gastos de devolución que cobra el banco (opcional).</summary>
    public decimal Gastos { get; set; }
    /// <summary>Cuenta de los gastos (por defecto, servicios bancarios 626).</summary>
    public long? CuentaGastosId { get; set; }
    public long? CanalId { get; set; }
}

public sealed record AsientoHechoDto(long AsientoId, int AsientoNumero);
