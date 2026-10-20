namespace Contabilidad.Domain;

public sealed class ImportarExtractoCmd
{
    /// <summary>Cuenta bancaria de la empresa. En Norma 43 se deduce del fichero si se deja vacía; en CSV es obligatoria.</summary>
    public long? CuentaBancariaId { get; set; }
    public string? Nombre { get; set; }
    /// <summary>Contenido del fichero en base64.</summary>
    public string Contenido { get; set; } = "";
}

public sealed record ExtractoImportadoDto(long Id, long CuentaBancariaId, string Cuenta, DateOnly Desde, DateOnly Hasta, decimal? SaldoInicial,
    decimal? SaldoFinal, int Lineas, int Nuevas, int Repetidas);

public sealed record ImportacionDto(string Formato, IReadOnlyList<ExtractoImportadoDto> Extractos, IReadOnlyList<string> Avisos);

public sealed class ExtractoResumenDto
{
    public long Id { get; set; }
    public long CuentaBancariaId { get; set; }
    public string Cuenta { get; set; } = "";
    public string Formato { get; set; } = "";
    public string? Fichero { get; set; }
    public DateOnly FechaDesde { get; set; }
    public DateOnly FechaHasta { get; set; }
    public decimal? SaldoInicial { get; set; }
    public decimal? SaldoFinal { get; set; }
    public int Lineas { get; set; }
    public int Conciliadas { get; set; }
    public DateTimeOffset ImportadoEn { get; set; }
}

public sealed class LineaExtractoDto
{
    public long Id { get; set; }
    public long ExtractoId { get; set; }
    public DateOnly Fecha { get; set; }
    public DateOnly? FechaValor { get; set; }
    public decimal Importe { get; set; }
    public string? Concepto { get; set; }
    public string? ConceptoComun { get; set; }
    public string? Documento { get; set; }
    public string? Referencia1 { get; set; }
    public string? Referencia2 { get; set; }
    public string Estado { get; set; } = "";
}

/// <summary>Apunte de la cuenta contable del banco. Importe = debe − haber (positivo, entra dinero).</summary>
public sealed class ApunteBancoDto
{
    public long ApunteId { get; set; }
    public long AsientoId { get; set; }
    public int Numero { get; set; }
    public string Ejercicio { get; set; } = "";
    public string Diario { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public decimal Importe { get; set; }
    public string? Concepto { get; set; }
}

public sealed class ConciliacionDto
{
    public long Id { get; set; }
    public string Tipo { get; set; } = "";
    public decimal Importe { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public string? Lineas { get; set; }
    public string? Apuntes { get; set; }
}

/// <summary>
/// Estado de la conciliación a una fecha: saldo del banco menos lo que el banco tiene y la contabilidad no (líneas pendientes)
/// debe ser igual al saldo contable menos lo que la contabilidad tiene y el banco no (apuntes pendientes).
/// </summary>
public sealed record EstadoConciliacionDto(
    long CuentaBancariaId, string Cuenta, string CuentaContable, DateOnly? Desde, DateOnly Hasta, decimal SaldoContable, decimal? SaldoBanco,
    decimal LineasPendientes, decimal ApuntesPendientes, decimal? Diferencia, IReadOnlyList<LineaExtractoDto> Lineas,
    IReadOnlyList<ApunteBancoDto> Apuntes, IReadOnlyList<ConciliacionDto> Recientes, IReadOnlyList<string> Avisos);

public sealed class ConciliarCmd
{
    public long CuentaBancariaId { get; set; }
    public List<long> LineaIds { get; set; } = [];
    public List<long> ApunteIds { get; set; } = [];
}

public sealed class ConciliacionAutomaticaCmd
{
    public long CuentaBancariaId { get; set; }
    /// <summary>Diferencia máxima de días entre la línea del banco y el apunte (por defecto 5).</summary>
    public int DiasMargen { get; set; } = 5;
}

public sealed record ConciliacionAutomaticaDto(int Conciliadas, int LineasPendientes, int ApuntesPendientes);

public sealed class AsientoDesdeLineaCmd
{
    /// <summary>Contrapartida: comisiones (626), intereses (662/769), un cliente o proveedor, otra cuenta bancaria…</summary>
    public long CuentaId { get; set; }
    public long CanalId { get; set; }
    public long? TerceroId { get; set; }
    public string? Concepto { get; set; }
    public long? DiarioId { get; set; }
}

public sealed class LiquidarDesdeLineaCmd
{
    public long VencimientoId { get; set; }
    public long? CanalId { get; set; }
}

public sealed record ConciliacionHechaDto(long ConciliacionId, long? AsientoId, int? AsientoNumero);

public sealed class ReglaConciliacionDto
{
    public long Id { get; set; }
    public long? CuentaBancariaId { get; set; }
    public string Texto { get; set; } = "";
    public string Signo { get; set; } = "";
    public long CuentaId { get; set; }
    public string Cuenta { get; set; } = "";
    public string CuentaNombre { get; set; } = "";
    public long CanalId { get; set; }
    public string? Concepto { get; set; }
    public int Orden { get; set; }
}

public sealed class CrearReglaConciliacionCmd
{
    /// <summary>Texto que debe aparecer en el concepto de la línea (sin distinguir mayúsculas ni acentos).</summary>
    public string Texto { get; set; } = "";
    /// <summary>CARGO (salidas), ABONO (entradas) o AMBOS.</summary>
    public string Signo { get; set; } = "CARGO";
    public long CuentaId { get; set; }
    public long CanalId { get; set; }
    public long? CuentaBancariaId { get; set; }
    /// <summary>Concepto del asiento; por defecto, el de la línea.</summary>
    public string? Concepto { get; set; }
    public int Orden { get; set; } = 100;
}

public sealed class AplicarReglasCmd
{
    public long CuentaBancariaId { get; set; }
    /// <summary>Sin contabilizar: solo dice qué regla se aplicaría a cada línea.</summary>
    public bool Simular { get; set; }
}

public sealed record LineaReglaDto(long LineaId, DateOnly Fecha, decimal Importe, string? Concepto, long ReglaId, string Regla, string Cuenta, int? AsientoNumero);

public sealed record ReglasAplicadasDto(int Aplicadas, IReadOnlyList<LineaReglaDto> Lineas, IReadOnlyList<string> Avisos);
