namespace Contabilidad.Domain;

// DTOs con propiedades mutables para que Dapper los materialice sin configuración extra.

public sealed class EmpresaDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Nif { get; set; } = "";
    public string ImpuestoIndirecto { get; set; } = "";
    public string RolEnEmpresa { get; set; } = "";
}

public sealed class EjercicioDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public DateOnly FechaInicio { get; set; }
    public DateOnly FechaFin { get; set; }
    public string Estado { get; set; } = "";
    public bool EsPartido { get; set; }
}

public sealed class CuentaDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
}

public sealed class DiarioDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
}

public sealed class CanalDto
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
}

public sealed class ApunteCmd
{
    public long CuentaId { get; set; }
    public long? TerceroId { get; set; }
    public long? ActividadId { get; set; }
    public long CanalId { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
    public string? Concepto { get; set; }
}

public sealed class CrearAsientoCmd
{
    public DateOnly Fecha { get; set; }
    public long DiarioId { get; set; }
    public string Concepto { get; set; } = "";
    public bool Borrador { get; set; }
    public List<ApunteCmd> Apuntes { get; set; } = [];
}

public sealed record AsientoCreado(long Id, int Numero, string Estado);

public sealed class SimularCanalCmd
{
    public long CanalId { get; set; }
    public DateOnly Fecha { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
}

public sealed class LineaCanalCmd
{
    public long CanalBaseId { get; set; }
    public string Modo { get; set; } = "PORCENTAJE";   // PORCENTAJE | FORMULA | RESTO
    public decimal? Porcentaje { get; set; }
    public string? Expresion { get; set; }
    public short Decimales { get; set; } = 2;
}

public sealed class NuevaVersionCanalCmd
{
    public DateOnly VigenteDesde { get; set; }
    public string? Descripcion { get; set; }
    public List<LineaCanalCmd> Lineas { get; set; } = [];
}

public sealed class FilaAsientoDto
{
    public long AsientoId { get; set; }
    public int Numero { get; set; }
    public DateOnly Fecha { get; set; }
    public string Concepto { get; set; } = "";
    public string Estado { get; set; } = "";
    public string Diario { get; set; } = "";
    public short Linea { get; set; }
    public string Cuenta { get; set; } = "";
    public string CuentaNombre { get; set; } = "";
    public string Canal { get; set; } = "";
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
}

public sealed class FilaMayorDto
{
    public DateOnly Fecha { get; set; }
    public int Numero { get; set; }
    public string? Concepto { get; set; }
    public string Canal { get; set; } = "";
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
    public decimal Saldo { get; set; }
}
