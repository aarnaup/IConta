namespace Contabilidad.Domain.Cierre;

/// <summary>Saldo de una cuenta en un canal base. <see cref="Saldo"/> = debe − haber (positivo: deudor).</summary>
public sealed record SaldoCuenta(long CuentaId, string Codigo, long CanalBaseId, decimal Saldo);

/// <summary>Apunte calculado, listo para pasarlo a un asiento. Va siempre a un canal base.</summary>
public sealed record ApunteGenerado(long CuentaId, long CanalId, decimal Debe, decimal Haber);

/// <summary>Resultado de la regularización en un canal base: positivo = beneficio, negativo = pérdida.</summary>
public sealed record ResultadoCanal(long CanalBaseId, decimal Ingresos, decimal Gastos)
{
    public decimal Resultado => Ingresos - Gastos;
}

/// <summary>
/// Asientos de fin de ejercicio. Cada canal base es una contabilidad completa (los asientos deben cuadrar en cada uno),
/// así que todo se calcula canal a canal y cada apunte va al canal base de su saldo.
/// </summary>
public static class GeneradorCierre
{
    /// <summary>Cuentas de gastos (6) e ingresos (7): se saldan contra la 129 en la regularización.</summary>
    public static bool EsDeResultados(string codigo) => codigo.Length > 0 && codigo[0] is '6' or '7';

    /// <summary>Cuentas de gastos e ingresos imputados al patrimonio neto (8 y 9, solo PGC normal).</summary>
    public static bool EsDePatrimonioNetoImputado(string codigo) => codigo.Length > 0 && codigo[0] is '8' or '9';

    /// <summary>Apunte que deja a cero un saldo: un saldo deudor se abona y uno acreedor se carga.</summary>
    public static ApunteGenerado Saldar(SaldoCuenta s)
        => s.Saldo > 0 ? new(s.CuentaId, s.CanalBaseId, 0, s.Saldo) : new(s.CuentaId, s.CanalBaseId, -s.Saldo, 0);

    /// <summary>Apunte que vuelve a abrir un saldo con su mismo signo.</summary>
    public static ApunteGenerado Reabrir(SaldoCuenta s)
        => s.Saldo > 0 ? new(s.CuentaId, s.CanalBaseId, s.Saldo, 0) : new(s.CuentaId, s.CanalBaseId, 0, -s.Saldo);

    /// <summary>
    /// Regularización: salda las cuentas 6 y 7 contra la cuenta de resultado (129) en cada canal.
    /// Beneficio: la 129 queda con saldo acreedor; pérdida, deudor.
    /// </summary>
    public static (IReadOnlyList<ApunteGenerado> Apuntes, IReadOnlyList<ResultadoCanal> Resultados) Regularizacion(
        IEnumerable<SaldoCuenta> saldos, long cuentaResultadoId)
    {
        var apuntes = new List<ApunteGenerado>();
        var resultados = new List<ResultadoCanal>();
        foreach (var canal in saldos.Where(s => s.Saldo != 0 && EsDeResultados(s.Codigo))
                                    .GroupBy(s => s.CanalBaseId).OrderBy(g => g.Key))
        {
            var lista = canal.OrderBy(s => s.Codigo, StringComparer.Ordinal).ToList();
            apuntes.AddRange(lista.Select(Saldar));

            var gastos = lista.Where(s => s.Codigo[0] == '6').Sum(s => s.Saldo);
            var ingresos = -lista.Where(s => s.Codigo[0] == '7').Sum(s => s.Saldo);
            var r = new ResultadoCanal(canal.Key, ingresos, gastos);
            resultados.Add(r);

            if (r.Resultado > 0) apuntes.Add(new(cuentaResultadoId, canal.Key, 0, r.Resultado));
            else if (r.Resultado < 0) apuntes.Add(new(cuentaResultadoId, canal.Key, -r.Resultado, 0));
        }
        return (apuntes, resultados);
    }

    /// <summary>Asiento de cierre: salda todas las cuentas con saldo, canal a canal.</summary>
    public static IReadOnlyList<ApunteGenerado> Cierre(IEnumerable<SaldoCuenta> saldos)
        => Ordenar(saldos).Select(Saldar).ToList();

    /// <summary>Asiento de apertura del ejercicio siguiente: reabre los mismos saldos que se cerraron.</summary>
    public static IReadOnlyList<ApunteGenerado> Apertura(IEnumerable<SaldoCuenta> saldos)
        => Ordenar(saldos).Select(Reabrir).ToList();

    private static IEnumerable<SaldoCuenta> Ordenar(IEnumerable<SaldoCuenta> saldos)
        => saldos.Where(s => s.Saldo != 0).OrderBy(s => s.CanalBaseId).ThenBy(s => s.Codigo, StringComparer.Ordinal);
}
