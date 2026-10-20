namespace Contabilidad.Domain.Fiscal;

/// <summary>Línea interior de una factura en criterio de caja (importes totales de la factura).</summary>
public sealed record LineaCaja(long TipoIvaId, decimal Porcentaje, decimal PorcentajeRecargo, bool EsBienInversion,
    decimal Base, decimal Cuota, decimal CuotaRecargo, decimal CuotaDeducible);

public sealed record FacturaCaja(long Id, string Libro, bool Rectificativa, DateOnly FechaOperacion, DateOnly FechaContable, decimal Total,
    long? CanalId, IReadOnlyList<LineaCaja> Lineas);

/// <summary>Cobro o pago de una factura (negativo si es una devolución del recibo).</summary>
public sealed record CobroCaja(long FacturaId, DateOnly Fecha, decimal Importe);

/// <summary>Lo devengado (o deducible) de una factura entre dos fechas.</summary>
public sealed record DevengoCaja(FacturaCaja Factura, IReadOnlyList<LineaCaja> Lineas)
{
    public decimal Base => Lineas.Sum(l => l.Base);
    /// <summary>Repercutida + recargo en emitidas; deducible en recibidas.</summary>
    public decimal Cuota => Factura.Libro == "EMITIDAS" ? Lineas.Sum(l => l.Cuota + l.CuotaRecargo) : Lineas.Sum(l => l.CuotaDeducible);
}

/// <summary>
/// Régimen especial del criterio de caja (arts. 163 decies a sexiesdecies LIVA; el IGIC tiene uno equivalente): el impuesto de las
/// operaciones interiores se devenga al cobrar (emitidas) y se deduce al pagar (recibidas), en proporción a lo cobrado o pagado
/// sobre el total de la factura, y como tarde el 31 de diciembre del año siguiente al de la operación.
/// Para no arrastrar redondeos se calcula lo acumulado a cada fecha y el periodo es la diferencia.
/// Las rectificativas se devengan en su fecha contable.
/// </summary>
public static class CriterioCaja
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static DateOnly FechaLimite(FacturaCaja f) => new(f.FechaOperacion.Year + 1, 12, 31);

    /// <summary>Parte de la factura (0 a 1) devengada hasta la fecha, incluida.</summary>
    public static decimal Proporcion(FacturaCaja f, IEnumerable<CobroCaja> cobros, DateOnly hasta)
    {
        if (hasta < f.FechaContable && hasta < f.FechaOperacion) return 0;
        if (hasta >= FechaLimite(f)) return 1;
        if (f.Rectificativa || f.Total <= 0) return hasta >= f.FechaContable ? 1 : 0;
        var cobrado = cobros.Where(c => c.FacturaId == f.Id && c.Fecha <= hasta).Sum(c => c.Importe);
        return Math.Clamp(cobrado / f.Total, 0m, 1m);
    }

    public static DevengoCaja Devengado(FacturaCaja f, IReadOnlyList<CobroCaja> cobros, DateOnly desde, DateOnly hasta)
    {
        var p1 = Proporcion(f, cobros, hasta);
        var p0 = Proporcion(f, cobros, desde.AddDays(-1));
        decimal D(decimal v) => R(v * p1) - R(v * p0);
        var lineas = f.Lineas.Select(l => l with
            {
                Base = D(l.Base), Cuota = D(l.Cuota), CuotaRecargo = D(l.CuotaRecargo), CuotaDeducible = D(l.CuotaDeducible)
            })
            .Where(l => l.Base != 0 || l.Cuota != 0 || l.CuotaRecargo != 0 || l.CuotaDeducible != 0).ToList();
        return new DevengoCaja(f, lineas);
    }

    public static IReadOnlyList<DevengoCaja> DelPeriodo(IEnumerable<FacturaCaja> facturas, IReadOnlyList<CobroCaja> cobros, PeriodoFiscal p)
        => facturas.Select(f => Devengado(f, cobros, p.Inicio, p.Fin)).Where(d => d.Lineas.Count > 0).ToList();

    /// <summary>Lo devengado como grupos para el 303/420 (sin rectificativas aparte: ya están en su signo).</summary>
    public static IReadOnlyList<GrupoIva> Grupos(IEnumerable<DevengoCaja> devengos)
        => devengos.SelectMany(d => d.Lineas.Select(l => (d.Factura, l)))
            .GroupBy(x => (x.Factura.Libro, x.Factura.Rectificativa, x.l.Porcentaje, x.l.PorcentajeRecargo, x.l.EsBienInversion))
            .Select(g => new GrupoIva(g.Key.Libro, g.Key.Rectificativa, "INTERIOR", g.Key.Porcentaje, g.Key.PorcentajeRecargo, g.Key.EsBienInversion,
                g.Sum(x => x.l.Base), g.Sum(x => x.l.Cuota), g.Sum(x => x.l.CuotaRecargo), g.Sum(x => x.l.CuotaDeducible)))
            .ToList();
}
