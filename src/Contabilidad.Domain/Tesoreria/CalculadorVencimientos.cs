namespace Contabilidad.Domain.Tesoreria;

public sealed record PlazoVencimiento(short Numero, short Total, DateOnly Fecha, decimal Importe);

/// <summary>Reparte el importe de un documento en vencimientos según la forma de pago.</summary>
public static class CalculadorVencimientos
{
    public const int MaxPlazos = 60;

    /// <summary>
    /// Plazos iguales; la diferencia de redondeo se suma al ÚLTIMO plazo para que la suma sea exactamente el total.
    /// Primer vencimiento = fecha base + diasPrimerPlazo; los siguientes, + diasEntrePlazos cada uno.
    /// </summary>
    public static IReadOnlyList<PlazoVencimiento> Generar(decimal total, DateOnly fechaBase, int numPlazos, int diasPrimerPlazo, int diasEntrePlazos)
    {
        if (total <= 0) throw new ReglaContableException("Solo se generan vencimientos de importes positivos.");
        if (numPlazos is < 1 or > MaxPlazos) throw new ReglaContableException($"El número de plazos debe estar entre 1 y {MaxPlazos}.");
        if (diasPrimerPlazo < 0 || diasEntrePlazos < 0) throw new ReglaContableException("Los días de plazo no pueden ser negativos.");
        if (numPlazos > 1 && diasEntrePlazos == 0)
            throw new ReglaContableException("Con varios plazos hay que indicar los días entre plazos.");

        total = Math.Round(total, 2, MidpointRounding.AwayFromZero);
        var cuota = Math.Round(total / numPlazos, 2, MidpointRounding.AwayFromZero);
        var lista = new List<PlazoVencimiento>(numPlazos);
        decimal acumulado = 0;
        for (var i = 1; i <= numPlazos; i++)
        {
            var importe = i == numPlazos ? total - acumulado : cuota;
            acumulado += importe;
            lista.Add(new PlazoVencimiento((short)i, (short)numPlazos, fechaBase.AddDays(diasPrimerPlazo + (i - 1) * diasEntrePlazos), importe));
        }
        if (lista.Any(x => x.Importe <= 0))
            throw new ReglaContableException("El importe es demasiado pequeño para repartirlo en tantos plazos.");
        return lista;
    }

    /// <summary>Estado de un vencimiento según lo que queda por cobrar/pagar.</summary>
    public static string EstadoTras(decimal importe, decimal pendiente)
        => pendiente <= 0 ? "LIQUIDADO" : pendiente < importe ? "PARCIAL" : "PENDIENTE";
}
