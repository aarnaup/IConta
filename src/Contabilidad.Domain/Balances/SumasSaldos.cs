namespace Contabilidad.Domain.Balances;

/// <summary>Movimientos de una cuenta en el periodo pedido.</summary>
public sealed record MovimientoCuenta(string Codigo, decimal Debe, decimal Haber);

public sealed record FilaSumasSaldos(string Codigo, string Nombre, decimal Debe, decimal Haber)
{
    public decimal SaldoDeudor => Debe > Haber ? Debe - Haber : 0;
    public decimal SaldoAcreedor => Haber > Debe ? Haber - Debe : 0;
}

public static class SumasSaldos
{
    /// <summary>Nivel de detalle: 1 grupo, 2 subgrupo, 3 cuenta, 4 subcuenta (4 dígitos), 5 cuenta completa.</summary>
    public const int Detalle = 5;

    /// <summary>
    /// Agrupa los movimientos por los primeros <paramref name="nivel"/> dígitos del código (o por cuenta completa con nivel 5).
    /// <paramref name="nombre"/> da el nombre de cada código agrupado.
    /// </summary>
    public static IReadOnlyList<FilaSumasSaldos> Agrupar(IEnumerable<MovimientoCuenta> movimientos, int nivel, Func<string, string> nombre)
    {
        if (nivel is < 1 or > Detalle) throw new ReglaContableException("El nivel debe estar entre 1 (grupo) y 5 (cuenta completa).");
        return movimientos
            .Where(m => m.Debe != 0 || m.Haber != 0)
            .GroupBy(m => nivel == Detalle || m.Codigo.Length <= nivel ? m.Codigo : m.Codigo[..nivel])
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FilaSumasSaldos(g.Key, nombre(g.Key), g.Sum(m => m.Debe), g.Sum(m => m.Haber)))
            .ToList();
    }
}
