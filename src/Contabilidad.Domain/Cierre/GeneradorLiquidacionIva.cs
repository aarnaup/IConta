namespace Contabilidad.Domain.Cierre;

/// <summary>Cifras de la liquidación en un canal base.</summary>
/// <param name="Repercutido">Saldo acreedor de las cuentas de IVA/IGIC repercutido (477).</param>
/// <param name="Soportado">Saldo deudor de las cuentas de IVA/IGIC soportado (472).</param>
/// <param name="Compensado">Parte del resultado que se cubre con cuotas a compensar de periodos anteriores (4700).</param>
public sealed record LiquidacionCanal(long CanalBaseId, decimal Repercutido, decimal Soportado, decimal Compensado)
{
    /// <summary>Repercutido − soportado: positivo, a ingresar; negativo, a compensar o devolver.</summary>
    public decimal Resultado => Repercutido - Soportado;
    public decimal AIngresar => Resultado > 0 ? Resultado - Compensado : 0;
    public decimal AFavor => Resultado < 0 ? -Resultado : 0;
}

/// <param name="Soportado">Saldos de las cuentas 472 a la fecha de fin del periodo.</param>
/// <param name="Repercutido">Saldos de las cuentas 477 a esa fecha.</param>
/// <param name="SaldoDeudorPorCanal">Saldo de la cuenta de Hacienda deudora (4700) en cada canal: cuotas a compensar de antes.</param>
/// <param name="CompensacionMaxima">Compensación que aplica la declaración; 0 si no aplica ninguna.</param>
public sealed record EntradaLiquidacionIva(
    IReadOnlyList<SaldoCuenta> Soportado,
    IReadOnlyList<SaldoCuenta> Repercutido,
    IReadOnlyDictionary<long, decimal> SaldoDeudorPorCanal,
    decimal CompensacionMaxima,
    long CuentaAcreedoraId,
    long CuentaDeudoraId);

/// <summary>
/// Asiento de liquidación del IVA/IGIC de un periodo, canal a canal:
/// 477 (D) a 472 (H); la diferencia a la 4750 (a ingresar, H) o a la 4700 (a compensar o devolver, D).
/// Si la declaración aplica cuotas a compensar de periodos anteriores, esa parte sale de la 4700 (H) en lugar de la 4750.
/// </summary>
public static class GeneradorLiquidacionIva
{
    public static (IReadOnlyList<ApunteGenerado> Apuntes, IReadOnlyList<LiquidacionCanal> Canales) Generar(EntradaLiquidacionIva e)
    {
        var apuntes = new List<ApunteGenerado>();
        var canales = new List<LiquidacionCanal>();

        var porCanal = e.Repercutido.Select(s => (s, rep: true)).Concat(e.Soportado.Select(s => (s, rep: false)))
            .Where(x => x.s.Saldo != 0)
            .GroupBy(x => x.s.CanalBaseId)
            .OrderBy(g => g.Key);

        foreach (var canal in porCanal)
        {
            var rep = canal.Where(x => x.rep).Select(x => x.s).OrderBy(s => s.Codigo, StringComparer.Ordinal).ToList();
            var sop = canal.Where(x => !x.rep).Select(x => x.s).OrderBy(s => s.Codigo, StringComparer.Ordinal).ToList();
            apuntes.AddRange(rep.Select(GeneradorCierre.Saldar));
            apuntes.AddRange(sop.Select(GeneradorCierre.Saldar));

            var repercutido = -rep.Sum(s => s.Saldo);
            var soportado = sop.Sum(s => s.Saldo);
            var resultado = repercutido - soportado;

            decimal compensado = 0;
            if (resultado > 0)
            {
                var disponible = Math.Max(0, e.SaldoDeudorPorCanal.GetValueOrDefault(canal.Key));
                compensado = Math.Min(resultado, Math.Min(disponible, Math.Max(0, e.CompensacionMaxima)));
                if (compensado > 0) apuntes.Add(new(e.CuentaDeudoraId, canal.Key, 0, compensado));
                if (resultado - compensado > 0) apuntes.Add(new(e.CuentaAcreedoraId, canal.Key, 0, resultado - compensado));
            }
            else if (resultado < 0)
            {
                apuntes.Add(new(e.CuentaDeudoraId, canal.Key, -resultado, 0));
            }

            canales.Add(new LiquidacionCanal(canal.Key, repercutido, soportado, compensado));
        }
        return (apuntes, canales);
    }
}
