namespace Contabilidad.Domain.Fiscal;

/// <summary>Base de una factura intracomunitaria del periodo (las rectificativas del mismo periodo restan aquí).</summary>
public sealed record OperacionIntracom(long TerceroId, string Nif, string Pais, bool PaisUe, string Nombre, string Clave, decimal Base);

/// <summary>Rectificación de un periodo anterior: base declarada entonces y la que resulta con la rectificativa.</summary>
public sealed record Rectificacion349(string NifComunitario, string Nombre, string Clave, int Anio, string Periodo,
    decimal BaseAnterior, decimal BaseRectificada);

public sealed record Operador349(string NifComunitario, string Nombre, string Clave, decimal Base);

public sealed record Detalle349(IReadOnlyList<Operador349> Operaciones, IReadOnlyList<Rectificacion349> Rectificaciones);

/// <summary>
/// Modelo 349, declaración recapitulativa de operaciones intracomunitarias: una línea por operador (NIF-IVA con prefijo del país)
/// y clave (E entregas, S servicios prestados, T triangulares, A adquisiciones, I servicios recibidos), por la base imponible.
/// Las rectificativas de periodos anteriores van en el apartado de rectificaciones. VERIFICAR con las instrucciones del año.
/// </summary>
public static class Calculador349
{
    public static readonly string[] ClavesEmitidas = ["E", "S", "T"];
    public static readonly string[] ClavesRecibidas = ["A", "I"];

    /// <summary>NIF-IVA comunitario: el número con el prefijo del país (Grecia usa EL).</summary>
    public static string NifComunitario(string nif, string pais)
    {
        var n = new string((nif ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var prefijo = pais.ToUpperInvariant() == "GR" ? "EL" : pais.ToUpperInvariant();
        return n.StartsWith(prefijo, StringComparison.Ordinal) ? n : prefijo + n;
    }

    public static void Calcular(IReadOnlyList<OperacionIntracom> operaciones, IReadOnlyList<Rectificacion349> rectificaciones, DeclaracionCalculada d)
    {
        var fuera = operaciones.Where(o => !o.PaisUe || o.Pais.Equals("ES", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Nombre).Distinct().ToList();
        if (fuera.Count > 0)
            d.Avisos.Add($"Operaciones intracomunitarias con terceros que no son de otro país de la UE: {string.Join(", ", fuera)}. Revisa su NIF y país.");

        var ops = operaciones
            .GroupBy(o => (Nif: NifComunitario(o.Nif, o.Pais), o.Clave))
            .Select(g => new Operador349(g.Key.Nif, g.First().Nombre, g.Key.Clave, g.Sum(x => x.Base)))
            .Where(o => o.Base != 0)
            .OrderBy(o => o.Clave, StringComparer.Ordinal).ThenBy(o => o.NifComunitario, StringComparer.Ordinal).ToList();
        var negativos = ops.Where(o => o.Base < 0).Select(o => o.NifComunitario).ToList();
        if (negativos.Count > 0)
            d.Avisos.Add($"Bases negativas en el periodo ({string.Join(", ", negativos)}): una rectificativa sin factura de este periodo; " +
                         "indica la factura rectificada para declararla como rectificación.");

        var rect = rectificaciones.OrderBy(r => r.Anio).ThenBy(r => r.Periodo, StringComparer.Ordinal).ThenBy(r => r.NifComunitario, StringComparer.Ordinal).ToList();

        void Add(string clave, string desc, decimal importe) => d.Casillas.Add(new Casilla(clave, desc, importe));
        Add("01", "Número total de operadores intracomunitarios", ops.Count);
        Add("02", "Importe de las operaciones intracomunitarias", ops.Sum(o => o.Base));
        Add("03", "Número total de operadores con rectificaciones", rect.Count);
        Add("04", "Importe de las rectificaciones", rect.Sum(r => r.BaseRectificada));
        d.Detalle = new Detalle349(ops, rect);
        if (ops.Count == 0 && rect.Count == 0)
            d.Avisos.Add("No hay operaciones intracomunitarias en el periodo: el 349 solo se presenta si las hay.");
    }
}
