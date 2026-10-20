namespace Contabilidad.Domain.Balances;

/// <summary>Movimientos de una cuenta en el ejercicio (sin apertura, regularización ni cierre) por origen del asiento.</summary>
public sealed record MovimientoOrigen(string Codigo, string Origen, decimal Debe, decimal Haber)
{
    /// <summary>Haber − debe: positivo para ingresos y aumentos de patrimonio neto o pasivo.</summary>
    public decimal Acreedor => Haber - Debe;
}

public sealed record LineaEstado(string Id, string Texto, int Nivel, bool EsTotal, decimal Importe);

public sealed record ColumnaEcpn(string Id, string Texto);
public sealed record FilaEcpn(string Id, string Texto, bool EsTotal, IReadOnlyDictionary<string, decimal> Importes);

/// <summary>Estado de cambios en el patrimonio neto: A) ingresos y gastos reconocidos; B) cambios por columnas del patrimonio neto.</summary>
public sealed record EcpnCalculado(IReadOnlyList<LineaEstado> ParteA, IReadOnlyList<ColumnaEcpn> Columnas, IReadOnlyList<FilaEcpn> ParteB,
    IReadOnlyList<string> Avisos);

/// <summary>
/// Estado de cambios en el patrimonio neto (PGC, tercera parte). Se calcula con los saldos de apertura y los movimientos del ejercicio:
/// los grupos 8 y 9 (PGC normal) o, si no se usan, los abonos y cargos de las cuentas 130-132 dan los ingresos y gastos imputados al
/// patrimonio neto y sus transferencias; las operaciones con socios y la distribución del resultado salen de las cuentas de fondos propios.
/// </summary>
public static class GeneradorEcpn
{
    private static bool Empieza(string codigo, string prefijos) => prefijos.Split(' ').Any(p => codigo.StartsWith(p, StringComparison.Ordinal));

    private static readonly (string Id, string Texto, string Cuentas)[] Imputados =
    [
        ("I", "I. Por valoración de instrumentos financieros", "800 900"),
        ("II", "II. Por coberturas de flujos de efectivo", "810 811 910 911"),
        ("III", "III. Subvenciones, donaciones y legados recibidos", "940 941 942"),
        ("IV", "IV. Por ganancias y pérdidas actuariales y otros ajustes", "850 851 950 951"),
        ("V", "V. Por activos no corrientes y pasivos vinculados, mantenidos para la venta", "860 960"),
        ("VI", "VI. Diferencias de conversión", "820 920"),
        ("VII", "VII. Efecto impositivo", "830 833 834 835 838"),
    ];
    private static readonly (string Id, string Texto, string Cuentas)[] Transferencias =
    [
        ("VIII", "VIII. Por valoración de instrumentos financieros", "802 902 891 892 991 992 993 994"),
        ("IX", "IX. Por coberturas de flujos de efectivo", "812 813 912 913"),
        ("X", "X. Subvenciones, donaciones y legados recibidos", "840 841 842"),
        ("XI", "XI. Por activos no corrientes y pasivos vinculados, mantenidos para la venta", "862 962"),
        ("XII", "XII. Diferencias de conversión", "821 921"),
        ("XIII", "XIII. Efecto impositivo", "836 837"),
    ];

    public static readonly IReadOnlyList<ColumnaEcpn> Columnas =
    [
        new("CAP", "Capital escriturado"), new("NOEX", "(Capital no exigido)"), new("PRIMA", "Prima de emisión"), new("RES", "Reservas"),
        new("PROP", "(Acciones propias)"), new("ANT", "Resultados de ejercicios anteriores"), new("APORT", "Otras aportaciones de socios"),
        new("RDO", "Resultado del ejercicio"), new("DIVC", "(Dividendo a cuenta)"), new("OTROS", "Otros instrumentos de patrimonio"),
        new("AJ", "Ajustes por cambios de valor"), new("SUBV", "Subvenciones, donaciones y legados"), new("TOTAL", "TOTAL"),
    ];

