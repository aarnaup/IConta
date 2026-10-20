namespace Contabilidad.Domain.Analitica;

/// <summary>Elemento analítico (centro de coste, proyecto…) con su dimensión y el porcentaje que se le imputa.</summary>
public sealed record ImputacionElemento(long ElementoId, long DimensionId, decimal Porcentaje);

/// <summary>Parte de un apunte imputada a un elemento.</summary>
public sealed record ParteAnalitica(long ElementoId, decimal Porcentaje, decimal Debe, decimal Haber);

/// <summary>
/// Reparto de un apunte entre elementos analíticos. Cada dimensión es independiente (un gasto puede ir a un centro de coste y,
/// a la vez, a un proyecto) y dentro de cada una los porcentajes suman 100. El redondeo se lo lleva el último elemento de la
/// dimensión, así que la suma de las partes es exactamente el importe del apunte.
/// </summary>
public static class RepartoAnalitico
{
    public static void Validar(IReadOnlyList<ImputacionElemento> imputaciones)
    {
        if (imputaciones.Any(i => i.Porcentaje <= 0 || i.Porcentaje > 100))
            throw new ReglaContableException("Los porcentajes de analítica van de 0 (sin incluir) a 100.");
        if (imputaciones.GroupBy(i => i.ElementoId).Any(g => g.Count() > 1))
            throw new ReglaContableException("Un elemento analítico aparece dos veces en el mismo reparto.");
        foreach (var d in imputaciones.GroupBy(i => i.DimensionId))
            if (d.Sum(i => i.Porcentaje) != 100m)
                throw new ReglaContableException("Los porcentajes de cada dimensión analítica deben sumar 100.",
                    $"suman {d.Sum(i => i.Porcentaje):0.####}");
    }

    public static IReadOnlyList<ParteAnalitica> Repartir(decimal debe, decimal haber, IReadOnlyList<ImputacionElemento> imputaciones)
    {
        Validar(imputaciones);
        var partes = new List<ParteAnalitica>();
        foreach (var d in imputaciones.GroupBy(i => i.DimensionId))
        {
            var lista = d.ToList();
            decimal restoD = debe, restoH = haber;
            for (var i = 0; i < lista.Count; i++)
            {
                var ultimo = i == lista.Count - 1;
                var pd = ultimo ? restoD : Math.Round(debe * lista[i].Porcentaje / 100m, 2, MidpointRounding.AwayFromZero);
                var ph = ultimo ? restoH : Math.Round(haber * lista[i].Porcentaje / 100m, 2, MidpointRounding.AwayFromZero);
                restoD -= pd; restoH -= ph;
                partes.Add(new ParteAnalitica(lista[i].ElementoId, lista[i].Porcentaje, pd, ph));
            }
        }
        return partes;
    }

    /// <summary>
    /// Reparto por defecto de una cuenta: para cada dimensión vale el definido en la cuenta más concreta (prefijo más largo)
    /// entre la propia cuenta y sus cuentas padre.
    /// </summary>
    public static IReadOnlyList<ImputacionElemento> PorDefecto(string cuenta,
        IEnumerable<(string Cuenta, ImputacionElemento Imputacion)> definidos)
        => definidos.Where(x => cuenta.StartsWith(x.Cuenta, StringComparison.Ordinal))
            .GroupBy(x => x.Imputacion.DimensionId)
            .SelectMany(g =>
            {
                var largo = g.Max(x => x.Cuenta.Length);
                return g.Where(x => x.Cuenta.Length == largo).Select(x => x.Imputacion);
            })
            .ToList();
}
