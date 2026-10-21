using Contabilidad.Domain.Cierre;

namespace Contabilidad.Domain.Fiscal;

/// <summary>Cuentas del Impuesto sobre Sociedades (PGC).</summary>
public sealed record CuentasIs(long ImpuestoCorriente, long ImpuestoDiferido, long PagosACuenta, long HpAcreedora, long HpDeudora,
    long ActivoDiferido, long PasivoDiferido);

/// <summary>
/// Asientos del Impuesto sobre Sociedades:
/// <list type="bullet">
/// <item>Pago fraccionado (202): 473 (retenciones y pagos a cuenta) contra 4752 (Hacienda acreedora por el impuesto), que se salda al pagar.</item>
/// <item>Liquidación anual (200): gasto por impuesto corriente (6300) por la cuota líquida contra 473 por las retenciones y los pagos
/// fraccionados; la cuota diferencial va a 4752 (a ingresar) o a 4709 (a devolver).</item>
/// <item>Impuesto diferido de las diferencias de amortización: lleva los activos por diferencias temporarias deducibles (4740) y los
/// pasivos por diferencias temporarias imponibles (479) al importe que corresponde a cierre, contra 6301.</item>
/// </list>
/// </summary>
public static class GeneradorAsientoSociedades
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static IReadOnlyList<ApunteGenerado> PagoFraccionado(decimal importe, CuentasIs c, long canal)
    {
        if (importe <= 0) throw new ReglaContableException("El pago fraccionado no tiene importe a ingresar: no hay asiento.");
        return [new(c.PagosACuenta, canal, R(importe), 0), new(c.HpAcreedora, canal, 0, R(importe))];
    }

    /// <param name="activoObjetivo">Activo por diferencias temporarias deducibles que corresponde a cierre (diferencia acumulada × tipo).</param>
    /// <param name="saldoActivo">Saldo deudor actual de la 4740.</param>
    /// <param name="pasivoObjetivo">Pasivo por diferencias temporarias imponibles que corresponde a cierre.</param>
    /// <param name="saldoPasivo">Saldo acreedor actual de la 479.</param>
    public static IReadOnlyList<ApunteGenerado> Liquidacion(decimal cuotaLiquida, decimal retenciones, decimal pagosFraccionados,
        decimal activoObjetivo, decimal saldoActivo, decimal pasivoObjetivo, decimal saldoPasivo, CuentasIs c, long canal)
    {
        if (cuotaLiquida < 0 || retenciones < 0 || pagosFraccionados < 0) throw new ReglaContableException("Importes del impuesto no válidos.");
        var a = new List<ApunteGenerado>();
        void D(long cuenta, decimal v) { if (R(v) != 0) a.Add(new(cuenta, canal, R(v), 0)); }
        void H(long cuenta, decimal v) { if (R(v) != 0) a.Add(new(cuenta, canal, 0, R(v))); }

        // Impuesto corriente
        D(c.ImpuestoCorriente, cuotaLiquida);
        H(c.PagosACuenta, retenciones + pagosFraccionados);
        var diferencial = cuotaLiquida - retenciones - pagosFraccionados;
        if (diferencial > 0) H(c.HpAcreedora, diferencial); else D(c.HpDeudora, -diferencial);

        // Impuesto diferido: el 6301 recoge el neto (gasto si aumenta el pasivo o baja el activo; ingreso al revés)
        var dActivo = R(activoObjetivo - saldoActivo);
        var dPasivo = R(pasivoObjetivo - saldoPasivo);
        if (dActivo > 0) D(c.ActivoDiferido, dActivo); else H(c.ActivoDiferido, -dActivo);
        if (dPasivo > 0) H(c.PasivoDiferido, dPasivo); else D(c.PasivoDiferido, -dPasivo);
        var gastoDiferido = dPasivo - dActivo;
        if (gastoDiferido > 0) D(c.ImpuestoDiferido, gastoDiferido); else H(c.ImpuestoDiferido, -gastoDiferido);
        if (a.Count == 0) throw new ReglaContableException("No hay cuota ni impuesto diferido que contabilizar.");
        return a;
    }
}
