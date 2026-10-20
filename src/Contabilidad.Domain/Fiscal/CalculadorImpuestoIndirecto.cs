namespace Contabilidad.Domain.Fiscal;

/// <summary>
/// Importes de facturas contabilizadas del periodo, ya agrupados por la base de datos.
/// Las rectificativas llegan con importes negativos.
/// </summary>
public sealed record GrupoIva(
    string Libro, bool Rectificativa, string TipoOperacion, decimal Porcentaje, decimal PorcentajeRecargo, bool EsBienInversion,
    decimal Base, decimal Cuota, decimal CuotaRecargo, decimal CuotaDeducible);

public sealed record EntradaImpuestoIndirecto(
    IReadOnlyList<GrupoIva> Grupos,
    /// <summary>Regularización por el % definitivo de prorrata (solo se aplica en el último periodo del año).</summary>
    decimal RegularizacionProrrata,
    /// <summary>Cuotas a compensar de periodos anteriores todavía sin aplicar.</summary>
    decimal CompensacionPendiente,
    bool SolicitaDevolucion,
    bool EsUltimoPeriodo);

/// <summary>
/// Autoliquidación de IVA (303) o IGIC (420) y resúmenes anuales, sobre facturas ya agrupadas.
/// El 303 usa la numeración de casillas del formulario (01–09 devengado, 16–24 recargo, 27, 28–31, 45, 46, 64–71…);
/// el IGIC usa claves descriptivas (D7B/D7C = base/cuota al 7 %, TDEV, TDED, RES…) porque sus tipos varían.
/// VERIFICAR la numeración con el formulario del año antes de presentar.
/// </summary>
public static class CalculadorImpuestoIndirecto
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Pct(decimal p) => p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public static void Calcular(bool igic, EntradaImpuestoIndirecto e, DeclaracionCalculada d)
    {
        var interiores = e.Grupos.Where(g => g.TipoOperacion == "INTERIOR").ToList();
        var noSoportadas = e.Grupos.Where(g => g.TipoOperacion is not ("INTERIOR" or "EXENTA" or "NO_SUJETA")).ToList();
        if (noSoportadas.Count > 0)
            throw new ReglaContableException("Hay operaciones intracomunitarias, importaciones o con inversión del sujeto pasivo.",
                "Todavía no se calculan en este modelo; revisa las facturas del periodo.");

        var devengado = interiores.Where(g => g.Libro == "EMITIDAS" && !g.Rectificativa).ToList();
        var emitidasRect = interiores.Where(g => g.Libro == "EMITIDAS" && g.Rectificativa).ToList();
        var soportado = interiores.Where(g => g.Libro == "RECIBIDAS" && !g.Rectificativa).ToList();
        var recibidasRect = interiores.Where(g => g.Libro == "RECIBIDAS" && g.Rectificativa).ToList();

        void Add(string clave, string desc, decimal importe) => d.Casillas.Add(new Casilla(clave, desc, importe));

        // ---------------------------------------------------------------- devengado
        decimal totalDevengado = 0;
        if (igic)
        {
            foreach (var g in devengado.GroupBy(x => x.Porcentaje).OrderBy(x => x.Key))
            {
                var b = g.Sum(x => x.Base); var c = g.Sum(x => x.Cuota);
                if (b == 0 && c == 0) continue;
                Add($"D{Pct(g.Key)}B", $"Base imponible al {Pct(g.Key)} %", b);
                Add($"D{Pct(g.Key)}C", $"Cuota devengada al {Pct(g.Key)} %", c);
                totalDevengado += c;
            }
            foreach (var g in devengado.Where(x => x.PorcentajeRecargo > 0).GroupBy(x => x.PorcentajeRecargo).OrderBy(x => x.Key))
            {
                var b = g.Sum(x => x.Base); var c = g.Sum(x => x.CuotaRecargo);
                Add($"R{Pct(g.Key)}B", $"Base con recargo de equivalencia {Pct(g.Key)} %", b);
                Add($"R{Pct(g.Key)}C", $"Cuota de recargo de equivalencia {Pct(g.Key)} %", c);
                totalDevengado += c;
            }
            if (emitidasRect.Count > 0)
            {
                var c = emitidasRect.Sum(x => x.Cuota + x.CuotaRecargo);
                Add("MODB", "Modificación de bases (rectificativas emitidas)", emitidasRect.Sum(x => x.Base));
                Add("MODC", "Modificación de cuotas (rectificativas emitidas)", c);
                totalDevengado += c;
            }
            Add("TDEV", "Total cuota devengada", R(totalDevengado));
        }
        else
        {
            var tramos = new (decimal Pct, string B, string T, string C)[] { (4m, "01", "02", "03"), (10m, "04", "05", "06"), (21m, "07", "08", "09") };
            foreach (var t in tramos)
            {
                var g = devengado.Where(x => x.Porcentaje == t.Pct).ToList();
                var b = g.Sum(x => x.Base); var c = g.Sum(x => x.Cuota);
                if (b == 0 && c == 0) continue;
                Add(t.B, $"Régimen general: base imponible al {t.Pct} %", b);
                Add(t.T, "Tipo %", t.Pct);
                Add(t.C, $"Régimen general: cuota al {t.Pct} %", c);
                totalDevengado += c;
            }
            var sinCasilla = devengado.Where(x => x.Porcentaje is not (4m or 10m or 21m) && (x.Base != 0 || x.Cuota != 0)).Select(x => x.Porcentaje).Distinct().ToList();
            if (sinCasilla.Count > 0)
                throw new ReglaContableException($"Hay facturas emitidas con IVA al {string.Join(", ", sinCasilla.Select(Pct))} %, que no tiene casilla en el 303.",
                    "Solo se calculan los tipos 4, 10 y 21 %.");

            var recargos = new (decimal Pct, string B, string T, string C)[] { (0.5m, "16", "17", "18"), (1.4m, "19", "20", "21"), (5.2m, "22", "23", "24") };
            foreach (var t in recargos)
            {
                var g = devengado.Where(x => x.PorcentajeRecargo == t.Pct).ToList();
                var b = g.Sum(x => x.Base); var c = g.Sum(x => x.CuotaRecargo);
                if (b == 0 && c == 0) continue;
                Add(t.B, $"Recargo de equivalencia: base al {t.Pct} %", b);
                Add(t.T, "Tipo %", t.Pct);
                Add(t.C, $"Recargo de equivalencia: cuota al {t.Pct} %", c);
                totalDevengado += c;
            }
            if (devengado.Any(x => x.PorcentajeRecargo > 0 && x.PorcentajeRecargo is not (0.5m or 1.4m or 5.2m)))
                throw new ReglaContableException("Hay un recargo de equivalencia con un tipo distinto de 0,5, 1,4 o 5,2 %.");

            if (emitidasRect.Count > 0)
            {
                var cIva = emitidasRect.Sum(x => x.Cuota);
                Add("14", "Modificación de bases y cuotas: base (rectificativas emitidas)", emitidasRect.Sum(x => x.Base));
                Add("15", "Modificación de bases y cuotas: cuota (rectificativas emitidas)", cIva);
                totalDevengado += cIva;
                var cRec = emitidasRect.Sum(x => x.CuotaRecargo);
                if (cRec != 0)
                {
                    Add("25", "Modificación de recargo de equivalencia: base", emitidasRect.Where(x => x.CuotaRecargo != 0).Sum(x => x.Base));
                    Add("26", "Modificación de recargo de equivalencia: cuota", cRec);
                    totalDevengado += cRec;
                }
            }
            Add("27", "Total cuota devengada", R(totalDevengado));
        }

        // ---------------------------------------------------------------- deducible
        decimal totalDeducible = 0;
        var corr = soportado.Where(x => !x.EsBienInversion).ToList();
        var inv = soportado.Where(x => x.EsBienInversion).ToList();
        string kCorrB = igic ? "SCB" : "28", kCorrC = igic ? "SCC" : "29", kInvB = igic ? "SIB" : "30", kInvC = igic ? "SIC" : "31";
        if (corr.Count > 0)
        {
            Add(kCorrB, "Operaciones interiores corrientes: base", corr.Sum(x => x.Base));
            Add(kCorrC, "Operaciones interiores corrientes: cuota deducible", corr.Sum(x => x.CuotaDeducible));
            totalDeducible += corr.Sum(x => x.CuotaDeducible);
        }
        if (inv.Count > 0)
        {
            Add(kInvB, "Bienes de inversión interiores: base", inv.Sum(x => x.Base));
            Add(kInvC, "Bienes de inversión interiores: cuota deducible", inv.Sum(x => x.CuotaDeducible));
            totalDeducible += inv.Sum(x => x.CuotaDeducible);
        }
        if (recibidasRect.Count > 0)
        {
            Add(igic ? "RDB" : "40", "Rectificación de deducciones: base", recibidasRect.Sum(x => x.Base));
            var c = recibidasRect.Sum(x => x.CuotaDeducible);
            Add(igic ? "RDC" : "41", "Rectificación de deducciones: cuota", c);
            totalDeducible += c;
        }
        if (e.EsUltimoPeriodo && e.RegularizacionProrrata != 0)
        {
            Add(igic ? "REG" : "44", "Regularización por aplicación del porcentaje definitivo de prorrata", e.RegularizacionProrrata);
            totalDeducible += e.RegularizacionProrrata;
        }
        else if (!e.EsUltimoPeriodo && e.RegularizacionProrrata != 0)
            d.Avisos.Add("La regularización por prorrata definitiva se aplica en el último periodo del año.");

        Add(igic ? "TDED" : "45", "Total a deducir", R(totalDeducible));

        // ---------------------------------------------------------------- resultado
        var resultadoRG = R(totalDevengado) - R(totalDeducible);
        decimal resultado, aplicada = 0;
        if (igic)
        {
            Add("RES", "Resultado antes de compensaciones (devengado − deducible)", resultadoRG);
            if (resultadoRG > 0) aplicada = Math.Min(resultadoRG, e.CompensacionPendiente);
            Add("PEND_ANT", "Cuotas a compensar de periodos anteriores (pendientes)", e.CompensacionPendiente);
            Add("COMP", "Cuotas a compensar aplicadas en este periodo", aplicada);
            resultado = resultadoRG - aplicada;
            Add("LIQ", "Resultado de la liquidación", resultado);
        }
        else
        {
            Add("46", "Resultado régimen general (27 − 45)", resultadoRG);
            Add("64", "Suma de resultados", resultadoRG);
            Add("65", "% de atribución a la Administración del Estado", 100m);
            var atribuible = R(resultadoRG);
            Add("66", "Cuota atribuible a la Administración del Estado", atribuible);
            if (atribuible > 0) aplicada = Math.Min(atribuible, e.CompensacionPendiente);
            Add("110", "Cuotas a compensar de periodos anteriores (pendientes)", e.CompensacionPendiente);
            Add("68", "Cuotas a compensar de periodos anteriores aplicadas", aplicada);
            resultado = atribuible - aplicada;
            Add("69", "Resultado", resultado);
            Add("71", "Resultado de la liquidación", resultado);
        }

        d.Resultado = resultado;
        if (resultado >= 0) d.AIngresar = resultado;
        else if (e.SolicitaDevolucion && e.EsUltimoPeriodo) d.ADevolver = -resultado;
        else
        {
            d.ACompensar = -resultado;
            if (e.SolicitaDevolucion) d.Avisos.Add("La devolución solo se puede solicitar en el último periodo del año; el saldo se compensa.");
        }
        var pendienteSiguiente = (e.CompensacionPendiente - aplicada) + d.ACompensar;
        Add(igic ? "PEND_SIG" : "87", "Cuotas a compensar pendientes para periodos posteriores", pendienteSiguiente);
    }
}
