namespace Contabilidad.Domain.Fiscal;

/// <summary>Última presentación de un 303 del año: lo que el 390 recoge en su apartado 9 y en la compensación del ejercicio anterior.</summary>
public sealed record Resumen303(string Periodo, decimal Resultado, decimal AIngresar, decimal ADevolver, decimal ACompensar,
    decimal CompensacionPendienteInicial, decimal CompensacionAplicada, decimal PendientePosterior);

/// <summary>Prorrata del año para el apartado 12 (una por sector diferenciado).</summary>
public sealed record Prorrata390(string Tipo, decimal OperacionesTotales, decimal OperacionesConDerecho, int? PorcentajeDefinitivo);

public sealed record Entrada390(
    IReadOnlyList<GrupoIva> Grupos,
    decimal RegularizacionBienesInversion,
    decimal RegularizacionProrrata,
    IReadOnlyList<Resumen303> Autoliquidaciones,
    InformacionCaja? Caja,
    IReadOnlyList<Prorrata390> Prorratas,
    bool RecargoEquivalencia = false);

/// <summary>
/// Modelo 390, declaración-resumen anual del IVA, con la numeración de casillas del modelo oficial (diseño de 2025): devengado
/// por tipo y clase de operación (régimen ordinario, criterio de caja, adquisiciones intracomunitarias de bienes y de servicios,
/// inversión del sujeto pasivo, modificaciones, recargo de equivalencia), deducible por tipo (interiores corrientes y bienes de
/// inversión, importaciones, adquisiciones intracomunitarias), resultado, resultados de las autoliquidaciones del año, volumen de
/// operaciones, operaciones específicas, criterio de caja y prorrata.
/// </summary>
public static class Calculador390
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    // Casillas (base, cuota) por tipo: 0, 2, 4, 5, 7,5, 10 y 21 %
    private static readonly Dictionary<string, Dictionary<decimal, (string B, string C)>> Devengado = new()
    {
        ["ORDINARIO"] = new() { [0m] = ("700", "701"), [2m] = ("667", "668"), [4m] = ("01", "02"), [5m] = ("702", "703"), [7.5m] = ("669", "670"),
                                [10m] = ("03", "04"), [21m] = ("05", "06") },
        ["CAJA"] = new() { [0m] = ("708", "709"), [2m] = ("675", "676"), [4m] = ("643", "644"), [5m] = ("710", "711"), [7.5m] = ("677", "678"),
                           [10m] = ("645", "646"), [21m] = ("647", "648") },
        ["INTRA_BIENES"] = new() { [0m] = ("716", "717"), [2m] = ("683", "684"), [4m] = ("21", "22"), [5m] = ("718", "719"), [7.5m] = ("685", "686"),
                                   [10m] = ("23", "24"), [21m] = ("25", "26") },
        ["INTRA_SERVICIOS"] = new() { [0m] = ("720", "721"), [2m] = ("687", "688"), [4m] = ("545", "546"), [5m] = ("722", "723"),
                                      [7.5m] = ("689", "690"), [10m] = ("547", "548"), [21m] = ("551", "552") },
    };

    private static readonly Dictionary<decimal, (string B, string C)> Recargos = new()
    {
        [0m] = ("663", "664"), [0.26m] = ("691", "692"), [0.5m] = ("35", "36"), [0.62m] = ("665", "666"), [1m] = ("693", "694"),
        [1.4m] = ("599", "600"), [5.2m] = ("601", "602"), [1.75m] = ("41", "42"),
    };

    // Deducible por tipo (2, 4, 5, 7,5, 10, 21 %) y total del bloque
    private static readonly Dictionary<string, (Dictionary<decimal, (string B, string C)> Tipos, string TB, string TC)> Deducible = new()
    {
        ["INTERIOR_CORRIENTE"] = (new() { [2m] = ("695", "696"), [4m] = ("190", "191"), [5m] = ("724", "725"), [7.5m] = ("697", "698"),
                                          [10m] = ("603", "604"), [21m] = ("605", "606") }, "48", "49"),
        ["INTERIOR_INVERSION"] = (new() { [2m] = ("749", "750"), [4m] = ("196", "197"), [5m] = ("728", "729"), [7.5m] = ("751", "752"),
                                          [10m] = ("611", "612"), [21m] = ("613", "614") }, "50", "51"),
        ["IMPORTACION_CORRIENTE"] = (new() { [2m] = ("757", "758"), [4m] = ("202", "203"), [5m] = ("732", "733"), [7.5m] = ("759", "760"),
                                             [10m] = ("619", "620"), [21m] = ("621", "622") }, "52", "53"),
        ["IMPORTACION_INVERSION"] = (new() { [2m] = ("761", "762"), [4m] = ("208", "209"), [5m] = ("734", "735"), [7.5m] = ("763", "764"),
                                             [10m] = ("623", "624"), [21m] = ("625", "626") }, "54", "55"),
        ["INTRA_CORRIENTE"] = (new() { [2m] = ("765", "766"), [4m] = ("214", "215"), [5m] = ("736", "737"), [7.5m] = ("767", "768"),
                                       [10m] = ("627", "628"), [21m] = ("629", "630") }, "56", "57"),
        ["INTRA_INVERSION"] = (new() { [2m] = ("769", "770"), [4m] = ("220", "221"), [5m] = ("738", "739"), [7.5m] = ("771", "772"),
                                       [10m] = ("631", "632"), [21m] = ("633", "634") }, "58", "59"),
        ["INTRA_SERVICIOS"] = (new() { [2m] = ("773", "774"), [4m] = ("587", "588"), [5m] = ("740", "741"), [7.5m] = ("775", "776"),
                                       [10m] = ("635", "636"), [21m] = ("637", "638") }, "597", "598"),
    };

    public static void Calcular(Entrada390 e, DeclaracionCalculada d)
    {
        var Siempre = new HashSet<string>(SiempreBase);
        var importes = new Dictionary<string, decimal>();
        void Suma(string clave, decimal v) => importes[clave] = importes.GetValueOrDefault(clave) + v;
        decimal I(string clave) => importes.GetValueOrDefault(clave);

        var emitidas = e.Grupos.Where(g => g.Libro == "EMITIDAS").ToList();
        var recibidas = e.Grupos.Where(g => g.Libro == "RECIBIDAS").ToList();
        var sinCasilla = new HashSet<string>();

        // ---------------------------------------------------------------- 5. IVA devengado
        void PorTipo(string bloque, IEnumerable<GrupoIva> gs)
        {
            foreach (var g in gs.GroupBy(x => x.Porcentaje))
            {
                if (!Devengado[bloque].TryGetValue(g.Key, out var k)) { sinCasilla.Add($"{g.Key:0.##} %"); continue; }
                Suma(k.B, g.Sum(x => x.Base)); Suma(k.C, g.Sum(x => x.Cuota));
            }
        }
        var interiorDevengado = emitidas.Where(g => g.TipoOperacion == "INTERIOR" && !g.Rectificativa).ToList();
        PorTipo("ORDINARIO", interiorDevengado.Where(g => !g.CriterioCaja));
        PorTipo("CAJA", interiorDevengado.Where(g => g.CriterioCaja));
        PorTipo("INTRA_BIENES", recibidas.Where(g => g.TipoOperacion == "INTRACOM_ADQUISICION"));
        PorTipo("INTRA_SERVICIOS", recibidas.Where(g => g.TipoOperacion == "INTRACOM_SERVICIOS"));
        var isp = recibidas.Where(g => g.TipoOperacion == "INVERSION_SUJETO_PASIVO").ToList();
        Suma("27", isp.Sum(g => g.Base)); Suma("28", isp.Sum(g => g.Cuota));
        var rectEmitidas = emitidas.Where(g => g.TipoOperacion == "INTERIOR" && g.Rectificativa).ToList();
        Suma("29", rectEmitidas.Sum(g => g.Base)); Suma("30", rectEmitidas.Sum(g => g.Cuota));

        string[] bases = ["700", "667", "01", "702", "669", "03", "05", "708", "675", "643", "710", "677", "645", "647",
                          "716", "683", "21", "718", "685", "23", "25", "720", "687", "545", "722", "689", "547", "551", "27", "29"];
        string[] cuotas = ["701", "668", "02", "703", "670", "04", "06", "709", "676", "644", "711", "678", "646", "648",
                           "717", "684", "22", "719", "686", "24", "26", "721", "688", "546", "723", "690", "548", "552", "28", "30"];
        Suma("33", bases.Sum(I)); Suma("34", cuotas.Sum(I));

        foreach (var g in interiorDevengado.Where(x => x.PorcentajeRecargo != 0 || x.CuotaRecargo != 0).GroupBy(x => x.PorcentajeRecargo))
        {
            if (!Recargos.TryGetValue(g.Key, out var k)) { sinCasilla.Add($"recargo {g.Key:0.##} %"); continue; }
            Suma(k.B, g.Sum(x => x.Base)); Suma(k.C, g.Sum(x => x.CuotaRecargo));
        }
        var rectRecargo = rectEmitidas.Where(g => g.CuotaRecargo != 0).ToList();
        Suma("43", rectRecargo.Sum(g => g.Base)); Suma("44", rectRecargo.Sum(g => g.CuotaRecargo));
        Suma("47", I("34") + Recargos.Values.Sum(k => I(k.C)) + I("44"));

        // ---------------------------------------------------------------- 5. IVA deducible
        var soportado = recibidas.Where(g => !g.Rectificativa && g.TipoOperacion is not ("EXENTA" or "NO_SUJETA")).ToList();
        string BloqueDeducible(GrupoIva g) => g.TipoOperacion switch
        {
            "IMPORTACION" => g.EsBienInversion ? "IMPORTACION_INVERSION" : "IMPORTACION_CORRIENTE",
            "INTRACOM_ADQUISICION" => g.EsBienInversion ? "INTRA_INVERSION" : "INTRA_CORRIENTE",
            "INTRACOM_SERVICIOS" => "INTRA_SERVICIOS",
            _ => g.EsBienInversion ? "INTERIOR_INVERSION" : "INTERIOR_CORRIENTE",
        };
        foreach (var g in soportado.GroupBy(x => (Bloque: BloqueDeducible(x), x.Porcentaje)))
        {
            var (tipos, tb, tc) = Deducible[g.Key.Bloque];
            if (!tipos.TryGetValue(g.Key.Porcentaje, out var k))
            {
                if (g.Sum(x => x.CuotaDeducible) != 0) sinCasilla.Add($"deducible al {g.Key.Porcentaje:0.##} %");
                continue;
            }
            Suma(k.B, g.Sum(x => x.Base)); Suma(k.C, g.Sum(x => x.CuotaDeducible));
            Suma(tb, g.Sum(x => x.Base)); Suma(tc, g.Sum(x => x.CuotaDeducible));
        }
        var rectRecibidas = recibidas.Where(g => g.Rectificativa && g.TipoOperacion is not ("EXENTA" or "NO_SUJETA")).ToList();
        Suma("639", rectRecibidas.Sum(g => g.Base)); Suma("62", rectRecibidas.Sum(g => g.CuotaDeducible));
        Suma("63", e.RegularizacionBienesInversion);
        Suma("522", e.RegularizacionProrrata);
        Suma("64", I("49") + I("51") + I("53") + I("55") + I("57") + I("59") + I("598") + I("61") + I("62") + I("63") + I("522"));
        Suma("65", I("47") - I("64"));

        // ---------------------------------------------------------------- 7. Resultado de la liquidación anual
        var porPeriodo = e.Autoliquidaciones.ToList();
        var primero = porPeriodo.FirstOrDefault(x => x.Periodo is "1T" or "01");
        var aplicadas = porPeriodo.Sum(x => x.CompensacionAplicada);
        // Compensación de cuotas de ejercicios anteriores aplicada en el año: como mucho lo pendiente al empezar el año
        var compensacionAnterior = primero is null ? aplicadas : Math.Min(aplicadas, primero.CompensacionPendienteInicial);
        Suma("658", 0m);
        Suma("84", I("65") + I("658"));
        Suma("85", compensacionAnterior);
        Suma("86", I("84") - I("85"));

        // ---------------------------------------------------------------- 9. Resultado de las liquidaciones
        var ultimo = porPeriodo.FirstOrDefault(x => x.Periodo is "4T" or "12");
        Suma("95", porPeriodo.Sum(x => x.AIngresar));
        Suma("97", ultimo?.ACompensar ?? 0m);
        Suma("98", ultimo?.ADevolver ?? 0m);
        Suma("662", ultimo?.PendientePosterior ?? 0m);

        // ---------------------------------------------------------------- 10. Volumen de operaciones
        var interiores = emitidas.Where(g => g.TipoOperacion == "INTERIOR").ToList();
        Suma(e.RecargoEquivalencia ? "102" : "99", interiores.Where(g => !g.CriterioCaja).Sum(g => g.Base));
        Suma("653", interiores.Where(g => g.CriterioCaja).Sum(g => g.Base));
        Suma("103", emitidas.Where(g => g.TipoOperacion == "INTRACOM_ENTREGA").Sum(g => g.Base));
        Suma("104", emitidas.Where(g => g.TipoOperacion == "EXPORTACION").Sum(g => g.Base));
        Suma("105", emitidas.Where(g => g.TipoOperacion == "EXENTA").Sum(g => g.Base));
        Suma("110", emitidas.Where(g => g.TipoOperacion == "NO_SUJETA").Sum(g => g.Base));
        Suma("125", emitidas.Where(g => g.TipoOperacion == "INVERSION_SUJETO_PASIVO").Sum(g => g.Base));
        Suma("107", emitidas.Where(g => g.EsBienInversion).Sum(g => g.Base));
        Suma("108", I("99") + I("653") + I("103") + I("104") + I("105") + I("110") + I("125") + I("102") - I("106") - I("107"));

        // ---------------------------------------------------------------- 11. Operaciones específicas
        Suma("230", recibidas.Where(g => g.TipoOperacion == "EXENTA").Sum(g => g.Base));
        Suma("232", recibidas.Where(g => g.TipoOperacion is not ("EXENTA" or "NO_SUJETA") && g.Cuota != 0 && g.CuotaDeducible == 0).Sum(g => g.Base));
        if (e.Caja is { } cj)
        {
            Suma("654", cj.BaseEntregas); Suma("655", cj.CuotaEntregas);
            Suma("656", cj.BaseAdquisiciones); Suma("657", cj.CuotaAdquisiciones);
        }

        // ---------------------------------------------------------------- 12. Prorrata
        // Dos primeras prorratas (115-118 y 120-123); el tipo va como 0 general / 1 especial y en el fichero como G / E
        var claves = new[] { ("115", "116", "117", "118"), ("120", "121", "122", "123") };
        var prorratas = e.Prorratas.Take(2).ToList();
        for (var i = 0; i < prorratas.Count; i++)
        {
            var p = prorratas[i];
            Suma(claves[i].Item1, p.OperacionesTotales);
            Suma(claves[i].Item2, p.OperacionesConDerecho);
            Suma(claves[i].Item3, p.Tipo == "ESPECIAL" ? 1 : 0);
            Suma(claves[i].Item4, p.PorcentajeDefinitivo ?? 0);
            Siempre.Add(claves[i].Item3);
        }
        if (e.Prorratas.Count > 2)
            d.Avisos.Add("Hay más de dos sectores con prorrata: el apartado 12 recoge los dos primeros; completa el resto en el formulario.");

        foreach (var (clave, desc) in Descripciones)
            if (importes.TryGetValue(clave, out var v) && (v != 0 || Siempre.Contains(clave)))
                d.Casillas.Add(new Casilla(clave, desc, R(v)));

        if (sinCasilla.Count > 0)
            throw new ReglaContableException($"Hay operaciones sin casilla en el 390: {string.Join(", ", sinCasilla)}.");

        d.Resultado = 0; d.AIngresar = d.ADevolver = d.ACompensar = 0;
        if (porPeriodo.Count == 0) d.Avisos.Add($"No hay autoliquidaciones 303 presentadas del año: el apartado 9 va a cero.");
        else
        {
            var suma303 = porPeriodo.Sum(x => x.Resultado + x.CompensacionAplicada);   // resultado de cada periodo antes de compensar
            d.Avisos.Add(suma303 == R(I("65"))
                ? $"El resultado del régimen general [65] cuadra con las {porPeriodo.Count} autoliquidaciones presentadas."
                : $"El resultado del régimen general [65] ({I("65"):N2}) no cuadra con las autoliquidaciones presentadas ({suma303:N2}): hay facturas contabilizadas después de presentar o periodos sin presentar.");
        }
        d.Avisos.Add("Casillas con la numeración del modelo oficial 390. Los regímenes especiales (simplificado, agricultura, bienes usados, agencias de viajes), las operaciones intragrupo y la tributación por territorio van a cero.");
    }

    private static readonly HashSet<string> SiempreBase = ["33", "34", "47", "64", "65", "84", "85", "86", "95", "97", "98", "662", "108"];

    private static readonly (string Clave, string Desc)[] Descripciones =
    [
        ("700", "Régimen ordinario 0 %: base"), ("701", "Régimen ordinario 0 %: cuota"),
        ("667", "Régimen ordinario 2 %: base"), ("668", "Régimen ordinario 2 %: cuota"),
        ("01", "Régimen ordinario 4 %: base"), ("02", "Régimen ordinario 4 %: cuota"),
        ("702", "Régimen ordinario 5 %: base"), ("703", "Régimen ordinario 5 %: cuota"),
        ("669", "Régimen ordinario 7,5 %: base"), ("670", "Régimen ordinario 7,5 %: cuota"),
        ("03", "Régimen ordinario 10 %: base"), ("04", "Régimen ordinario 10 %: cuota"),
        ("05", "Régimen ordinario 21 %: base"), ("06", "Régimen ordinario 21 %: cuota"),
        ("708", "Criterio de caja 0 %: base"), ("709", "Criterio de caja 0 %: cuota"),
        ("675", "Criterio de caja 2 %: base"), ("676", "Criterio de caja 2 %: cuota"),
        ("643", "Criterio de caja 4 %: base"), ("644", "Criterio de caja 4 %: cuota"),
        ("710", "Criterio de caja 5 %: base"), ("711", "Criterio de caja 5 %: cuota"),
        ("677", "Criterio de caja 7,5 %: base"), ("678", "Criterio de caja 7,5 %: cuota"),
        ("645", "Criterio de caja 10 %: base"), ("646", "Criterio de caja 10 %: cuota"),
        ("647", "Criterio de caja 21 %: base"), ("648", "Criterio de caja 21 %: cuota"),
        ("716", "Adquisiciones intracomunitarias de bienes 0 %: base"), ("717", "Adquisiciones intracomunitarias de bienes 0 %: cuota"),
        ("683", "Adquisiciones intracomunitarias de bienes 2 %: base"), ("684", "Adquisiciones intracomunitarias de bienes 2 %: cuota"),
        ("21", "Adquisiciones intracomunitarias de bienes 4 %: base"), ("22", "Adquisiciones intracomunitarias de bienes 4 %: cuota"),
        ("718", "Adquisiciones intracomunitarias de bienes 5 %: base"), ("719", "Adquisiciones intracomunitarias de bienes 5 %: cuota"),
        ("685", "Adquisiciones intracomunitarias de bienes 7,5 %: base"), ("686", "Adquisiciones intracomunitarias de bienes 7,5 %: cuota"),
        ("23", "Adquisiciones intracomunitarias de bienes 10 %: base"), ("24", "Adquisiciones intracomunitarias de bienes 10 %: cuota"),
        ("25", "Adquisiciones intracomunitarias de bienes 21 %: base"), ("26", "Adquisiciones intracomunitarias de bienes 21 %: cuota"),
        ("720", "Adquisiciones intracomunitarias de servicios 0 %: base"), ("721", "Adquisiciones intracomunitarias de servicios 0 %: cuota"),
        ("687", "Adquisiciones intracomunitarias de servicios 2 %: base"), ("688", "Adquisiciones intracomunitarias de servicios 2 %: cuota"),
        ("545", "Adquisiciones intracomunitarias de servicios 4 %: base"), ("546", "Adquisiciones intracomunitarias de servicios 4 %: cuota"),
        ("722", "Adquisiciones intracomunitarias de servicios 5 %: base"), ("723", "Adquisiciones intracomunitarias de servicios 5 %: cuota"),
        ("689", "Adquisiciones intracomunitarias de servicios 7,5 %: base"), ("690", "Adquisiciones intracomunitarias de servicios 7,5 %: cuota"),
        ("547", "Adquisiciones intracomunitarias de servicios 10 %: base"), ("548", "Adquisiciones intracomunitarias de servicios 10 %: cuota"),
        ("551", "Adquisiciones intracomunitarias de servicios 21 %: base"), ("552", "Adquisiciones intracomunitarias de servicios 21 %: cuota"),
        ("27", "IVA devengado por inversión del sujeto pasivo: base"), ("28", "IVA devengado por inversión del sujeto pasivo: cuota"),
        ("29", "Modificación de bases y cuotas: base"), ("30", "Modificación de bases y cuotas: cuota"),
        ("33", "Total bases imponibles"), ("34", "Total cuotas devengadas"),
        ("663", "Recargo de equivalencia 0 %: base"), ("664", "Recargo de equivalencia 0 %: cuota"),
        ("691", "Recargo de equivalencia 0,26 %: base"), ("692", "Recargo de equivalencia 0,26 %: cuota"),
        ("35", "Recargo de equivalencia 0,5 %: base"), ("36", "Recargo de equivalencia 0,5 %: cuota"),
        ("665", "Recargo de equivalencia 0,62 %: base"), ("666", "Recargo de equivalencia 0,62 %: cuota"),
        ("693", "Recargo de equivalencia 1 %: base"), ("694", "Recargo de equivalencia 1 %: cuota"),
        ("599", "Recargo de equivalencia 1,4 %: base"), ("600", "Recargo de equivalencia 1,4 %: cuota"),
        ("601", "Recargo de equivalencia 5,2 %: base"), ("602", "Recargo de equivalencia 5,2 %: cuota"),
        ("41", "Recargo de equivalencia 1,75 %: base"), ("42", "Recargo de equivalencia 1,75 %: cuota"),
        ("43", "Modificación de recargo de equivalencia: base"), ("44", "Modificación de recargo de equivalencia: cuota"),
        ("47", "Total cuotas de IVA y recargo de equivalencia"),
        ("695", "Interiores corrientes 2 %: base"), ("696", "Interiores corrientes 2 %: cuota deducible"),
        ("190", "Interiores corrientes 4 %: base"), ("191", "Interiores corrientes 4 %: cuota deducible"),
        ("724", "Interiores corrientes 5 %: base"), ("725", "Interiores corrientes 5 %: cuota deducible"),
        ("697", "Interiores corrientes 7,5 %: base"), ("698", "Interiores corrientes 7,5 %: cuota deducible"),
        ("603", "Interiores corrientes 10 %: base"), ("604", "Interiores corrientes 10 %: cuota deducible"),
        ("605", "Interiores corrientes 21 %: base"), ("606", "Interiores corrientes 21 %: cuota deducible"),
        ("48", "Total interiores corrientes: base"), ("49", "Total interiores corrientes: cuota deducible"),
        ("749", "Interiores bienes de inversión 2 %: base"), ("750", "Interiores bienes de inversión 2 %: cuota deducible"),
        ("196", "Interiores bienes de inversión 4 %: base"), ("197", "Interiores bienes de inversión 4 %: cuota deducible"),
        ("728", "Interiores bienes de inversión 5 %: base"), ("729", "Interiores bienes de inversión 5 %: cuota deducible"),
        ("751", "Interiores bienes de inversión 7,5 %: base"), ("752", "Interiores bienes de inversión 7,5 %: cuota deducible"),
        ("611", "Interiores bienes de inversión 10 %: base"), ("612", "Interiores bienes de inversión 10 %: cuota deducible"),
        ("613", "Interiores bienes de inversión 21 %: base"), ("614", "Interiores bienes de inversión 21 %: cuota deducible"),
        ("50", "Total interiores bienes de inversión: base"), ("51", "Total interiores bienes de inversión: cuota deducible"),
        ("757", "Importaciones corrientes 2 %: base"), ("758", "Importaciones corrientes 2 %: cuota deducible"),
        ("202", "Importaciones corrientes 4 %: base"), ("203", "Importaciones corrientes 4 %: cuota deducible"),
        ("732", "Importaciones corrientes 5 %: base"), ("733", "Importaciones corrientes 5 %: cuota deducible"),
        ("759", "Importaciones corrientes 7,5 %: base"), ("760", "Importaciones corrientes 7,5 %: cuota deducible"),
        ("619", "Importaciones corrientes 10 %: base"), ("620", "Importaciones corrientes 10 %: cuota deducible"),
        ("621", "Importaciones corrientes 21 %: base"), ("622", "Importaciones corrientes 21 %: cuota deducible"),
        ("52", "Total importaciones corrientes: base"), ("53", "Total importaciones corrientes: cuota deducible"),
        ("761", "Importaciones bienes de inversión 2 %: base"), ("762", "Importaciones bienes de inversión 2 %: cuota deducible"),
        ("208", "Importaciones bienes de inversión 4 %: base"), ("209", "Importaciones bienes de inversión 4 %: cuota deducible"),
        ("734", "Importaciones bienes de inversión 5 %: base"), ("735", "Importaciones bienes de inversión 5 %: cuota deducible"),
        ("763", "Importaciones bienes de inversión 7,5 %: base"), ("764", "Importaciones bienes de inversión 7,5 %: cuota deducible"),
        ("623", "Importaciones bienes de inversión 10 %: base"), ("624", "Importaciones bienes de inversión 10 %: cuota deducible"),
        ("625", "Importaciones bienes de inversión 21 %: base"), ("626", "Importaciones bienes de inversión 21 %: cuota deducible"),
        ("54", "Total importaciones bienes de inversión: base"), ("55", "Total importaciones bienes de inversión: cuota deducible"),
        ("765", "Adq. intracomunitarias corrientes 2 %: base"), ("766", "Adq. intracomunitarias corrientes 2 %: cuota deducible"),
        ("214", "Adq. intracomunitarias corrientes 4 %: base"), ("215", "Adq. intracomunitarias corrientes 4 %: cuota deducible"),
        ("736", "Adq. intracomunitarias corrientes 5 %: base"), ("737", "Adq. intracomunitarias corrientes 5 %: cuota deducible"),
        ("767", "Adq. intracomunitarias corrientes 7,5 %: base"), ("768", "Adq. intracomunitarias corrientes 7,5 %: cuota deducible"),
        ("627", "Adq. intracomunitarias corrientes 10 %: base"), ("628", "Adq. intracomunitarias corrientes 10 %: cuota deducible"),
        ("629", "Adq. intracomunitarias corrientes 21 %: base"), ("630", "Adq. intracomunitarias corrientes 21 %: cuota deducible"),
        ("56", "Total adq. intracomunitarias corrientes: base"), ("57", "Total adq. intracomunitarias corrientes: cuota deducible"),
        ("769", "Adq. intracomunitarias bienes de inversión 2 %: base"), ("770", "Adq. intracomunitarias bienes de inversión 2 %: cuota deducible"),
        ("220", "Adq. intracomunitarias bienes de inversión 4 %: base"), ("221", "Adq. intracomunitarias bienes de inversión 4 %: cuota deducible"),
        ("738", "Adq. intracomunitarias bienes de inversión 5 %: base"), ("739", "Adq. intracomunitarias bienes de inversión 5 %: cuota deducible"),
        ("771", "Adq. intracomunitarias bienes de inversión 7,5 %: base"), ("772", "Adq. intracomunitarias bienes de inversión 7,5 %: cuota deducible"),
        ("631", "Adq. intracomunitarias bienes de inversión 10 %: base"), ("632", "Adq. intracomunitarias bienes de inversión 10 %: cuota deducible"),
        ("633", "Adq. intracomunitarias bienes de inversión 21 %: base"), ("634", "Adq. intracomunitarias bienes de inversión 21 %: cuota deducible"),
        ("58", "Total adq. intracomunitarias bienes de inversión: base"), ("59", "Total adq. intracomunitarias bienes de inversión: cuota deducible"),
        ("773", "Adq. intracomunitarias de servicios 2 %: base"), ("774", "Adq. intracomunitarias de servicios 2 %: cuota deducible"),
        ("587", "Adq. intracomunitarias de servicios 4 %: base"), ("588", "Adq. intracomunitarias de servicios 4 %: cuota deducible"),
        ("740", "Adq. intracomunitarias de servicios 5 %: base"), ("741", "Adq. intracomunitarias de servicios 5 %: cuota deducible"),
        ("775", "Adq. intracomunitarias de servicios 7,5 %: base"), ("776", "Adq. intracomunitarias de servicios 7,5 %: cuota deducible"),
        ("635", "Adq. intracomunitarias de servicios 10 %: base"), ("636", "Adq. intracomunitarias de servicios 10 %: cuota deducible"),
        ("637", "Adq. intracomunitarias de servicios 21 %: base"), ("638", "Adq. intracomunitarias de servicios 21 %: cuota deducible"),
        ("597", "Total adq. intracomunitarias de servicios: base"), ("598", "Total adq. intracomunitarias de servicios: cuota deducible"),
        ("639", "Rectificación de deducciones: base"), ("62", "Rectificación de deducciones: cuota"),
        ("63", "Regularización de bienes de inversión"),
        ("522", "Regularización por aplicación del porcentaje definitivo de prorrata"),
        ("64", "Suma de deducciones"), ("65", "Resultado régimen general (47 − 64)"),
        ("658", "Regularización de cuotas del art. 80.Cinco.5ª LIVA"), ("84", "Suma de resultados (65 + 658)"),
        ("85", "Compensación de cuotas del ejercicio anterior"), ("86", "Resultado de la liquidación (84 − 85)"),
        ("95", "Total resultados a ingresar en las autoliquidaciones del ejercicio"),
        ("97", "Resultado de la autoliquidación del último periodo: a compensar"),
        ("98", "Resultado de la autoliquidación del último periodo: a devolver"),
        ("662", "Cuotas pendientes de compensación al término del ejercicio"),
        ("99", "Volumen de operaciones: régimen general"), ("653", "Volumen de operaciones: criterio de caja"),
        ("103", "Volumen de operaciones: entregas intracomunitarias"),
        ("104", "Volumen de operaciones: exportaciones y otras exentas con derecho a deducción"),
        ("105", "Volumen de operaciones: exentas sin derecho a deducción"),
        ("110", "Volumen de operaciones: no sujetas por reglas de localización"),
        ("125", "Volumen de operaciones: sujetas con inversión del sujeto pasivo"),
        ("102", "Volumen de operaciones: régimen especial del recargo de equivalencia"),
        ("107", "Entregas de bienes de inversión (no forman parte del volumen)"),
        ("108", "Total volumen de operaciones"),
        ("230", "Adquisiciones interiores exentas"), ("232", "Bases imponibles del IVA soportado no deducible"),
        ("654", "Criterio de caja: entregas (base)"), ("655", "Criterio de caja: entregas (cuota)"),
        ("656", "Criterio de caja: adquisiciones (base)"), ("657", "Criterio de caja: adquisiciones (cuota)"),
        ("115", "Prorrata 1: importe total de operaciones"), ("116", "Prorrata 1: operaciones con derecho a deducción"),
        ("117", "Prorrata 1: tipo (0 general, 1 especial)"), ("118", "Prorrata 1: % definitivo"),
        ("120", "Prorrata 2: importe total de operaciones"), ("121", "Prorrata 2: operaciones con derecho a deducción"),
        ("122", "Prorrata 2: tipo (0 general, 1 especial)"), ("123", "Prorrata 2: % definitivo"),
    ];
}
