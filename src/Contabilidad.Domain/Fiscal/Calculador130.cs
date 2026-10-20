namespace Contabilidad.Domain.Fiscal;

public sealed record Entrada130(
    decimal Ingresos, decimal Gastos, decimal Retenciones, decimal PagosAnteriores);

/// <summary>
/// Modelo 130: pago fraccionado del IRPF en estimación directa. Acumulado desde el 1 de enero hasta el fin del trimestre:
/// 20 % del rendimiento neto, menos pagos de trimestres anteriores y retenciones soportadas.
/// </summary>
public static class Calculador130
{
    /// <summary>Cuentas que se consideran ingresos y gastos de la actividad (prefijos del PGC). Ajustables según el plan de cada empresa.</summary>
    public static readonly string[] PrefijosIngresos = ["70", "75"];
    public static readonly string[] PrefijosGastos = ["60", "62", "631", "64", "65", "66", "68"];

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static void Calcular(Entrada130 e, DeclaracionCalculada d)
    {
        var rendimiento = R(e.Ingresos - e.Gastos);
        var veinte = rendimiento > 0 ? R(rendimiento * 0.20m) : 0m;
        var resultado = R(veinte - e.PagosAnteriores - e.Retenciones);

        d.Casillas.AddRange([
            new("01", "Ingresos computables (acumulado del año)", R(e.Ingresos)),
            new("02", "Gastos fiscalmente deducibles (acumulado del año)", R(e.Gastos)),
            new("03", "Rendimiento neto (01 − 02)", rendimiento),
            new("04", "20 % del rendimiento neto (si es positivo)", veinte),
            new("05", "Pagos fraccionados de trimestres anteriores", R(e.PagosAnteriores)),
            new("06", "Retenciones e ingresos a cuenta soportados (acumulado)", R(e.Retenciones)),
            new("07", "Pago fraccionado previo (04 − 05 − 06)", resultado),
            new("12", "Suma de pagos fraccionados previos del trimestre (07 + 11)", resultado),
            new("13", "A deducir: minoración por rendimientos netos bajos (art. 110.3.c RIRPF)", 0m),
            new("14", "Diferencia (12 − 13)", resultado),
            new("15", "A deducir: resultados negativos de trimestres anteriores", 0m),
            new("16", "A deducir: pagos por préstamos para la vivienda habitual", 0m),
            new("17", "Total (14 − 15 − 16)", resultado),
            new("18", "A deducir: resultado a ingresar de la autoliquidación anterior (solo complementarias)", 0m),
            new("19", "Resultado de la autoliquidación (17 − 18)", resultado),
        ]);
        d.Resultado = resultado;
        d.AIngresar = Math.Max(resultado, 0m);
        if (resultado < 0)
            d.Avisos.Add("El resultado es negativo: no hay nada que ingresar este trimestre (el exceso de retenciones se regulariza en la renta anual).");
        d.Avisos.Add("No se aplican automáticamente: la deducción por rendimientos netos bajos, la provisión del 5 % de gastos de difícil justificación (estimación simplificada) ni las minoraciones por vivienda habitual.");
    }
}
