namespace Contabilidad.Domain.Facturacion;

/// <summary>Cálculo de cuotas IVA/IGIC, recargo, deducibilidad (prorrata) y retención IRPF.</summary>
public static class FacturaCalculadora
{
    private static decimal R(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    public static FacturaCalculada Calcular(
        LibroFactura libro,
        IReadOnlyList<LineaIvaEntrada> entradas,
        decimal? prorrataPct,
        RetencionEntrada? retencion)
    {
        if (entradas.Count == 0)
            throw new ReglaContableException("La factura necesita al menos una línea de IVA/IGIC.");

        var lineas = new List<LineaIvaCalculada>();
        foreach (var e in entradas)
        {
            var cuota = R(e.Base * e.Porcentaje / 100m);
            var recargo = R(e.Base * e.PorcentajeRecargo / 100m);
            decimal pctDed = 0m, deducible = 0m;

            if (libro == LibroFactura.Recibidas)
            {
                pctDed = e.ReglaDeduccion switch
                {
                    "TOTAL" => 100m,
                    "NINGUNA" => 0m,
                    "PRORRATA" => prorrataPct ?? throw new ReglaContableException("Falta el % de prorrata."),
                    "PORCENTAJE_AFECTACION" => (e.PorcentajeDeduccion is { } p && p >= 0m && p <= 100m)
                        ? p
                        : throw new ReglaContableException("El % de afectación debe estar entre 0 y 100."),
                    _ => throw new ReglaContableException($"Regla de deducción no válida: {e.ReglaDeduccion}")
                };
                deducible = R(cuota * pctDed / 100m);
            }

            lineas.Add(new LineaIvaCalculada(
                e.TipoIvaId, e.VigenciaId, e.TipoOperacion, e.Base, e.Porcentaje, cuota,
                e.PorcentajeRecargo, recargo, e.ReglaDeduccion, pctDed, deducible, e.EsBienInversion));
        }

        var baseTotal = lineas.Sum(l => l.Base);
        var cuotaTotal = lineas.Sum(l => l.Cuota);
        var deducibleTotal = lineas.Sum(l => l.CuotaDeducible);
        var recargoTotal = lineas.Sum(l => l.CuotaRecargo);

        RetencionCalculada? ret = retencion is null
            ? null
            : new RetencionCalculada(retencion.TipoRetencionId, retencion.VigenciaId, baseTotal,
                retencion.Porcentaje, R(baseTotal * retencion.Porcentaje / 100m));
        var importeRet = ret?.Importe ?? 0m;

        var noDeducible = libro == LibroFactura.Recibidas ? cuotaTotal - deducibleTotal : 0m;
        var totales = new TotalesFactura(
            baseTotal, cuotaTotal, deducibleTotal, noDeducible, recargoTotal, importeRet,
            baseTotal + cuotaTotal + recargoTotal - importeRet);

        return new FacturaCalculada(lineas, ret, totales);
    }
}
