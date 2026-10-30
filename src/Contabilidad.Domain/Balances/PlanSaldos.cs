namespace Contabilidad.Domain.Balances;

/// <summary>Cuenta del plan de la empresa (grupo, subgrupo, cuenta o subcuenta).</summary>
public sealed record CuentaPlan(string Codigo, string Nombre, bool EsSubcuenta);

/// <summary>
/// Cuenta con sus acumulados: saldo anterior (deudor en positivo), sumas del debe y del haber del periodo y saldo final.
/// En los grupos, subgrupos y cuentas, la suma de sus subcuentas.
/// </summary>
public sealed record FilaPlanSaldos(string Codigo, string Nombre, int Nivel, bool EsSubcuenta, decimal SaldoAnterior, decimal Debe, decimal Haber)
{
    public decimal SaldoFinal => SaldoAnterior + Debe - Haber;
}

public static class PlanSaldos
{
    /// <summary>
    /// Plan de cuentas ordenado jerárquicamente (1, 10, 100, 10000000, 101…) con los acumulados de cada cuenta.
    /// <paramref name="anterior"/> son los movimientos que forman el saldo anterior (los previos al periodo y la apertura);
    /// <paramref name="periodo"/>, los del periodo. Con <paramref name="soloConSaldo"/> se quitan las cuentas sin saldo ni movimientos.
    /// </summary>
    public static IReadOnlyList<FilaPlanSaldos> Construir(IEnumerable<CuentaPlan> plan, IEnumerable<MovimientoCuenta> anterior,
        IEnumerable<MovimientoCuenta> periodo, bool soloConSaldo)
    {
        var ant = anterior.GroupBy(m => m.Codigo).ToDictionary(g => g.Key, g => g.Sum(m => m.Debe - m.Haber));
        var per = periodo.GroupBy(m => m.Codigo).ToDictionary(g => g.Key, g => (Debe: g.Sum(m => m.Debe), Haber: g.Sum(m => m.Haber)));

        // Cuentas del plan y, por si acaso, las que tengan movimientos sin estar en él
        var cuentas = plan.GroupBy(c => c.Codigo).Select(g => g.First()).ToDictionary(c => c.Codigo);
        foreach (var codigo in ant.Keys.Concat(per.Keys).Where(k => !cuentas.ContainsKey(k)))
            cuentas[codigo] = new CuentaPlan(codigo, "", true);
        var conMovs = ant.Keys.Union(per.Keys).ToList();

        var filas = new List<FilaPlanSaldos>();
        foreach (var c in cuentas.Values.OrderBy(c => c.Codigo, StringComparer.Ordinal))
        {
            var codigos = c.EsSubcuenta ? [c.Codigo] : conMovs.Where(k => k.Length > c.Codigo.Length && k.StartsWith(c.Codigo, StringComparison.Ordinal));
            decimal sa = 0, d = 0, h = 0;
            foreach (var k in codigos)
            {
                sa += ant.GetValueOrDefault(k);
                if (per.TryGetValue(k, out var p)) { d += p.Debe; h += p.Haber; }
            }
            if (soloConSaldo && sa == 0 && d == 0 && h == 0) continue;
            filas.Add(new FilaPlanSaldos(c.Codigo, c.Nombre, c.EsSubcuenta ? 5 : Math.Min(c.Codigo.Length, 4), c.EsSubcuenta, sa, d, h));
        }
        return filas;
    }
}
