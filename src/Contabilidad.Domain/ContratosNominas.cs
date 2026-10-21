namespace Contabilidad.Domain;

public sealed class CrearNominaCmd
{
    public long TerceroId { get; set; }
    public long CanalId { get; set; }
    /// <summary>Fecha de devengo (último día del mes de la nómina): fecha del asiento y periodo del 111.</summary>
    public DateOnly Fecha { get; set; }
    /// <summary>Texto libre: «Octubre 2026», «Paga extra de verano»…</summary>
    public string? Periodo { get; set; }
    public decimal Bruto { get; set; }
    public decimal Especie { get; set; }
    public decimal IngresoACuenta { get; set; }
    public bool IngresoRepercutido { get; set; } = true;
    public decimal Retencion { get; set; }
    public decimal SsTrabajador { get; set; }
    public decimal SsEmpresa { get; set; }
    public decimal Anticipos { get; set; }
}

public sealed class NominaDto
{
    public long Id { get; set; }
    public long TerceroId { get; set; }
    public string Empleado { get; set; } = "";
    public string Nif { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public string? Periodo { get; set; }
    public decimal Bruto { get; set; }
    public decimal Especie { get; set; }
    public decimal IngresoACuenta { get; set; }
    public bool IngresoRepercutido { get; set; }
    public decimal Retencion { get; set; }
    public decimal PorcentajeRetencion { get; set; }
    public decimal SsTrabajador { get; set; }
    public decimal SsEmpresa { get; set; }
    public decimal Anticipos { get; set; }
    public decimal Liquido { get; set; }
    public string Estado { get; set; } = "";
    public long? AsientoId { get; set; }
    public int? AsientoNumero { get; set; }
}

/// <summary>Datos del trabajador que pide el modelo 190 (clave A).</summary>
public sealed class EmpleadoDto
{
    public long TerceroId { get; set; }
    public string Nombre { get; set; } = "";
    public string Nif { get; set; } = "";
    public short? AnioNacimiento { get; set; }
    /// <summary>1 con cónyuge a cargo y sin hijos… 2 con cónyuge sin rentas superiores a 1.500 €, 3 resto (instrucciones del 190).</summary>
    public short SituacionFamiliar { get; set; } = 3;
    public string? NifConyuge { get; set; }
    /// <summary>0 sin discapacidad, 1 del 33 % al 65 %, 2 del 33 % al 65 % con ayuda de terceros o movilidad reducida, 3 igual o superior al 65 %.</summary>
    public short Discapacidad { get; set; }
    /// <summary>1 general, 2 inferior al año, 3 relación laboral especial de carácter dependiente, 4 rendimientos que se perciben de forma esporádica.</summary>
    public short Contrato { get; set; } = 1;
    public bool MovilidadGeografica { get; set; }
}
