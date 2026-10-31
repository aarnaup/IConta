namespace Contabilidad.Domain.Fiscal;

/// <summary>Fila del formulario: un texto y de una a tres casillas (p. ej. base, tipo y cuota); "" deja la celda vacía.</summary>
public sealed record FilaFormulario(string Texto, string[] Casillas, bool Total = false);

/// <summary>Apartado del formulario con los títulos de sus columnas.</summary>
public sealed record ApartadoFormulario(string Titulo, string[] Columnas, IReadOnlyList<FilaFormulario> Filas);

/// <summary>
/// Casilla que se calcula a partir de otras. Con <see cref="Terminos"/> se recalcula sumando a lo calculado la diferencia de cada término
/// (así conserva lo que no está en casillas, como la cuota del régimen simplificado); con <see cref="Funcion"/>, desde los valores actuales.
/// </summary>
public sealed record TotalFormulario(string Casilla, (string Casilla, int Signo)[] Terminos, Func<Func<string, decimal>, decimal>? Funcion = null);

/// <summary>
/// Formulario de un modelo con el aspecto del impreso oficial: apartados, filas y casillas, qué casillas son totales y cuál es el resultado.
/// </summary>
public sealed record FormularioModelo(string Modelo, IReadOnlyList<ApartadoFormulario> Apartados, IReadOnlyList<TotalFormulario> Totales,
    string? CasillaResultado, bool Compensable)
{
    public IEnumerable<string> CasillasTotales => Totales.Select(t => t.Casilla);
}

