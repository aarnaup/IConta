namespace Contabilidad.Domain.Fiscal;

/// <summary>Importe (base + cuota + recargo) de las facturas de un tercero en un trimestre y clave (A adquisiciones, B entregas).</summary>
public sealed record Operacion347(long TerceroId, string Nif, string Nombre, string Clave, int Trimestre, decimal Importe);

public sealed record Fila347(long TerceroId, string Nif, string Nombre, string Clave, decimal Total, decimal T1, decimal T2, decimal T3, decimal T4);

/// <summary>Modelo 347 (y 415): terceros con operaciones anuales que superan 3.005,06 € por clave.</summary>
public static class Calculador347
{
    public const decimal Umbral = 3005.06m;

    public static void Calcular(IReadOnlyList<Operacion347> operaciones, DeclaracionCalculada d)
    {
        var filas = operaciones
            .GroupBy(o => (o.TerceroId, o.Clave))
            .Select(g =>
            {
                decimal T(int q) => g.Where(o => o.Trimestre == q).Sum(o => o.Importe);
                var f = g.First();
                return new Fila347(f.TerceroId, f.Nif, f.Nombre, f.Clave, g.Sum(o => o.Importe), T(1), T(2), T(3), T(4));
            })
            .ToList();

        var declaradas = filas.Where(f => f.Total > Umbral).OrderBy(f => f.Clave).ThenBy(f => f.Nombre).ToList();
        foreach (var f in declaradas.Where(f => string.IsNullOrWhiteSpace(f.Nif)))
            d.Avisos.Add($"{f.Nombre} no tiene NIF.");

        decimal Total(string clave) => declaradas.Where(f => f.Clave == clave).Sum(f => f.Total);
        d.Casillas.AddRange([
            new("N", "Número de terceros declarados", declaradas.Count),
            new("TA", "Total adquisiciones de bienes y servicios (clave A)", Total("A")),
            new("TB", "Total entregas de bienes y servicios (clave B)", Total("B")),
        ]);
        d.Detalle = declaradas;
        d.Avisos.Add("No se incluyen los cobros en metálico superiores a 6.000 €, los arrendamientos con retención ni las operaciones intracomunitarias, importaciones o con criterio de caja.");
        var cercanos = filas.Count(f => f.Total <= Umbral && f.Total > Umbral * 0.9m);
        if (cercanos > 0) d.Avisos.Add($"{cercanos} tercero(s) quedan justo por debajo del umbral de {Umbral:N2} €.");
    }
}
