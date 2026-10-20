namespace Contabilidad.Domain;

// ---------------------------------------------------------------- analítica
public sealed class ElementoAnaliticoDto
{
    public long Id { get; set; }
    public long DimensionId { get; set; }
    public long? PadreId { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public bool Activo { get; set; }
}

public sealed class DimensionAnaliticaDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public List<ElementoAnaliticoDto> Elementos { get; set; } = [];
}

public sealed record DimensionCmd(string Codigo, string Nombre);
public sealed record ElementoCmd(string Codigo, string Nombre, long? PadreId, bool Activo = true);

/// <summary>Imputación de un apunte (o reparto por defecto de una cuenta) a un elemento.</summary>
public sealed class ImputacionCmd
{
    public long ElementoId { get; set; }
    public decimal Porcentaje { get; set; } = 100;
}

public sealed class RepartoCuentaDto
{
    public string Cuenta { get; set; } = "";
    public string CuentaNombre { get; set; } = "";
    public bool RequiereAnalitica { get; set; }
    public List<ImputacionDto> Reparto { get; set; } = [];
}

public sealed class ImputacionDto
{
    public long ElementoId { get; set; }
    public string Dimension { get; set; } = "";
    public string Elemento { get; set; } = "";
    public string ElementoNombre { get; set; } = "";
    public decimal Porcentaje { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
}

/// <summary>Reparto por defecto de una cuenta (o de sus subcuentas, si es una cuenta padre) y si exige analítica al teclear.</summary>
public sealed class GuardarRepartoCuentaCmd
{
    public string Cuenta { get; set; } = "";
    public bool RequiereAnalitica { get; set; }
    public List<ImputacionCmd> Reparto { get; set; } = [];
}

public sealed class ApunteAnaliticaDto
{
    public long ApunteId { get; set; }
    public long AsientoId { get; set; }
    public int Numero { get; set; }
    public DateOnly Fecha { get; set; }
    public string Cuenta { get; set; } = "";
    public string CuentaNombre { get; set; } = "";
    public string? Concepto { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
    public List<ImputacionDto> Imputaciones { get; set; } = [];
}

public sealed class FilaInformeAnaliticoDto
{
    public long? ElementoId { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public int Nivel { get; set; }
    public bool EsCuenta { get; set; }
    public decimal Ingresos { get; set; }
    public decimal Gastos { get; set; }
    public decimal Resultado => Ingresos - Gastos;
    public decimal Otros { get; set; }
}

public sealed class InformeAnaliticoDto
{
    public string Dimension { get; set; } = "";
    public string Canal { get; set; } = "";
    public DateOnly Desde { get; set; }
    public DateOnly Hasta { get; set; }
    public List<FilaInformeAnaliticoDto> Filas { get; set; } = [];
    public FilaInformeAnaliticoDto SinAsignar { get; set; } = new();
    public FilaInformeAnaliticoDto Total { get; set; } = new();
    public List<string> Avisos { get; set; } = [];
}

// ---------------------------------------------------------------- presupuestos
public sealed class PresupuestoDto
{
    public long Id { get; set; }
    public long EjercicioId { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public int Version { get; set; }
    public long? CanalBaseId { get; set; }
    public string? Canal { get; set; }
    public string Estado { get; set; } = "";
    public decimal Ingresos { get; set; }
    public decimal Gastos { get; set; }
    public List<string> Periodos { get; set; } = [];
    public List<LineaPresupuestoDto> Lineas { get; set; } = [];
}

/// <summary>Una fila del presupuesto: cuenta (o prefijo), elemento analítico opcional e importe de cada periodo.</summary>
public sealed class LineaPresupuestoDto
{
    public string CuentaPatron { get; set; } = "";
    public string? CuentaNombre { get; set; }
    public long? ElementoId { get; set; }
    public string? Elemento { get; set; }
    public decimal[] Importes { get; set; } = [];
    public decimal Total => Importes.Sum();
}

public sealed record CrearPresupuestoCmd(string Codigo, string Nombre, long? CanalId);

public sealed class LineaPresupuestoCmd
{
    public string CuentaPatron { get; set; } = "";
    public long? ElementoId { get; set; }
    /// <summary>Importe de cada periodo; si no se indica, <see cref="Total"/> se reparte a partes iguales.</summary>
    public decimal[]? Importes { get; set; }
    public decimal? Total { get; set; }
}

public sealed class GuardarLineasPresupuestoCmd
{
    public List<LineaPresupuestoCmd> Lineas { get; set; } = [];
}

/// <summary>Rellena el presupuesto con lo real de otro ejercicio (grupos 6 y 7), agrupado por cuentas de <see cref="Digitos"/> dígitos.</summary>
public sealed class GenerarDesdeRealCmd
{
    public long EjercicioOrigenId { get; set; }
    public decimal IncrementoPorcentaje { get; set; }
    public int Digitos { get; set; } = 3;
    public long? DimensionId { get; set; }
}

public sealed class FilaComparativaDto
{
    public string CuentaPatron { get; set; } = "";
    public string? CuentaNombre { get; set; }
    public string? Elemento { get; set; }
    public decimal Presupuesto { get; set; }
    public decimal Real { get; set; }
    public decimal Desviacion => Real - Presupuesto;
    public decimal? Ejecucion { get; set; }
    public bool Favorable { get; set; }
    public decimal PresupuestoAnual { get; set; }
    public decimal[] PresupuestoPeriodos { get; set; } = [];
    public decimal[] RealPeriodos { get; set; } = [];
}

public sealed class ComparativaPresupuestoDto
{
    public PresupuestoDto Presupuesto { get; set; } = new();
    public int HastaPeriodo { get; set; }
    public List<FilaComparativaDto> Filas { get; set; } = [];
    public decimal IngresosPresupuesto { get; set; }
    public decimal IngresosReal { get; set; }
    public decimal GastosPresupuesto { get; set; }
    public decimal GastosReal { get; set; }
    public decimal ResultadoPresupuesto => IngresosPresupuesto - GastosPresupuesto;
    public decimal ResultadoReal => IngresosReal - GastosReal;
    public List<string> Avisos { get; set; } = [];
}