public static class FormulariosFiscales
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static FilaFormulario F(string texto, params string[] casillas) => new(texto, casillas);
    private static FilaFormulario T(string texto, params string[] casillas) => new(texto, casillas, true);
    private static (string, int)[] Suma(params string[] c) => c.Select(x => (x, 1)).ToArray();

    /// <summary>
    /// Formulario del modelo para una declaración con las casillas <paramref name="presentes"/> (las calculadas y las escritas a mano).
    /// Las filas opcionales (tipos temporales, apartados poco usados) solo salen si tienen alguna casilla presente.
    /// </summary>
    public static FormularioModelo Obtener(string modelo, IReadOnlyCollection<string> presentes)
    {
        var f = modelo switch
        {
            "303" => M303(),
            "420" => M420(presentes),
            "111" => M111(),
            "115" => M115(),
            "130" => M130(),
            "309" => M309(),
            _ => null,
        };
        return f is null ? Generico(modelo, presentes) : Filtrar(f, presentes);
    }

    // Quita las filas «opcionales» (marcadas con * al final del texto) sin casillas presentes
    private static FormularioModelo Filtrar(FormularioModelo f, IReadOnlyCollection<string> presentes)
        => f with
        {
            Apartados = f.Apartados.Select(a => a with
            {
                Filas = a.Filas.Where(x => !x.Texto.EndsWith('*') || x.Casillas.Any(presentes.Contains))
                    .Select(x => x.Texto.EndsWith('*') ? x with { Texto = x.Texto[..^1] } : x).ToList()
            }).Where(a => a.Filas.Count > 0).ToList()
        };

    // ------------------------------------------------------------------------------------------------ 303
    private static FormularioModelo M303()
    {
        string[] btc = ["Base imponible", "Tipo %", "Cuota"];
        return new("303",
        [
            new("IVA devengado", btc,
            [
                F("Régimen general al 0 %*", "150", "151", "152"),
                F("Régimen general al 2 %*", "165", "166", "167"),
                F("Régimen general al 4 %", "01", "02", "03"),
                F("Régimen general al 5 / 7,5 %*", "153", "154", "155"),
                F("Régimen general al 10 %", "04", "05", "06"),
                F("Régimen general al 21 %", "07", "08", "09"),
                F("Adquisiciones intracomunitarias de bienes y servicios", "10", "", "11"),
                F("Otras operaciones con inversión del sujeto pasivo", "12", "", "13"),
                F("Modificación de bases y cuotas", "14", "", "15"),
                F("Recargo de equivalencia al 0,5 %*", "168", "169", "170"),
                F("Recargo de equivalencia al 0,62 / 1 %*", "16", "17", "18"),
                F("Recargo de equivalencia al 1,4 %", "19", "20", "21"),
                F("Recargo de equivalencia al 1,75 %*", "156", "157", "158"),
                F("Recargo de equivalencia al 5,2 %", "22", "23", "24"),
                F("Modificación de bases y cuotas del recargo de equivalencia*", "25", "", "26"),
                T("Total cuota devengada", "", "", "27"),
            ]),
            new("IVA deducible", ["Base imponible", "Cuota deducible"],
            [
                F("Operaciones interiores corrientes", "28", "29"),
                F("Operaciones interiores con bienes de inversión", "30", "31"),
                F("Importaciones de bienes corrientes", "32", "33"),
                F("Importaciones de bienes de inversión", "34", "35"),
                F("Adquisiciones intracomunitarias de bienes y servicios corrientes", "36", "37"),
                F("Adquisiciones intracomunitarias de bienes de inversión", "38", "39"),
                F("Rectificación de deducciones", "40", "41"),
                F("Compensaciones del régimen especial de la agricultura, ganadería y pesca*", "", "42"),
                F("Regularización de bienes de inversión", "", "43"),
                F("Regularización por aplicación del porcentaje definitivo de prorrata", "", "44"),
                T("Total a deducir", "", "45"),
            ]),
            new("Resultado", ["Importe"],
            [
                T("Resultado régimen general (27 − 45)", "46"),
                T("Suma de resultados", "64"),
                F("% atribuible a la Administración del Estado", "65"),
                T("Atribuible a la Administración del Estado", "66"),
                F("IVA a la importación liquidado por la Aduana pendiente de ingreso", "77"),
                F("Cuotas a compensar pendientes de periodos anteriores", "110"),
                T("Cuotas a compensar de periodos anteriores aplicadas en este periodo", "78"),
                T("Resultado de la autoliquidación (66 + 77 − 78)", "69"),
                F("A deducir: resultado de la autoliquidación anterior (complementaria)", "70"),
                T("Resultado de la liquidación (69 − 70)", "71"),
            ]),
            new("Información adicional", ["Importe"],
            [
                F("Entregas intracomunitarias de bienes y servicios", "59"),
                F("Exportaciones y operaciones asimiladas", "60"),
                F("Operaciones no sujetas por reglas de localización (excepto 123)*", "120"),
                F("Operaciones sujetas con inversión del sujeto pasivo", "122"),
                F("Operaciones no sujetas acogidas a la ventanilla única*", "123"),
                F("Criterio de caja: entregas por la regla general (base)*", "62"),
                F("Criterio de caja: entregas por la regla general (cuota)*", "63"),
                F("Criterio de caja: adquisiciones (base)*", "74"),
                F("Criterio de caja: adquisiciones (cuota)*", "75"),
            ]),
        ],
        [
            new("27", Suma("152", "167", "03", "155", "06", "09", "11", "13", "15", "170", "18", "21", "158", "24", "26")),
            new("45", Suma("29", "31", "33", "35", "37", "39", "41", "42", "43", "44")),
            new("46", [("27", 1), ("45", -1)]),
            new("64", Suma("46")),
            new("66", [], v => R(v("64") * v("65") / 100m)),
            new("78", [], v => v("66") > 0 ? Math.Min(v("66"), Math.Max(v("110"), 0)) : 0m),
            new("69", [("66", 1), ("77", 1), ("78", -1)]),
            new("71", [("69", 1), ("70", -1)]),
        ], "71", true);
    }

    // ------------------------------------------------------------------------------------------------ 420 (IGIC, claves por tipo)
    private static FormularioModelo M420(IReadOnlyCollection<string> presentes)
    {
        // Las claves de base y cuota de un mismo concepto acaban en B y C (D7B/D7C, MODB/MODC, SCB/SCC…)
        IEnumerable<FilaFormulario> Pares(IEnumerable<string> claves, Func<string, string> texto)
            => claves.Where(k => k.EndsWith('B') && presentes.Contains(k[..^1] + "C") || k.EndsWith('C') && !presentes.Contains(k[..^1] + "B"))
                .Select(k => k.EndsWith('B') ? F(texto(k), k, k[..^1] + "C") : F(texto(k), "", k));
        string Tipo(string k) => k.Length > 2 && char.IsAsciiDigit(k[1]) ? k[1..^1] : "";
        var dev = presentes.Where(k => k.Length >= 3 && (k[0] is 'D' or 'R') && char.IsAsciiDigit(k[1])).Order(StringComparer.Ordinal).ToList();
        var devFilas = Pares(dev, k => k[0] == 'D' ? $"Régimen general al {Tipo(k)} %" : $"Recargo de equivalencia al {Tipo(k)} %").ToList();
        devFilas.Add(F("Inversión del sujeto pasivo", "ISPB", "ISPC"));
        devFilas.Add(F("Modificación de bases y cuotas", "MODB", "MODC"));
        devFilas.Add(T("Total cuota devengada", "", "TDEV"));
        var cuotasDev = dev.Where(k => k.EndsWith('C')).Concat(["ISPC", "MODC"]).ToArray();
        return new("420",
        [
            new("IGIC devengado", ["Base imponible", "Cuota"], devFilas),
            new("IGIC deducible", ["Base imponible", "Cuota deducible"],
            [
                F("Operaciones interiores corrientes", "SCB", "SCC"),
                F("Operaciones interiores con bienes de inversión", "SIB", "SIC"),
                F("Importaciones de bienes corrientes", "ICB", "ICC"),
                F("Importaciones de bienes de inversión", "IIB", "IIC"),
                F("Rectificación de deducciones", "RDB", "RDC"),
                F("Regularización de bienes de inversión", "", "RBI"),
                F("Regularización por aplicación del porcentaje definitivo de prorrata", "", "REG"),
                T("Total a deducir", "", "TDED"),
            ]),
            new("Resultado", ["Importe"],
            [
                T("Resultado antes de compensaciones", "RES"),
                F("Cuotas a compensar de periodos anteriores (pendientes)", "PEND_ANT"),
                T("Cuotas a compensar aplicadas en este periodo", "COMP"),
                T("Resultado de la liquidación", "LIQ"),
            ]),
            new("Información adicional", ["Importe"],
            [
                F("Exportaciones y operaciones asimiladas*", "EXP"),
                F("Operaciones sujetas con inversión del sujeto pasivo (emitidas)*", "ISPE"),
                F("Operaciones no sujetas por reglas de localización*", "NSL"),
                F("Criterio de caja: entregas (base)*", "CCEB"),
                F("Criterio de caja: entregas (cuota)*", "CCEC"),
                F("Criterio de caja: adquisiciones (base)*", "CCAB"),
                F("Criterio de caja: adquisiciones (cuota)*", "CCAC"),
            ]),
        ],
        [
            new("TDEV", Suma(cuotasDev)),
            new("TDED", Suma("SCC", "SIC", "ICC", "IIC", "RDC", "RBI", "REG")),
            new("RES", [("TDEV", 1), ("TDED", -1)]),
            new("COMP", [], v => v("RES") > 0 ? Math.Min(v("RES"), Math.Max(v("PEND_ANT"), 0)) : 0m),
            new("LIQ", [("RES", 1), ("COMP", -1)]),
        ], "LIQ", true);
    }

    // ------------------------------------------------------------------------------------------------ 111 y 115
    private static FormularioModelo M111()
    {
        string[] col = ["Nº de perceptores", "Importe de las percepciones", "Retenciones e ingresos a cuenta"];
        return new("111",
        [
            new("Rendimientos del trabajo", col,
            [
                F("Dinerarios", "01", "02", "03"),
                F("En especie", "04", "05", "06"),
            ]),
            new("Rendimientos de actividades económicas", col,
            [
                F("Dinerarios", "07", "08", "09"),
                F("En especie", "10", "11", "12"),
            ]),
            new("Otras retenciones", col,
            [
                F("Premios por juegos, concursos, rifas: dinerarios", "13", "14", "15"),
                F("Premios por juegos, concursos, rifas: en especie", "16", "17", "18"),
                F("Ganancias de aprovechamientos forestales: dinerarias", "19", "20", "21"),
                F("Ganancias de aprovechamientos forestales: en especie", "22", "23", "24"),
                F("Contraprestaciones por la cesión de derechos de imagen", "25", "26", "27"),
            ]),
            new("Total liquidación", ["Importe"],
            [
                T("Suma de retenciones e ingresos a cuenta", "28"),
                F("A deducir (solo autoliquidación complementaria)", "29"),
                T("Resultado a ingresar (28 − 29)", "30"),
            ]),
        ],
        [
            new("28", Suma("03", "06", "09", "12", "15", "18", "21", "24", "27")),
            new("30", [("28", 1), ("29", -1)]),
        ], "30", false);
    }

    private static FormularioModelo M115() => new("115",
    [
        new("Retenciones e ingresos a cuenta", ["Nº de perceptores", "Base de las retenciones", "Retenciones e ingresos a cuenta"],
        [
            F("Arrendamiento de inmuebles urbanos", "01", "02", "03"),
        ]),
        new("Liquidación", ["Importe"],
        [
            F("A deducir (solo autoliquidación complementaria)", "04"),
            T("Resultado a ingresar (03 − 04)", "05"),
        ]),
    ],
    [
        new("05", [("03", 1), ("04", -1)]),
    ], "05", false);

    // ------------------------------------------------------------------------------------------------ 130
    private static FormularioModelo M130() => new("130",
    [
        new("I. Actividades económicas en estimación directa (acumulado del año)", ["Importe"],
        [
            F("Ingresos computables", "01"),
            F("Gastos fiscalmente deducibles", "02"),
            T("Rendimiento neto (01 − 02)", "03"),
            T("20 % del rendimiento neto (si es positivo)", "04"),
            F("A deducir: pagos fraccionados de trimestres anteriores", "05"),
            F("A deducir: retenciones e ingresos a cuenta soportados", "06"),
            T("Pago fraccionado previo (04 − 05 − 06)", "07"),
        ]),
        new("II. Actividades agrícolas, ganaderas, forestales y pesqueras", ["Importe"],
        [
            F("Pago fraccionado previo del trimestre*", "11"),
        ]),
        new("III. Total liquidación", ["Importe"],
        [
            T("Suma de pagos fraccionados previos del trimestre (07 + 11)", "12"),
            F("A deducir: minoración por rendimientos netos bajos (art. 110.3.c RIRPF)", "13"),
            T("Diferencia (12 − 13)", "14"),
            F("A deducir: resultados negativos de trimestres anteriores", "15"),
            F("A deducir: pagos por préstamos para la vivienda habitual", "16"),
            T("Total (14 − 15 − 16)", "17"),
            F("A deducir: resultado a ingresar de la autoliquidación anterior (complementaria)", "18"),
            T("Resultado de la autoliquidación (17 − 18)", "19"),
        ]),
    ],
    [
        new("03", [("01", 1), ("02", -1)]),
        new("04", [], v => v("03") > 0 ? R(v("03") * 0.20m) : 0m),
        new("07", [("04", 1), ("05", -1), ("06", -1)]),
        new("12", [("07", 1), ("11", 1)]),
        new("14", [("12", 1), ("13", -1)]),
        new("17", [("14", 1), ("15", -1), ("16", -1)]),
        new("19", [("17", 1), ("18", -1)]),
    ], "19", false);

    // ------------------------------------------------------------------------------------------------ 309
    private static FormularioModelo M309() => new("309",
    [
        new("Régimen general", ["Base imponible", "Tipo %", "Cuota"],
        [
            F("Tipo 1", "01", "02", "03"),
            F("Tipo 2*", "25", "26", "27"),
            F("Tipo 3*", "04", "05", "06"),
            F("Tipo 4*", "07", "08", "09"),
        ]),
        new("Recargo de equivalencia", ["Base imponible", "Tipo %", "Cuota"],
        [
            F("Tipo 1*", "10", "11", "12"),
            F("Tipo 2*", "13", "14", "15"),
            F("Tipo 3*", "16", "17", "18"),
            F("Tipo 4*", "19", "20", "21"),
        ]),
        new("Liquidación", ["Importe"],
        [
            T("Total cuota devengada", "22"),
            F("A deducir (solo autoliquidación complementaria)", "23"),
            T("Resultado a ingresar (22 − 23)", "24"),
        ]),
    ],
    [
        new("22", Suma("03", "27", "06", "09", "12", "15", "18", "21")),
        new("24", [("22", 1), ("23", -1)]),
    ], "24", false);

    // ------------------------------------------------------------------------------------------------ resto de modelos
    /// <summary>Un apartado con las casillas en el orden del cálculo; sin totales: el resultado no cambia con las casillas escritas a mano.</summary>
    private static FormularioModelo Generico(string modelo, IReadOnlyCollection<string> presentes)
        => new(modelo, [new ApartadoFormulario("Casillas", ["Importe"], presentes.Select(c => F("", c)).ToList())], [], null, false);

    // ================================================================================================ recálculo

    /// <summary>
    /// Valores de las casillas a partir de lo calculado: suma los <paramref name="ajustes"/> (correcciones de periodos anteriores), pone
    /// las casillas escritas a mano y recalcula los totales que no se han escrito a mano.
    /// </summary>
    public static Dictionary<string, decimal> Recalcular(FormularioModelo f, IReadOnlyDictionary<string, decimal> calculadas,
        IReadOnlyDictionary<string, decimal> ajustes, IReadOnlyDictionary<string, decimal> manuales)
    {
        var v = new Dictionary<string, decimal>(calculadas);
        decimal Calc(string c) => calculadas.GetValueOrDefault(c);
        decimal Val(string c) => v.GetValueOrDefault(c);
        var totales = f.CasillasTotales.ToHashSet();
        foreach (var (c, d) in ajustes.Where(a => !totales.Contains(a.Key))) v[c] = Val(c) + d;
        foreach (var (c, m) in manuales.Where(a => !totales.Contains(a.Key))) v[c] = m;

        foreach (var t in f.Totales)
        {
            if (manuales.TryGetValue(t.Casilla, out var m)) { v[t.Casilla] = m; continue; }
            var nuevo = t.Funcion is not null ? t.Funcion(Val)
                : Calc(t.Casilla) + t.Terminos.Sum(x => x.Signo * (Val(x.Casilla) - Calc(x.Casilla)));
            if (nuevo != 0 || v.ContainsKey(t.Casilla)) v[t.Casilla] = R(nuevo);
        }
        return v;
    }

    /// <summary>Reparto del resultado: a ingresar si es positivo; si es negativo, a compensar (o a devolver) en los modelos que lo permiten.</summary>
    public static (decimal AIngresar, decimal ADevolver, decimal ACompensar) Repartir(FormularioModelo f, decimal resultado, bool devolucion)
        => resultado >= 0 ? (resultado, 0, 0)
           : !f.Compensable ? (0, 0, 0)
           : devolucion ? (0, -resultado, 0) : (0, 0, -resultado);

    // ================================================================================================ corrección en el periodo siguiente

    /// <summary>
    /// Casilla del periodo siguiente en la que se lleva la diferencia de una casilla de un periodo ya presentado. En el 303 y el 420, lo
    /// devengado va a «modificación de bases y cuotas» y lo deducible a «rectificación de deducciones»; en el resto, a la misma casilla.
    /// Devuelve null para las casillas que no se corrigen así (totales, tipos, compensaciones, información adicional).
    /// </summary>
    public static string? CasillaCorreccion(string modelo, string casilla)
    {
        string[] baseDev303 = ["150", "165", "01", "153", "04", "07", "10", "12", "14"], cuotaDev303 = ["152", "167", "03", "155", "06", "09", "11", "13", "15"];
        string[] baseRec303 = ["168", "16", "19", "156", "22", "25"], cuotaRec303 = ["170", "18", "21", "158", "24", "26"];
        string[] baseDed303 = ["28", "30", "32", "34", "36", "38", "40"], cuotaDed303 = ["29", "31", "33", "35", "37", "39", "41", "42", "43", "44"];
        return modelo switch
        {
            "303" => baseDev303.Contains(casilla) ? "14" : cuotaDev303.Contains(casilla) ? "15"
                   : baseRec303.Contains(casilla) ? "25" : cuotaRec303.Contains(casilla) ? "26"
                   : baseDed303.Contains(casilla) ? "40" : cuotaDed303.Contains(casilla) ? "41" : null,
            "420" => casilla is "TDEV" or "TDED" or "RES" or "PEND_ANT" or "COMP" or "LIQ" ? null
                   : (casilla.Length >= 3 && (casilla[0] is 'D' or 'R') && char.IsAsciiDigit(casilla[1])) || casilla is "ISPB" or "ISPC" or "MODB" or "MODC"
                       ? (casilla.EndsWith('B') ? "MODB" : "MODC")
                   : casilla is "SCB" or "SIB" or "ICB" or "IIB" or "RDB" ? "RDB"
                   : casilla is "SCC" or "SIC" or "ICC" or "IIC" or "RDC" or "RBI" or "REG" ? "RDC" : null,
            "111" => casilla is "28" or "29" or "30" ? null : casilla,
            "115" => casilla is "02" or "03" ? casilla : null,
            "309" => casilla is "22" or "23" or "24" or "HI" or "02" or "05" or "08" or "11" or "14" or "17" or "20" or "26" ? null : casilla,
            _ => null,
        };
    }

    /// <summary>Periodo siguiente (2T tras 1T, 01 del año siguiente tras 12…).</summary>
    public static (int Anio, string Periodo) Siguiente(int anio, string periodo)
        => periodo switch
        {
            "4T" => (anio + 1, "1T"),
            "12" => (anio + 1, "01"),
            ['1' or '2' or '3', 'T'] => (anio, $"{periodo[0] - '0' + 1}T"),
            _ when int.TryParse(periodo, out var m) && m is >= 1 and < 12 => (anio, (m + 1).ToString("D2")),
            _ => throw new ReglaContableException($"El periodo {periodo} no tiene un periodo siguiente en el que corregir."),
        };
}
