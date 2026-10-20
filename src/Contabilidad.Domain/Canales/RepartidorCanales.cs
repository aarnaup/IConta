namespace Contabilidad.Domain.Canales;

/// <summary>
/// Reparte el importe de un apunte entre canales base.
///  - Canal base (1,2,3): 100% a sí mismo.
///  - Canal derivado: aplica las líneas de la versión vigente (A=123 => 100% a cada canal;
///    E => porcentaje / fórmula / resto).
/// </summary>
public sealed class RepartidorCanales
{
    public IReadOnlyList<ImporteCanal> Repartir(ConfiguracionCanal canal, decimal debe, decimal haber)
    {
        if (debe < 0 || haber < 0)
            throw new ReglaContableException("Los importes no pueden ser negativos.");
        if (debe != 0 && haber != 0)
            throw new ReglaContableException("Un apunte no puede tener Debe y Haber a la vez.");

        if (canal.Tipo == TipoCanal.Base)
            return [new ImporteCanal(canal.CanalId, debe, haber, null)];

        if (canal.Lineas.Count == 0)
            throw new ReglaContableException($"El canal {canal.Codigo} no tiene fórmula vigente para esa fecha.");

        var esDebe = debe != 0;
        var importe = esDebe ? debe : haber;
        var variables = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["IMPORTE"] = importe, ["DEBE"] = debe, ["HABER"] = haber
        };

        var porCanal = new Dictionary<long, decimal>();
        LineaCanal? resto = null;
        decimal acumulado = 0;

        foreach (var l in canal.Lineas.OrderBy(x => x.Orden))
        {
            if (l.Modo == ModoReparto.Resto)
            {
                if (resto is not null)
                    throw new ReglaContableException($"El canal {canal.Codigo} tiene más de una línea 'resto'.");
                resto = l;
                continue;
            }

            var valor = l.Modo == ModoReparto.Porcentaje
                ? importe * (l.Porcentaje ?? 0m) / 100m
                : ExpresionEvaluador.Evaluar(l.Expresion ?? "", variables);

            valor = Math.Round(valor, l.Decimales, MidpointRounding.AwayFromZero);
            porCanal[l.CanalBaseId] = porCanal.GetValueOrDefault(l.CanalBaseId) + valor;
            acumulado += valor;
        }

        // El "resto" absorbe el redondeo para que el reparto cuadre exactamente
        if (resto is not null)
            porCanal[resto.CanalBaseId] = porCanal.GetValueOrDefault(resto.CanalBaseId) + (importe - acumulado);

        return porCanal
            .Select(kv => new ImporteCanal(kv.Key, esDebe ? kv.Value : 0m, esDebe ? 0m : kv.Value, canal.VersionId))
            .ToList();
    }
}
