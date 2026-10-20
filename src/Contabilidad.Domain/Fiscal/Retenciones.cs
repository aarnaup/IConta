namespace Contabilidad.Domain.Fiscal;

/// <summary>Percepciones con retención de un perceptor en el periodo (facturas recibidas de profesionales o arrendadores).</summary>
public sealed record PercepcionRetenida(long TerceroId, string Nif, string Nombre, decimal Base, decimal Retencion);

/// <summary>
/// Modelos 111 (retenciones de rendimientos del trabajo y de actividades económicas) y 115 (arrendamientos de inmuebles urbanos),
/// con las retenciones practicadas en las facturas recibidas. VERIFICAR la numeración con el formulario del año.
/// </summary>
public static class CalculadorRetenciones
{
    /// <param name="retencionesContables">Movimiento acreedor de la 4751 en el periodo (null si no se contrasta).</param>
    /// <param name="retencionesFacturas111y115">Lo que suman entre los dos modelos las facturas del periodo.</param>
    public static void Calcular(string modelo, IReadOnlyList<PercepcionRetenida> filas, decimal? retencionesContables,
        decimal retencionesFacturas111y115, DeclaracionCalculada d)
    {
        var perceptores = filas.GroupBy(f => f.TerceroId)
            .Select(g => new PercepcionRetenida(g.Key, g.First().Nif, g.First().Nombre, g.Sum(x => x.Base), g.Sum(x => x.Retencion)))
            .Where(p => p.Base != 0 || p.Retencion != 0)
            .OrderBy(p => p.Nombre, StringComparer.CurrentCulture).ToList();
        var num = perceptores.Count;
        var bases = perceptores.Sum(p => p.Base);
        var ret = perceptores.Sum(p => p.Retencion);

        void Add(string clave, string desc, decimal importe) => d.Casillas.Add(new Casilla(clave, desc, importe));
        if (modelo == "111")
        {
            Add("07", "Rendimientos de actividades económicas (dinerarios): nº de perceptores", num);
            Add("08", "Rendimientos de actividades económicas (dinerarios): importe de las percepciones", bases);
            Add("09", "Rendimientos de actividades económicas (dinerarios): importe de las retenciones", ret);
            Add("28", "Total liquidación: suma de retenciones e ingresos a cuenta", ret);
            Add("29", "A deducir (solo autoliquidación complementaria)", 0);
            Add("30", "Resultado a ingresar", ret);
            d.Avisos.Add("Solo incluye las retenciones de facturas recibidas (profesionales). Las de nóminas, premios, ganancias forestales o " +
                         "cesión de derechos de imagen se añaden a mano.");
        }
        else
        {
            Add("01", "Nº de perceptores", num);
            Add("02", "Base de las retenciones e ingresos a cuenta", bases);
            Add("03", "Retenciones e ingresos a cuenta", ret);
            Add("04", "A deducir (solo autoliquidación complementaria)", 0);
            Add("05", "Resultado a ingresar", ret);
        }
        d.Resultado = ret;
        d.AIngresar = Math.Max(0, ret);
        if (ret < 0) d.Avisos.Add("El resultado es negativo (rectificativas): estos modelos no tienen saldo a devolver; revisa las facturas.");
        if (num == 0) d.Avisos.Add($"No hay retenciones del modelo {modelo} en el periodo: si hay obligación de presentarlo, será sin actividad.");
        if (retencionesContables is { } c && c != retencionesFacturas111y115)
            d.Avisos.Add($"La cuenta de retenciones practicadas (4751) se ha movido {c:N2} en el periodo y las facturas suman {retencionesFacturas111y115:N2} " +
                         "entre el 111 y el 115: la diferencia (nóminas, asientos manuales…) no está en el modelo.");
        d.Detalle = perceptores;
    }
}
