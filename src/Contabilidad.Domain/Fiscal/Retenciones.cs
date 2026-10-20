namespace Contabilidad.Domain.Fiscal;

/// <summary>Percepciones con retención de un perceptor en el periodo (facturas recibidas de profesionales o arrendadores).</summary>
public sealed record PercepcionRetenida(long TerceroId, string Nif, string Nombre, decimal Base, decimal Retencion);

/// <summary>Rendimientos del trabajo de un trabajador en el periodo (nóminas): dinerarios con su retención y en especie con su ingreso a cuenta.</summary>
public sealed record PercepcionTrabajo(long TerceroId, string Nif, string Nombre, decimal Dinerario, decimal Retencion, decimal Especie,
    decimal IngresoACuenta);

/// <summary>
/// Modelos 111 (retenciones de rendimientos del trabajo y de actividades económicas) y 115 (arrendamientos de inmuebles urbanos),
/// con las retenciones practicadas en las facturas recibidas y, en el 111, las de las nóminas. VERIFICAR la numeración con el formulario del año.
/// </summary>
public static class CalculadorRetenciones
{
    /// <param name="retencionesContables">Movimiento acreedor de la 4751 en el periodo (null si no se contrasta).</param>
    /// <param name="retencionesFacturas111y115">Lo que suman entre los dos modelos las facturas del periodo (más las nóminas).</param>
    /// <param name="trabajo">Nóminas del periodo por trabajador (solo en el 111).</param>
    public static void Calcular(string modelo, IReadOnlyList<PercepcionRetenida> filas, decimal? retencionesContables,
        decimal retencionesFacturas111y115, DeclaracionCalculada d, IReadOnlyList<PercepcionTrabajo>? trabajo = null)
    {
        trabajo ??= [];
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
            var trab = trabajo.GroupBy(t => t.TerceroId)
                .Select(g => new PercepcionTrabajo(g.Key, g.First().Nif, g.First().Nombre, g.Sum(x => x.Dinerario), g.Sum(x => x.Retencion),
                    g.Sum(x => x.Especie), g.Sum(x => x.IngresoACuenta))).ToList();
            var dinerarios = trab.Where(t => t.Dinerario != 0 || t.Retencion != 0).ToList();
            var especie = trab.Where(t => t.Especie != 0 || t.IngresoACuenta != 0).ToList();
            Add("01", "Rendimientos del trabajo (dinerarios): nº de perceptores", dinerarios.Count);
            Add("02", "Rendimientos del trabajo (dinerarios): importe de las percepciones", dinerarios.Sum(t => t.Dinerario));
            Add("03", "Rendimientos del trabajo (dinerarios): importe de las retenciones", dinerarios.Sum(t => t.Retencion));
            Add("04", "Rendimientos del trabajo (en especie): nº de perceptores", especie.Count);
            Add("05", "Rendimientos del trabajo (en especie): valor de las percepciones", especie.Sum(t => t.Especie));
            Add("06", "Rendimientos del trabajo (en especie): importe de los ingresos a cuenta", especie.Sum(t => t.IngresoACuenta));
            Add("07", "Rendimientos de actividades económicas (dinerarios): nº de perceptores", num);
            Add("08", "Rendimientos de actividades económicas (dinerarios): importe de las percepciones", bases);
            Add("09", "Rendimientos de actividades económicas (dinerarios): importe de las retenciones", ret);
            ret += trab.Sum(t => t.Retencion + t.IngresoACuenta);
            num += trab.Count;
            Add("28", "Total liquidación: suma de retenciones e ingresos a cuenta", ret);
            Add("29", "A deducir (solo autoliquidación complementaria)", 0);
            Add("30", "Resultado a ingresar", ret);
            d.Avisos.Add("Incluye las retenciones de las facturas recibidas (profesionales) y de las nóminas. Las de premios, ganancias forestales " +
                         "o cesión de derechos de imagen se añaden a mano.");
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
            d.Avisos.Add($"La cuenta de retenciones practicadas (4751) se ha movido {c:N2} en el periodo y las facturas y nóminas suman {retencionesFacturas111y115:N2} " +
                         "entre el 111 y el 115: la diferencia (nóminas, asientos manuales…) no está en el modelo.");
        d.Detalle = perceptores;
    }
}