    /// <summary>Columna del estado B a la que pertenece una cuenta (null si no es de patrimonio neto).</summary>
    public static string? Columna(string c)
    {
        if (Empieza(c, "1030 1040")) return "NOEX";
        if (Empieza(c, "100 101 102")) return "CAP";
        if (Empieza(c, "110")) return "PRIMA";
        if (Empieza(c, "111")) return "OTROS";
        if (Empieza(c, "112 113 114 115 119")) return "RES";
        if (Empieza(c, "108 109")) return "PROP";
        if (Empieza(c, "118")) return "APORT";
        if (Empieza(c, "120 121")) return "ANT";
        if (Empieza(c, "129")) return "RDO";
        if (Empieza(c, "557")) return "DIVC";
        if (Empieza(c, "130 131 132 84 94 830 833 838")) return "SUBV";
        if (Empieza(c, "133 134 135 136 137 8 9")) return "AJ";
        return null;
    }

    public static EcpnCalculado Calcular(IReadOnlyDictionary<string, decimal> saldosIniciales, IReadOnlyList<MovimientoOrigen> movimientos,
        decimal resultadoEjercicio)
    {
        var avisos = new List<string>();
        var en89 = movimientos.Where(m => m.Codigo[0] is '8' or '9').ToList();
        var usa89 = en89.Count > 0;
        decimal Suma(string cuentas) => en89.Where(m => Empieza(m.Codigo, cuentas)).Sum(m => m.Acreedor);

        // ---- A) Ingresos y gastos reconocidos
        var a = new List<LineaEstado> { new("A", "A) Resultado de la cuenta de pérdidas y ganancias", 0, true, resultadoEjercicio) };
        decimal totB, totC;
        if (usa89)
        {
            a.Add(new("TB", "Ingresos y gastos imputados directamente al patrimonio neto", 0, false, 0));
            var b = Imputados.Select(i => new LineaEstado(i.Id, i.Texto, 1, false, Suma(i.Cuentas))).ToList();
            totB = b.Sum(x => x.Importe);
            a.AddRange(b); a.Add(new("B", "B) Total ingresos y gastos imputados directamente en el patrimonio neto (I a VII)", 0, true, totB));
            a.Add(new("TC", "Transferencias a la cuenta de pérdidas y ganancias", 0, false, 0));
            var c = Transferencias.Select(i => new LineaEstado(i.Id, i.Texto, 1, false, Suma(i.Cuentas))).ToList();
            totC = c.Sum(x => x.Importe);
            a.AddRange(c); a.Add(new("C", "C) Total transferencias a la cuenta de pérdidas y ganancias (VIII a XIII)", 0, true, totC));
            var sinClasificar = en89.Where(m => !Imputados.Concat(Transferencias).Any(i => Empieza(m.Codigo, i.Cuentas))).Select(m => m.Codigo).Distinct().ToList();
            if (sinClasificar.Count > 0) avisos.Add($"Cuentas de los grupos 8 y 9 sin línea en el estado: {string.Join(", ", sinClasificar)}.");
        }
        else
        {
            // PYMES: las subvenciones se abonan directamente en 130-132 y se cargan al imputarlas a resultados
            var subv = movimientos.Where(m => Empieza(m.Codigo, "130 131 132")).ToList();
            totB = subv.Sum(m => m.Haber); totC = -subv.Sum(m => m.Debe);
            a.Add(new("III", "Subvenciones, donaciones y legados recibidos (imputados al patrimonio neto)", 1, false, totB));
            a.Add(new("B", "B) Total ingresos y gastos imputados directamente en el patrimonio neto", 0, true, totB));
            a.Add(new("X", "Subvenciones, donaciones y legados transferidos a resultados", 1, false, totC));
            a.Add(new("C", "C) Total transferencias a la cuenta de pérdidas y ganancias", 0, true, totC));
            if (subv.Count > 0) avisos.Add("Subvenciones sin grupos 8 y 9 (PYMES): abonos de 130-132 como imputados y cargos como transferencias.");
        }
        a.Add(new("T", "TOTAL DE INGRESOS Y GASTOS RECONOCIDOS (A + B + C)", 0, true, resultadoEjercicio + totB + totC));

        // ---- B) Estado total de cambios
        var cols = Columnas.Select(c => c.Id).Where(c => c != "TOTAL").ToList();
        Dictionary<string, decimal> Vacia() => cols.ToDictionary(c => c, _ => 0m);
        var inicial = Vacia();
        foreach (var (codigo, saldo) in saldosIniciales)
            if (Columna(codigo) is { } col) inicial[col] += -saldo;

        var reconocidos = Vacia();
        reconocidos["RDO"] = resultadoEjercicio;
        var pn = movimientos.Where(m => Columna(m.Codigo) is not null).ToList();
        foreach (var m in pn.Where(m => Columna(m.Codigo) is "AJ" or "SUBV")) reconocidos[Columna(m.Codigo)!] += m.Acreedor;

        var socios = Vacia(); var dividendos = Vacia(); var otras = Vacia();
        foreach (var m in pn.Where(m => Columna(m.Codigo) is not ("AJ" or "SUBV")))
        {
            var col = Columna(m.Codigo)!;
            if (m.Origen == "APLICACION") otras[col] += m.Acreedor;
            else if (col is "CAP" or "NOEX" or "PRIMA" or "PROP" or "APORT" or "DIVC" or "OTROS") socios[col] += m.Acreedor;
            else otras[col] += m.Acreedor;
        }
        // Dividendos repartidos al aplicar el resultado: lo que va a la 526 sale de la columna del resultado o del remanente
        var div = -movimientos.Where(m => m.Origen == "APLICACION" && Empieza(m.Codigo, "526")).Sum(m => m.Acreedor);
        if (div != 0)
        {
            dividendos["RDO"] = div;
            otras["RDO"] -= div;
        }

        FilaEcpn Fila(string id, string texto, Dictionary<string, decimal> v, bool total = false)
            => new(id, texto, total, cols.ToDictionary(c => c, c => v[c]).Append(new("TOTAL", v.Values.Sum())).ToDictionary(x => x.Key, x => x.Value));
        var final = Vacia();
        foreach (var c in cols) final[c] = inicial[c] + reconocidos[c] + socios[c] + dividendos[c] + otras[c];

        var filas = new List<FilaEcpn>
        {
            Fila("INI", "A. SALDO AL INICIO DEL EJERCICIO", inicial, true),
            Fila("I", "I. Total ingresos y gastos reconocidos", reconocidos),
            Fila("II", "II. Operaciones con socios o propietarios (aumentos y reducciones de capital, aportaciones, acciones propias…)", socios),
            Fila("II4", "    Distribución de dividendos", dividendos),
            Fila("III", "III. Otras variaciones del patrimonio neto (aplicación del resultado y otras)", otras),
            Fila("FIN", "B. SALDO AL FINAL DEL EJERCICIO", final, true),
        };
        return new EcpnCalculado(a, Columnas, filas, avisos);
    }
}

