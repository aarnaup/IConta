namespace Contabilidad.Domain.Fiscal;

/// <summary>Percepciones del año de un perceptor con un tipo de retención.</summary>
public sealed record PercepcionAnual(long TerceroId, string Nif, string Nombre, string Pais, string? Provincia, string TipoRetencion, decimal Porcentaje,
    decimal Base, decimal Retencion);

/// <summary>Línea del resumen anual: una por perceptor, clave/subclave y porcentaje.</summary>
public sealed record PerceptorResumen(string Nif, string Nombre, string? Provincia, string Clave, string? Subclave, decimal Porcentaje,
    decimal Base, decimal Retencion);

/// <summary>
/// Resúmenes anuales de retenciones: 190 (trabajo y actividades económicas: clave G, actividades profesionales) y 180 (arrendamientos
/// de inmuebles urbanos). Se contrastan con los 111/115 presentados del año. VERIFICAR claves y subclaves con las instrucciones del año.
/// </summary>
public static class CalculadorResumenRetenciones
{
    /// <summary>Clave y subclave del 190 según el tipo de retención (general e inicio de actividad: G 01).</summary>
    public static (string Clave, string? Subclave) Clave190(string tipoRetencion) => tipoRetencion switch
    {
        "PROF_GEN" or "PROF_INI" => ("G", "01"),
        _ => ("G", "01"),
    };

    /// <summary>Provincia (código INE de 2 dígitos) o 98 para no residentes.</summary>
    public static string? Provincia(PercepcionAnual p) => p.Pais != "ES" ? "98" : p.Provincia;

    public static void Calcular(string modelo, IReadOnlyList<PercepcionAnual> filas, decimal? retencionesTrimestrales, int trimestralesPresentados,
        DeclaracionCalculada d)
    {
        var lineas = filas
            .GroupBy(f => (f.TerceroId, Clave: modelo == "190" ? Clave190(f.TipoRetencion) : ("", null), f.Porcentaje))
            .Select(g =>
            {
                var f = g.First();
                return new PerceptorResumen(f.Nif, f.Nombre, Provincia(f), g.Key.Clave.Item1, g.Key.Clave.Item2, g.Key.Porcentaje,
                    g.Sum(x => x.Base), g.Sum(x => x.Retencion));
            })
            .Where(l => l.Base != 0 || l.Retencion != 0)
            .OrderBy(l => l.Nombre, StringComparer.CurrentCulture).ThenBy(l => l.Porcentaje).ToList();

        void Add(string clave, string desc, decimal importe) => d.Casillas.Add(new Casilla(clave, desc, importe));
        Add("01", "Número total de perceptores", lineas.Select(l => l.Nif).Distinct().Count());
        Add("02", modelo == "190" ? "Importe total de las percepciones" : "Base de las retenciones e ingresos a cuenta", lineas.Sum(l => l.Base));
        Add("03", "Importe total de las retenciones e ingresos a cuenta", lineas.Sum(l => l.Retencion));
        d.Detalle = lineas;

        var sinProvincia = lineas.Where(l => l.Provincia is null).Select(l => l.Nombre).Distinct().ToList();
        if (sinProvincia.Count > 0)
            d.Avisos.Add($"Perceptores sin provincia (falta su código postal): {string.Join(", ", sinProvincia)}.");
        var trimestral = modelo == "190" ? "111" : "115";
        if (retencionesTrimestrales is null || trimestralesPresentados == 0)
            d.Avisos.Add($"No hay modelos {trimestral} presentados en el año con los que contrastar.");
        else if (retencionesTrimestrales != lineas.Sum(l => l.Retencion))
            d.Avisos.Add($"Las retenciones del resumen ({lineas.Sum(l => l.Retencion):N2}) no coinciden con las de los {trimestral} presentados " +
                         $"({retencionesTrimestrales:N2}, {trimestralesPresentados} periodo(s)).");
        if (modelo == "190")
            d.Avisos.Add("Solo incluye actividades profesionales (clave G) de las facturas recibidas; los rendimientos del trabajo (clave A) se añaden a mano.");
        else
            d.Avisos.Add("Cada arrendador debe llevar la referencia catastral y la dirección del inmueble arrendado: complétalas al presentar.");
    }
}
