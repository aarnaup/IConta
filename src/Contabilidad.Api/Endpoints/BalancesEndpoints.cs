using Contabilidad.Api.Seguridad;
using Contabilidad.Domain.Balances;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api.Endpoints;

/// <summary>
/// Balances del ejercicio elegido, por canal base (por defecto el primero): sumas y saldos, balance de situación y
/// pérdidas y ganancias en formato PYMES/abreviado. Solo lectura: módulo Balances o Contabilidad.
/// </summary>
public static class BalancesEndpoints
{
    public static void MapBalances(this IEndpointRouteBuilder app)
    {
        var b = app.MapGroup("/api/empresas/{empresaId:long}/ejercicios/{ejercicioId:long}/balances").RequireAuthorization();

        b.MapGet("/sumas-saldos", async (long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta, int? nivel,
            string? incluir, IBalancesRepositorio r, CancellationToken ct)
            => Results.Ok(await r.SumasSaldosAsync(empresaId, ejercicioId, canalId, desde, hasta, nivel ?? SumasSaldos.Detalle,
                                                  string.IsNullOrWhiteSpace(incluir) ? "MOVIMIENTOS" : incluir, ct)))
            .Requiere(Nivel.Lectura, Modulo.Balances, Modulo.Contabilidad);

        b.MapGet("/situacion", async (long empresaId, long ejercicioId, long? canalId, DateOnly? hasta, IBalancesRepositorio r, CancellationToken ct)
            => Results.Ok(await r.BalanceSituacionAsync(empresaId, ejercicioId, canalId, hasta, ct)))
            .Requiere(Nivel.Lectura, Modulo.Balances, Modulo.Contabilidad);

        b.MapGet("/perdidas-ganancias", async (long empresaId, long ejercicioId, long? canalId, DateOnly? desde, DateOnly? hasta,
            IBalancesRepositorio r, CancellationToken ct)
            => Results.Ok(await r.PerdidasGananciasAsync(empresaId, ejercicioId, canalId, desde, hasta, ct)))
            .Requiere(Nivel.Lectura, Modulo.Balances, Modulo.Contabilidad);
    }
}
