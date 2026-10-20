using Contabilidad.Domain.Balances;

namespace Contabilidad.Domain;

public sealed record CanalRefDto(long Id, string Codigo);

public sealed record SumasSaldosDto(
    CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, DateOnly Desde, DateOnly Hasta, int Nivel, string Incluir,
    IReadOnlyList<FilaSumasSaldos> Filas, decimal TotalDebe, decimal TotalHaber, decimal TotalSaldoDeudor, decimal TotalSaldoAcreedor,
    bool Cuadra);

public sealed record PartidaDto(string Id, string Texto, int Nivel, bool EsTotal, decimal Importe, decimal? ImporteAnterior,
    IReadOnlyList<CuentaEnPartida> Cuentas);

/// <summary>Balance de situación o cuenta de pérdidas y ganancias, con la columna del ejercicio anterior si existe.</summary>
public sealed record EstadoFinancieroDto(
    string Modelo, string Titulo, CanalRefDto Canal, IReadOnlyList<CanalRefDto> Canales, DateOnly Desde, DateOnly Hasta,
    string Ejercicio, string? EjercicioAnterior, IReadOnlyList<PartidaDto> Partidas, IReadOnlyList<SaldoBalance> SinClasificar,
    IReadOnlyList<string> Avisos);
