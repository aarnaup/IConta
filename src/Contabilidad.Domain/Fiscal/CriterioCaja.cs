namespace Contabilidad.Domain.Fiscal;

/// <summary>Línea interior de una factura en criterio de caja (importes totales de la factura).</summary>
public sealed record LineaCaja(long TipoIvaId, decimal Porcentaje, decimal PorcentajeRecargo, bool EsBienInversion,
    decimal Base, decimal Cuota, decimal CuotaRecargo, decimal CuotaDeducible);

/// <param name="RectificadaId">En una rectificativa, la factura que rectifica (si también está en criterio de caja).</param>
public sealed record FacturaCaja(long Id, string Libro, bool Rectificativa, DateOnly FechaOperacion, DateOnly FechaContable, decimal Total,
    long? CanalId, IReadOnlyList<LineaCaja> Lineas, long? RectificadaId = null);

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
/// Una rectificativa de una factura en criterio de caja sigue el cobro de la original: la factura y sus rectificativas se tratan como
/// una sola operación (total y líneas sumados desde la fecha de cada rectificativa) y se devenga la parte cobrada de ese total. Así, si la
/// original ya estaba cobrada, la rectificación se devenga en su fecha; si estaba pendiente, solo reduce lo que queda por devengar; y el
/// límite es el 31 de diciembre del año siguiente al de la operación original. Una rectificativa sin factura rectificada en el régimen se
/// devenga en su fecha contable.
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

    /// <summary>Parte (0 a 1) cobrada hasta la fecha de una factura y sus rectificativas, sobre el total que suman a esa fecha.</summary>
    public static decimal ProporcionGrupo(FacturaCaja original, IReadOnlyList<FacturaCaja> rectificativas, IEnumerable<CobroCaja> cobros, DateOnly hasta)
    {
        if (hasta < original.FechaContable && hasta < original.FechaOperacion) return 0;
        if (hasta >= FechaLimite(original)) return 1;
        var vigentes = rectificativas.Where(r => r.FechaContable <= hasta).ToList();
        var total = original.Total + vigentes.Sum(r => r.Total);
        if (total <= 0) return 1;
        var ids = vigentes.Select(r => r.Id).Append(original.Id).ToHashSet();
        var cobrado = cobros.Where(c => ids.Contains(c.FacturaId) && c.Fecha <= hasta).Sum(c => c.Importe);
        return Math.Clamp(cobrado / total, 0m, 1m);
    }

    /// <summary>Lo devengado entre dos fechas por una factura (o rectificativa) de un grupo factura + rectificativas.</summary>
    public static DevengoCaja DevengadoEnGrupo(FacturaCaja f, FacturaCaja original, IReadOnlyList<FacturaCaja> rectificativas,
        IReadOnlyList<CobroCaja> cobros, DateOnly desde, DateOnly hasta)
    {
        decimal P(DateOnly d) => f.Id != original.Id && d < f.FechaContable ? 0 : ProporcionGrupo(original, rectificativas, cobros, d);
        var p1 = P(hasta);
        var p0 = P(desde.AddDays(-1));
        return Prorratear(f, p0, p1);
    }

    public static DevengoCaja Devengado(FacturaCaja f, IReadOnlyList<CobroCaja> cobros, DateOnly desde, DateOnly hasta)
        => Prorratear(f, Proporcion(f, cobros, desde.AddDays(-1)), Proporcion(f, cobros, hasta));

    private static DevengoCaja Prorratear(FacturaCaja f, decimal p0, decimal p1)
    {
        decimal D(decimal v) => R(v * p1) - R(v * p0);
        var lineas = f.Lineas.Select(l => l with
            {
                Base = D(l.Base), Cuota = D(l.Cuota), CuotaRecargo = D(l.CuotaRecargo), CuotaDeducible = D(l.CuotaDeducible)
            })
            .Where(l => l.Base != 0 || l.Cuota != 0 || l.CuotaRecargo != 0 || l.CuotaDeducible != 0).ToList();
        return new DevengoCaja(f, lineas);
    }

    public static IReadOnlyList<DevengoCaja> DelPeriodo(IEnumerable<FacturaCaja> facturas, IReadOnlyList<CobroCaja> cobros, PeriodoFiscal p)
        => DelIntervalo(facturas.ToList(), cobros, p.Inicio, p.Fin);

    /// <summary>Lo devengado por cada factura entre dos fechas, agrupando cada factura con sus rectificativas.</summary>
    public static IReadOnlyList<DevengoCaja> DelIntervalo(IReadOnlyList<FacturaCaja> facturas, IReadOnlyList<CobroCaja> cobros, DateOnly desde, DateOnly hasta)
    {
        var porId = facturas.ToDictionary(f => f.Id);
        var rects = facturas.Where(f => f.Rectificativa && f.RectificadaId is { } o && porId.ContainsKey(o))
            .ToLookup(f => f.RectificadaId!.Value);
        return facturas.Select(f =>
            {
                var original = f.Rectificativa && f.RectificadaId is { } o && porId.TryGetValue(o, out var orig) ? orig : rects.Contains(f.Id) ? f : null;
                return original is null ? Devengado(f, cobros, desde, hasta)
                    : DevengadoEnGrupo(f, original, rects[original.Id].ToList(), cobros, desde, hasta);
            })
            .Where(d => d.Lineas.Count > 0).ToList();
    }

    /// <summary>Proporción devengada a una fecha de la factura, teniendo en cuenta su grupo (factura + rectificativas) si lo tiene.</summary>
    public static decimal ProporcionEn(FacturaCaja f, IReadOnlyList<FacturaCaja> facturas, IReadOnlyList<CobroCaja> cobros, DateOnly hasta)
    {
        var original = f.Rectificativa && f.RectificadaId is { } o ? facturas.FirstOrDefault(x => x.Id == o) : f;
        if (original is null) return Proporcion(f, cobros, hasta);
        var rs = facturas.Where(x => x.Rectificativa && x.RectificadaId == original.Id).ToList();
        return rs.Count == 0 && !f.Rectificativa ? Proporcion(f, cobros, hasta) : ProporcionGrupo(original, rs, cobros, hasta);
    }

    /// <summary>Lo devengado como grupos para el 303/420 (sin rectificativas aparte: ya están en su signo).</summary>
    public static IReadOnlyList<GrupoIva> Grupos(IEnumerable<DevengoCaja> devengos)
        => devengos.SelectMany(d => d.Lineas.Select(l => (d.Factura, l)))
            .GroupBy(x => (x.Factura.Libro, x.Factura.Rectificativa, x.l.Porcentaje, x.l.PorcentajeRecargo, x.l.EsBienInversion))
            .Select(g => new GrupoIva(g.Key.Libro, g.Key.Rectificativa, "INTERIOR", g.Key.Porcentaje, g.Key.PorcentajeRecargo, g.Key.EsBienInversion,
                g.Sum(x => x.l.Base), g.Sum(x => x.l.Cuota), g.Sum(x => x.l.CuotaRecargo), g.Sum(x => x.l.CuotaDeducible), CriterioCaja: true))
            .ToList();
}
