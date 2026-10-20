using Contabilidad.Domain.Cierre;

namespace Contabilidad.Domain.Nominas;

/// <summary>Importes de una nómina (mensual o de una paga extra).</summary>
/// <param name="Bruto">Retribución dineraria íntegra (devengos).</param>
/// <param name="Especie">Valoración de las retribuciones en especie (no se pagan en dinero; el gasto se contabiliza al adquirir el bien).</param>
/// <param name="IngresoACuenta">Ingreso a cuenta del IRPF sobre la especie.</param>
/// <param name="IngresoRepercutido">El ingreso a cuenta se descuenta al trabajador; si no, lo asume la empresa como gasto (649).</param>
/// <param name="Retencion">Retención del IRPF sobre la parte dineraria.</param>
/// <param name="SsTrabajador">Cotizaciones a la Seguridad Social a cargo del trabajador (gasto deducible en el 190).</param>
/// <param name="SsEmpresa">Cotizaciones a cargo de la empresa.</param>
/// <param name="Anticipos">Anticipos ya pagados que se descuentan (460).</param>
public sealed record ImportesNomina(decimal Bruto, decimal Especie, decimal IngresoACuenta, bool IngresoRepercutido, decimal Retencion,
    decimal SsTrabajador, decimal SsEmpresa, decimal Anticipos)
{
    /// <summary>Líquido a percibir: bruto − retención − Seguridad Social del trabajador − anticipos − ingreso a cuenta repercutido.</summary>
    public decimal Liquido => Bruto - Retencion - SsTrabajador - Anticipos - (IngresoRepercutido ? IngresoACuenta : 0);

    /// <summary>% de retención efectivo sobre la parte dineraria.</summary>
    public decimal PorcentajeRetencion => Bruto == 0 ? 0 : Math.Round(Retencion * 100m / Bruto, 2, MidpointRounding.AwayFromZero);

    public void Validar()
    {
        if (new[] { Bruto, Especie, IngresoACuenta, Retencion, SsTrabajador, SsEmpresa, Anticipos }.Any(x => x < 0))
            throw new ReglaContableException("Los importes de la nómina no pueden ser negativos.");
        if (Bruto == 0 && Especie == 0) throw new ReglaContableException("La nómina no tiene retribuciones.");
        if (Especie == 0 && IngresoACuenta != 0) throw new ReglaContableException("El ingreso a cuenta es de las retribuciones en especie.");
        if (Liquido < 0) throw new ReglaContableException("Los descuentos superan el bruto: el líquido sale negativo.");
        if (Retencion > Bruto) throw new ReglaContableException("La retención no puede superar el bruto dinerario.");
    }
}

/// <summary>Cuentas del asiento de la nómina.</summary>
public sealed record CuentasNomina(long Sueldos, long SsEmpresa, long OtrosGastosSociales, long Retenciones, long Organismos,
    long Anticipos, long Remuneraciones);

/// <summary>
/// Asiento de la nómina: cargo a sueldos y salarios (640) por el bruto, a Seguridad Social a cargo de la empresa (642) y, si la
/// empresa asume el ingreso a cuenta de la especie, a otros gastos sociales (649); abono a Hacienda acreedora por retenciones (4751:
/// retención e ingreso a cuenta), a organismos de la Seguridad Social (476: cuotas del trabajador y de la empresa), a anticipos (460)
/// y a remuneraciones pendientes de pago (465) por el líquido.
/// </summary>
public static class GeneradorAsientoNomina
{
    public static IReadOnlyList<ApunteGenerado> Generar(ImportesNomina n, CuentasNomina c, long canalId)
    {
        n.Validar();
        var a = new List<ApunteGenerado>();
        void D(long cuenta, decimal v) { if (v != 0) a.Add(new(cuenta, canalId, v, 0)); }
        void H(long cuenta, decimal v) { if (v != 0) a.Add(new(cuenta, canalId, 0, v)); }
        D(c.Sueldos, n.Bruto);
        D(c.SsEmpresa, n.SsEmpresa);
        if (!n.IngresoRepercutido) D(c.OtrosGastosSociales, n.IngresoACuenta);
        H(c.Retenciones, n.Retencion + n.IngresoACuenta);
        H(c.Organismos, n.SsTrabajador + n.SsEmpresa);
        H(c.Anticipos, n.Anticipos);
        H(c.Remuneraciones, n.Liquido);
        return a;
    }
}
