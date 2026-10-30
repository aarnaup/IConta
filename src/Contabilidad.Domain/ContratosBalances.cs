using Contabilidad.Domain.Balances;

namespace Contabilidad.Domain;

public sealed record CanalRefDto(long Id, string Codigo);

public sealed record SumasSaldosDto(
    CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, DateOnly Desde, DateOnly Hasta, int Nivel, string Incluir,
    IReadOnlyList<FilaSumasSaldos> Filas, decimal TotalDebe, decimal TotalHaber, decimal TotalSaldoDeudor, decimal TotalSaldoAcreedor,
    bool Cuadra);

/// <summary>Plan de cuentas con saldo anterior, debe, haber y saldo final; los totales, de las subcuentas.</summary>
public sealed record PlanSaldosDto(
    CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, DateOnly Desde, DateOnly Hasta, string Incluir,
    IReadOnlyList<FilaPlanSaldos> Filas, decimal TotalAnterior, decimal TotalDebe, decimal TotalHaber, decimal TotalFinal);

public sealed record PartidaDto(string Id, string Texto, int Nivel, bool EsTotal, decimal Importe, decimal? ImporteAnterior,
    IReadOnlyList<CuentaEnPartida> Cuentas);

/// <summary>Balance de situación o cuenta de pérdidas y ganancias, con la columna del ejercicio anterior si existe.</summary>
public sealed record EstadoFinancieroDto(
    string Modelo, string Titulo, CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, DateOnly Desde, DateOnly Hasta,
    string Ejercicio, string? EjercicioAnterior, IReadOnlyList<PartidaDto> Partidas, IReadOnlyList<SaldoBalance> SinClasificar,
    IReadOnlyList<string> Avisos);

/// <summary>Estado de cambios en el patrimonio neto del ejercicio.</summary>
public sealed record EcpnDto(CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, string Ejercicio, DateOnly Desde, DateOnly Hasta,
    IReadOnlyList<LineaEstado> IngresosGastos, IReadOnlyList<ColumnaEcpn> Columnas, IReadOnlyList<FilaEcpn> Cambios,
    decimal PatrimonioNetoBalance, IReadOnlyList<string> Avisos);

/// <summary>Estado de flujos de efectivo del ejercicio (método indirecto).</summary>
public sealed record EfeDto(CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, string Ejercicio, DateOnly Desde, DateOnly Hasta,
    IReadOnlyList<LineaEstado> Lineas, IReadOnlyList<string> Avisos);

public sealed record TablaMemoria(string Titulo, IReadOnlyList<string> Cabecera, IReadOnlyList<IReadOnlyList<string>> Filas);
public sealed record SeccionMemoria(string Numero, string Titulo, IReadOnlyList<string> Parrafos, IReadOnlyList<TablaMemoria> Tablas);

/// <summary>Borrador de memoria con las cifras de la contabilidad: los textos son una base que hay que completar y revisar.</summary>
public sealed record MemoriaDto(string Empresa, string Nif, string Ejercicio, DateOnly Desde, DateOnly Hasta, string Formato,
    IReadOnlyList<SeccionMemoria> Secciones, IReadOnlyList<string> Avisos);