/// <summary>
/// Estado de flujos de efectivo por el método indirecto (PGC normal). Cada línea sale de los movimientos del ejercicio de sus cuentas;
/// lo que no encaja en ninguna línea va a «otros cobros y pagos de explotación», así que la suma cuadra siempre con la variación de
/// la tesorería (57). Las cifras son una aproximación a partir de la contabilidad: conviene revisarlas.
/// </summary>
public static class GeneradorEfe
{
    private static bool Empieza(string codigo, string prefijos) => prefijos.Split(' ').Any(p => codigo.StartsWith(p, StringComparison.Ordinal));

    public static (IReadOnlyList<LineaEstado> Lineas, IReadOnlyList<string> Avisos) Calcular(IReadOnlyList<MovimientoOrigen> movimientos,
        decimal efectivoInicial)
    {
        // Saldo deudor del ejercicio (debe − haber) de las cuentas que empiezan por los prefijos, sin las que empiezan por «excepto»
        decimal D(string prefijos, string? excepto = null)
            => movimientos.Where(m => Empieza(m.Codigo, prefijos) && (excepto is null || !Empieza(m.Codigo, excepto))).Sum(m => m.Debe - m.Haber);
        decimal Debe(string prefijos, string? excepto = null)
            => movimientos.Where(m => Empieza(m.Codigo, prefijos) && (excepto is null || !Empieza(m.Codigo, excepto))).Sum(m => m.Debe);
        decimal Haber(string prefijos, string? excepto = null)
            => movimientos.Where(m => Empieza(m.Codigo, prefijos) && (excepto is null || !Empieza(m.Codigo, excepto))).Sum(m => m.Haber);

        var l = new List<LineaEstado>();
        void Add(string id, string texto, int nivel, decimal v, bool total = false) => l.Add(new(id, texto, nivel, total, v));

        // ---- A) Explotación
        var rai = -D("6 7", "630 633 638");
        var amort = D("68");
        var deterioro = D("69 79", "693 793 795");
        var provisiones = D("795");
        var subv = D("746");
        var bajasInm = D("670 671 672 770 771 772");
        var bajasFin = D("666 667 673 675 766 773 775");
        var ingFin = D("76", "763 766 768 773 775");
        var gasFin = D("66", "663 666 667 668 673 675");
        var cambio = D("668 768");
        var vr = D("663 763");
        var ajustes = amort + deterioro + provisiones + subv + bajasInm + bajasFin + ingFin + gasFin + cambio + vr;
        var existencias = -D("3 407 693 793");
        var deudores = -D("43 44 460 470 471 472 473 544 5531 5533 5580", "4709");
        var otrosAct = -D("480 567");
        var acreedores = -D("40 41 438 465 466 475 476 477", "407 4752");
        var otrosPas = -D("485 568 499 529 14");
        var capCorriente = existencias + deudores + otrosAct + acreedores + otrosPas;
        var intereses = -gasFin;
        var dividendosCob = -D("760");
        var interesesCob = -ingFin - dividendosCob;
        var impuesto = -D("630 633 638 4709 4752 474 479 83");

        // ---- B) Inversión (bruto de altas y bajas del inmovilizado y de las inversiones financieras a largo plazo)
        var inmov = "20 21 22 23 24 25 26 580 581 582 583 584";
        var pagosInv = -Debe(inmov);
        var cobrosDesinv = Haber(inmov) - Debe("28 29") - bajasInm;
        // ---- C) Financiación
        // Subvenciones: solo lo recibido (abonos de 130-132 o de 94); su imputación a resultados no mueve efectivo
        var patrimonio = -D("100 101 102 1030 1040 108 109 110 111 118 557") + Haber("130 131 132 94");
        var pasivoFin = -D("15 16 17 18 50 51 52 55 56", "526 557 5580 555 5531 5533 565 566 567 568");
        var dividendosPag = -Debe("526");

        var efectivoFinal = efectivoInicial + D("57");
        var explicado = rai + ajustes + capCorriente + intereses + dividendosCob + interesesCob + impuesto + pagosInv + cobrosDesinv
                        + patrimonio + pasivoFin + dividendosPag;
        var otrosExpl = D("57") - explicado;

        Add("A1", "1. Resultado del ejercicio antes de impuestos", 1, rai);
        Add("A2", "2. Ajustes del resultado", 1, ajustes);
        Add("A2a", "a) Amortización del inmovilizado (+)", 2, amort);
        Add("A2b", "b) Correcciones valorativas por deterioro (+/−)", 2, deterioro);
        Add("A2c", "c) Variación de provisiones (+/−)", 2, provisiones);
        Add("A2d", "d) Imputación de subvenciones (−)", 2, subv);
        Add("A2e", "e) Resultados por bajas y enajenaciones del inmovilizado (+/−)", 2, bajasInm);
        Add("A2f", "f) Resultados por bajas y enajenaciones de instrumentos financieros (+/−)", 2, bajasFin);
        Add("A2g", "g) Ingresos financieros (−)", 2, ingFin);
        Add("A2h", "h) Gastos financieros (+)", 2, gasFin);
        Add("A2i", "i) Diferencias de cambio (+/−)", 2, cambio);
        Add("A2j", "j) Variación de valor razonable en instrumentos financieros (+/−)", 2, vr);
        Add("A3", "3. Cambios en el capital corriente", 1, capCorriente);
        Add("A3a", "a) Existencias (+/−)", 2, existencias);
        Add("A3b", "b) Deudores y otras cuentas a cobrar (+/−)", 2, deudores);
        Add("A3c", "c) Otros activos corrientes (+/−)", 2, otrosAct);
        Add("A3d", "d) Acreedores y otras cuentas a pagar (+/−)", 2, acreedores);
        Add("A3e", "e) Otros pasivos corrientes (+/−)", 2, otrosPas);
        Add("A4", "4. Otros flujos de efectivo de las actividades de explotación", 1, intereses + dividendosCob + interesesCob + impuesto + otrosExpl);
        Add("A4a", "a) Pagos de intereses (−)", 2, intereses);
        Add("A4b", "b) Cobros de dividendos (+)", 2, dividendosCob);
        Add("A4c", "c) Cobros de intereses (+)", 2, interesesCob);
        Add("A4d", "d) Cobros (pagos) por impuesto sobre beneficios (+/−)", 2, impuesto);
        Add("A4e", "e) Otros pagos (cobros) (+/−)", 2, otrosExpl);
        var a = rai + ajustes + capCorriente + intereses + dividendosCob + interesesCob + impuesto + otrosExpl;
        Add("A5", "5. Flujos de efectivo de las actividades de explotación (1 + 2 + 3 + 4)", 0, a, true);
        Add("B6", "6. Pagos por inversiones (−)", 1, pagosInv);
        Add("B7", "7. Cobros por desinversiones (+)", 1, cobrosDesinv);
        Add("B8", "8. Flujos de efectivo de las actividades de inversión (7 − 6)", 0, pagosInv + cobrosDesinv, true);
        Add("C9", "9. Cobros y pagos por instrumentos de patrimonio", 1, patrimonio);
        Add("C10", "10. Cobros y pagos por instrumentos de pasivo financiero", 1, pasivoFin);
        Add("C11", "11. Pagos por dividendos y remuneraciones de otros instrumentos de patrimonio", 1, dividendosPag);
        Add("C12", "12. Flujos de efectivo de las actividades de financiación (9 + 10 + 11)", 0, patrimonio + pasivoFin + dividendosPag, true);
        Add("D", "D) Efecto de las variaciones de los tipos de cambio", 0, 0, true);
        Add("E", "E) AUMENTO/DISMINUCIÓN NETA DEL EFECTIVO O EQUIVALENTES (5 + 8 + 12 + D)", 0, D("57"), true);
        Add("EI", "Efectivo o equivalentes al comienzo del ejercicio", 1, efectivoInicial);
        Add("EF", "Efectivo o equivalentes al final del ejercicio", 1, efectivoFinal);

        var avisos = new List<string>();
        if (otrosExpl != 0)
            avisos.Add($"{otrosExpl:N2} de movimientos no encajan en ninguna línea concreta y se muestran en «otros pagos (cobros)» de explotación: revísalos.");
        avisos.Add("Los intereses y dividendos se suponen cobrados y pagados en el ejercicio; el capital corriente recoge sus pendientes.");
        return (l, avisos);
    }
}
