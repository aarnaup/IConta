namespace Contabilidad.Domain.Fiscal;

/// <summary>Datos de un bien de inversión para regularizar sus deducciones.</summary>
/// <param name="AnioInicio">Año de inicio de utilización: el primero del periodo de regularización.</param>
/// <param name="Anios">5, o 10 si es un inmueble.</param>
/// <param name="ProrrataInicial">Porcentaje definitivo de deducción del año de inicio (el que «prevaleció»).</param>
/// <param name="FechaTransmision">Fecha de venta dentro del periodo de regularización, si la hay.</param>
/// <param name="FechaBaja">Fecha de baja sin venta (siniestro, desguace): deja de regularizarse desde ese año.</param>
public sealed record BienInversionRegularizable(
    long Id, string Codigo, string Descripcion, int AnioInicio, int Anios, decimal CuotaSoportada, int ProrrataInicial,
    DateOnly? FechaTransmision = null, bool TransmisionExenta = false, DateOnly? FechaBaja = null)
{
    public int UltimoAnio => AnioInicio + Anios - 1;
    public decimal DeduccionInicial => RegularizacionBienesInversion.R(CuotaSoportada * ProrrataInicial / 100m);
}

/// <summary>Regularización de un año (ANUAL, art. 107-109) o por venta (TRANSMISION, art. 110). Importe positivo: más deducción.</summary>
public sealed record RegularizacionBien(long BienId, int Anio, string Tipo, int Prorrata, int AniosRegularizados, decimal Importe, string? Nota);

/// <summary>
/// Regularización de las deducciones de los bienes de inversión (arts. 107 a 110 de la Ley 37/1992 del IVA;
/// el IGIC tiene las mismas reglas en la Ley 20/1991). El año de inicio se ajusta con la prorrata definitiva como el resto
/// de cuotas; en los 4 años siguientes (9 si es un inmueble):
/// (cuota soportada × % definitivo del año − deducción del año de inicio) ÷ 5 (o 10), solo si los porcentajes difieren
/// en más de 10 puntos. Si el bien se vende dentro del periodo, se regulariza de una vez el año de la venta y los que
/// quedan, como si se hubiera usado al 100 % (venta sujeta y no exenta) o al 0 % (exenta o no sujeta).
/// </summary>
public static class RegularizacionBienesInversion
{
    public const decimal ImporteMinimo = 3005.06m;   // art. 108: por debajo no es bien de inversión a efectos del impuesto

    internal static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Regularización anual del año <paramref name="anio"/>. Devuelve null si ese año no toca (fuera del periodo,
    /// año de inicio, vendido o dado de baja). Sin <paramref name="prorrataDefinitiva"/> no se puede calcular: importe 0 con nota.
    /// </summary>
    public static RegularizacionBien? Anual(BienInversionRegularizable b, int anio, int? prorrataDefinitiva)
    {
        if (anio <= b.AnioInicio || anio > b.UltimoAnio) return null;
        if (b.FechaTransmision is { } t && t.Year <= anio) return null;      // la venta regulariza ese año y los siguientes
        if (b.FechaBaja is { } f && f.Year <= anio) return null;
        if (prorrataDefinitiva is not { } p)
            return new(b.Id, anio, "ANUAL", 0, 1, 0, $"Falta el porcentaje definitivo de prorrata de {anio}.");
        if (p == b.ProrrataInicial)
            return new(b.Id, anio, "ANUAL", p, 1, 0, "Mismo porcentaje de deducción que el año de inicio: no se regulariza.");
        if (Math.Abs(p - b.ProrrataInicial) <= 10)
            return new(b.Id, anio, "ANUAL", p, 1, 0, "Diferencia de 10 puntos o menos con el año de inicio: no se regulariza.");
        return new(b.Id, anio, "ANUAL", p, 1, R(b.CuotaSoportada * (p - b.ProrrataInicial) / 100m / b.Anios), null);
    }

    /// <summary>Regularización por la venta del bien (en la declaración del periodo de la venta); null si no procede.</summary>
    public static RegularizacionBien? Transmision(BienInversionRegularizable b)
    {
        if (b.FechaTransmision is not { } t || t.Year < b.AnioInicio || t.Year > b.UltimoAnio) return null;
        var p = b.TransmisionExenta ? 0 : 100;
        // El año de inicio ya se ajusta con la prorrata definitiva: se regularizan los siguientes
        var anios = t.Year == b.AnioInicio ? b.Anios - 1 : b.UltimoAnio - t.Year + 1;
        if (anios <= 0 || p == b.ProrrataInicial)
            return new(b.Id, t.Year, "TRANSMISION", p, anios, 0, "La venta no cambia el porcentaje de deducción.");
        return new(b.Id, t.Year, "TRANSMISION", p, anios, R(b.CuotaSoportada * (p - b.ProrrataInicial) / 100m * anios / b.Anios),
            b.TransmisionExenta ? "Venta exenta o no sujeta: se considera usado al 0 % el resto del periodo."
                                : "Venta sujeta y no exenta: se considera usado al 100 % el resto del periodo.");
    }

    /// <summary>
    /// Todo lo que toca regularizar en un periodo de declaración: las anuales del año si es el último periodo,
    /// y las ventas con fecha dentro del periodo.
    /// </summary>
    public static IReadOnlyList<RegularizacionBien> DelPeriodo(IEnumerable<BienInversionRegularizable> bienes, PeriodoFiscal periodo,
        Func<BienInversionRegularizable, int, int?> prorrataDefinitiva)
    {
        var res = new List<RegularizacionBien>();
        foreach (var b in bienes)
        {
            if (periodo.EsUltimoDelAnio && Anual(b, periodo.Anio, prorrataDefinitiva(b, periodo.Anio)) is { } a) res.Add(a);
            if (b.FechaTransmision is { } t && t >= periodo.Inicio && t <= periodo.Fin && Transmision(b) is { } v) res.Add(v);
        }
        return res;
    }
}
