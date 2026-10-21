namespace Contabilidad.Domain.Fiscal;

/// <summary>Datos del 425 además de los grupos de facturas del año.</summary>
/// <param name="Autoliquidaciones">420 presentados del año (mismos campos que en el 390).</param>
/// <param name="CuotaSimplificado">Cuota anual derivada del régimen simplificado (módulos), si la empresa está en él.</param>
/// <param name="ComercianteMinorista">Régimen especial del comerciante minorista: solo volumen de operaciones (exentas).</param>
public sealed record Entrada425(IReadOnlyList<GrupoIva> Grupos, decimal RegularizacionBienesInversion, decimal RegularizacionProrrata,
    IReadOnlyList<Resumen303> Autoliquidaciones, decimal CuotaSimplificado = 0, bool ComercianteMinorista = false);

/// <summary>
/// Modelo 425, resumen anual del IGIC, con la numeración de casillas del modelo de la Agencia Tributaria Canaria (la de los módulos
/// l10n_es_atc_mod425 de OCA): tipos 0, 3, 7, 9,5, 15 y 20 % (01-18), modificación de bases (70/71), inversión del sujeto pasivo
/// (75/76), totales (74/79), deducciones (80-94), resultado del régimen general (95), liquidación anual (112-115), autoliquidaciones del
/// año (116-119) y volumen de operaciones (120). El simplificado y el volumen por clases van con claves descriptivas.
/// </summary>
public static class Calculador425
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Tipo de IGIC → casillas de base, tipo y cuota (en el 420 y en el 425).</summary>
    public static readonly IReadOnlyList<(decimal[] Pcts, string B, string T, string C)> Tipos =
    [
        ([0m], "01", "02", "03"), ([3m, 5m], "04", "05", "06"), ([7m], "07", "08", "09"), ([9.5m], "10", "11", "12"),
        ([15m], "13", "14", "15"), ([20m], "16", "17", "18"),
    ];

    public static void Calcular(Entrada425 e, DeclaracionCalculada d)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        var emitidas = e.Grupos.Where(g => g.Libro == "EMITIDAS").ToList();
        var recibidas = e.Grupos.Where(g => g.Libro == "RECIBIDAS").ToList();
        decimal bases = 0, cuotas = 0, deducible = 0;

        if (!e.ComercianteMinorista)
        {
            // ---------------------------------------------------------------- devengado
            var devengado = emitidas.Where(g => !g.Rectificativa && g.TipoOperacion == "INTERIOR").ToList();
            var todos = Tipos.SelectMany(t => t.Pcts).ToHashSet();
            var sinCasilla = devengado.Where(g => !todos.Contains(g.Porcentaje) && (g.Base != 0 || g.Cuota != 0)).Select(g => g.Porcentaje).Distinct().ToList();
            if (sinCasilla.Count > 0)
                throw new ReglaContableException($"Hay ventas con IGIC al {string.Join(", ", sinCasilla)} %, que no tiene casilla en el 425.",
                    "Tipos del modelo: 0, 3, 7, 9,5, 15 y 20 %.");
            foreach (var t in Tipos)
            {
                var g = devengado.Where(x => t.Pcts.Contains(x.Porcentaje)).ToList();
                if (g.Count == 0) continue;
                Add(t.B, $"Régimen general: base imponible al {string.Join(" / ", t.Pcts)} %", g.Sum(x => x.Base));
                Add(t.T, "Tipo %", g.Select(x => x.Porcentaje).Distinct().Count() == 1 ? g[0].Porcentaje : t.Pcts[0]);
                Add(t.C, $"Régimen general: cuota al {string.Join(" / ", t.Pcts)} %", g.Sum(x => x.Cuota));
                bases += g.Sum(x => x.Base); cuotas += g.Sum(x => x.Cuota);
            }
            var rect = emitidas.Where(g => g.Rectificativa && g.TipoOperacion == "INTERIOR").ToList();
            if (rect.Count > 0)
            {
                Add("70", "Modificación de bases y cuotas: base", rect.Sum(x => x.Base));
                Add("71", "Modificación de bases y cuotas: cuota", rect.Sum(x => x.Cuota));
                bases += rect.Sum(x => x.Base); cuotas += rect.Sum(x => x.Cuota);
            }
            var isp = recibidas.Where(g => g.TipoOperacion == "INVERSION_SUJETO_PASIVO").ToList();
            if (isp.Count > 0)
            {
                Add("75", "Operaciones con inversión del sujeto pasivo: base", isp.Sum(x => x.Base));
                Add("76", "Operaciones con inversión del sujeto pasivo: cuota", isp.Sum(x => x.Cuota));
                bases += isp.Sum(x => x.Base); cuotas += isp.Sum(x => x.Cuota);
            }
            Add("74", "Total bases imponibles", bases);
            Add("79", "Total cuotas devengadas", cuotas);

            // ---------------------------------------------------------------- deducible
            var soportado = recibidas.Where(g => !g.Rectificativa && g.TipoOperacion is "INTERIOR" or "INVERSION_SUJETO_PASIVO" or "IMPORTACION").ToList();
            foreach (var (es, inv, b, c, txt) in new (Func<GrupoIva, bool>, bool, string, string, string)[] {
                (x => x.TipoOperacion != "IMPORTACION", false, "80", "81", "Operaciones interiores, bienes y servicios corrientes"),
                (x => x.TipoOperacion != "IMPORTACION", true, "82", "83", "Operaciones interiores, bienes de inversión"),
                (x => x.TipoOperacion == "IMPORTACION", false, "84", "85", "Importaciones de bienes corrientes"),
                (x => x.TipoOperacion == "IMPORTACION", true, "86", "87", "Importaciones de bienes de inversión") })
            {
                var g = soportado.Where(x => es(x) && x.EsBienInversion == inv && x.CuotaDeducible != 0).ToList();
                if (g.Count == 0) continue;
                Add(b, $"IGIC deducible. {txt}: base", g.Sum(x => x.Base));
                Add(c, $"IGIC deducible. {txt}: cuota", g.Sum(x => x.CuotaDeducible));
                deducible += g.Sum(x => x.CuotaDeducible);
            }
            var rectRec = recibidas.Where(g => g.Rectificativa && g.TipoOperacion is "INTERIOR" or "INVERSION_SUJETO_PASIVO" or "IMPORTACION").ToList();
            if (rectRec.Count > 0)
            {
                Add("88", "Rectificación de deducciones: base", rectRec.Sum(x => x.Base));
                Add("89", "Rectificación de deducciones: cuota", rectRec.Sum(x => x.CuotaDeducible));
                deducible += rectRec.Sum(x => x.CuotaDeducible);
            }
            if (e.RegularizacionBienesInversion != 0) Add("91", "Regularización de cuotas soportadas por bienes de inversión", e.RegularizacionBienesInversion);
            if (e.RegularizacionProrrata != 0) Add("93", "Regularización por aplicación del porcentaje definitivo de prorrata", e.RegularizacionProrrata);
            deducible += e.RegularizacionBienesInversion + e.RegularizacionProrrata;
            Add("94", "Total cuotas deducibles", deducible);
            Add("95", "Resultado régimen general", cuotas - deducible);
            if (e.CuotaSimplificado != 0) Add("TRS", "Régimen simplificado: total cuota anual derivada (módulos)", e.CuotaSimplificado);
        }

        // ---------------------------------------------------------------- liquidación anual y autoliquidaciones del año
        var autoliq = e.Autoliquidaciones;
        var primero = autoliq.FirstOrDefault(x => x.Periodo is "1T" or "01");
        var aplicadas = autoliq.Sum(x => x.CompensacionAplicada);
        var compensacion = primero is null ? aplicadas : Math.Min(aplicadas, primero.CompensacionPendienteInicial);
        var suma = cuotas - deducible + e.CuotaSimplificado;
        Add("112", "Regularización de cuotas art. 22.8.5ª Ley 20/1991", 0);
        Add("113", "Suma de resultados", suma);
        Add("114", "Cuota de IGIC a compensar del ejercicio anterior", compensacion);
        Add("115", "Resultado de la liquidación anual", suma - compensacion);
        var ultimo = autoliq.LastOrDefault();
        Add("116", "Total de ingresos realizados en las autoliquidaciones del año", autoliq.Sum(x => x.AIngresar));
        Add("117", "Total devoluciones mensuales", 0);
        Add("118", "Resultado de la última autoliquidación: a compensar", ultimo?.ACompensar ?? 0);
        Add("119", "Resultado de la última autoliquidación: a devolver", ultimo?.ADevolver ?? 0);

        // ---------------------------------------------------------------- volumen de operaciones
        var regGeneral = emitidas.Where(g => g.TipoOperacion == "INTERIOR" && !g.EsBienInversion && !e.ComercianteMinorista && e.CuotaSimplificado == 0).Sum(g => g.Base);
        var simplificado = e.CuotaSimplificado != 0 ? emitidas.Where(g => g.TipoOperacion == "INTERIOR" && !g.EsBienInversion).Sum(g => g.Base) : 0;
        var exportaciones = emitidas.Where(g => g.TipoOperacion == "EXPORTACION").Sum(g => g.Base);
        var exentas = emitidas.Where(g => g.TipoOperacion == "EXENTA" && !g.EsBienInversion).Sum(g => g.Base);
        var noSujetas = emitidas.Where(g => g.TipoOperacion is "NO_SUJETA_LOCAL" or "NO_SUJETA_OSS").Sum(g => g.Base);
        if (regGeneral != 0) Add("VREG", "Volumen: operaciones en régimen general", regGeneral);
        if (simplificado != 0) Add("VRES", "Volumen: operaciones en régimen simplificado", simplificado);
        if (exportaciones != 0) Add("VEXP", "Volumen: exportaciones y operaciones asimiladas", exportaciones);
        if (exentas != 0) Add("VESD", e.ComercianteMinorista ? "Volumen: exentas del comerciante minorista (sin derecho a deducción)"
                                                             : "Volumen: operaciones exentas sin derecho a deducción", exentas);
        if (noSujetas != 0) Add("VNSJ", "Volumen: no sujetas por reglas de localización", noSujetas);
        Add("120", "Total volumen de operaciones", regGeneral + simplificado + exportaciones + exentas + noSujetas);

        // El resumen anual es informativo
        d.Resultado = R(suma - compensacion);
        d.AIngresar = d.ADevolver = d.ACompensar = 0;
        var presentado = autoliq.Sum(x => x.Resultado);
        d.Avisos.Add(autoliq.Count == 0
            ? "No hay autoliquidaciones 420 presentadas en el año con las que contrastar."
            : R(presentado) == R(suma)
                ? $"Cuadra con la suma de las {autoliq.Count} autoliquidaciones 420 presentadas ({presentado:N2})."
                : $"NO cuadra: resultado anual {suma:N2} frente a {presentado:N2} en las {autoliq.Count} autoliquidaciones 420 presentadas.");
        if (e.ComercianteMinorista)
            d.Avisos.Add("Comerciante minorista: el 425 solo recoge el volumen de operaciones. VERIFICA si estás obligado a presentarlo.");
    }
}
