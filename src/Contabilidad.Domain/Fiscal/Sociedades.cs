namespace Contabilidad.Domain.Fiscal;

/// <summary>
/// Ajuste extracontable del Impuesto sobre Sociedades: positivo aumenta la base imponible, negativo la disminuye. <see cref="Clase"/> dice
/// en qué casilla del modelo 200 va (ver <see cref="ClasesAjusteIs"/>); sin clase va a «otras correcciones».
/// </summary>
public sealed record AjusteIs(string Concepto, decimal Importe, string? Clase = null);

/// <summary>Clases de ajuste extracontable con las casillas de aumento y disminución de la página 12 o 13 del modelo 200.</summary>
public static class ClasesAjusteIs
{
    public sealed record Clase(string Codigo, string Texto, string? Aumento, string? Disminucion);

    public static readonly IReadOnlyList<Clase> Todas =
    [
        new("AMORTIZACION", "Diferencias entre amortización contable y fiscal (art. 12.1 LIS)", "00303", "00304"),
        new("DETERIORO", "Pérdidas por deterioro (art. 13.1 LIS) y provisiones y gastos", "00415", "00211"),
        new("PROVISIONES", "Otras provisiones no deducibles (art. 14 LIS)", "00337", "00338"),
        new("MULTAS", "Multas, sanciones y otros (art. 15 c LIS)", "01815", null),
        new("DONATIVOS", "Donativos y liberalidades (art. 15 e LIS)", "00339", null),
        new("RETRIBUCION_FONDOS_PROPIOS", "Retribución de fondos propios (art. 15 a LIS)", "01002", null),
        new("GASTOS_FINANCIEROS", "Limitación de gastos financieros (art. 16 LIS)", "00363", "00364"),
        new("OTRAS", "Otras correcciones al resultado", "00413", "00414"),
    ];

    public static Clase De(string? codigo) => Todas.FirstOrDefault(c => c.Codigo == (codigo ?? "OTRAS"))
        ?? throw new ReglaContableException($"Clase de ajuste desconocida: {codigo}.", $"Valores: {string.Join(", ", Todas.Select(c => c.Codigo))}.");
}

/// <summary>Tipo de gravamen configurado: GENERAL, NUEVA_CREACION, REDUCIDA_DIMENSION, MICRO o PERSONALIZADO (con su %).</summary>
public sealed record TipoGravamenIs(string Clase, decimal? Porcentaje = null)
{
    public static readonly string[] Clases = ["GENERAL", "NUEVA_CREACION", "REDUCIDA_DIMENSION", "MICRO", "PERSONALIZADO"];
}

/// <summary>Base imponible negativa pendiente de compensar, por año de origen (el del periodo impositivo que la generó).</summary>
public sealed record BinAnio(int Anio, decimal Importe);

/// <param name="ResultadoAntesDeImpuestos">Resultado contable sin el gasto por Impuesto sobre Sociedades.</param>
/// <param name="Pagos">Pagos fraccionados (202) presentados del periodo: primero, segundo y tercero.</param>
/// <param name="ImpuestoContabilizado">Gasto por impuesto ya contabilizado (630, 633, 638): el resultado de la cuenta de pérdidas y
/// ganancias es el resultado antes de impuestos menos este importe.</param>
public sealed record Entrada200(
    decimal ResultadoAntesDeImpuestos, IReadOnlyList<AjusteIs> Ajustes, decimal ReservaCapitalizacion, decimal BinsDisponibles,
    decimal ImporteNetoCifraNegocios, decimal Bonificaciones, decimal Deducciones, decimal Retenciones, (decimal P1, decimal P2, decimal P3) Pagos,
    TipoGravamenIs Tipo, DateOnly Inicio, DateOnly Fin, decimal ImpuestoContabilizado = 0, IReadOnlyList<BinAnio>? BinsPorAnio = null)
{
    public decimal PagosFraccionados => Pagos.P1 + Pagos.P2 + Pagos.P3;
}

public sealed record Entrada202(
    string Modalidad, TipoGravamenIs Tipo, int AnioEjercicio,
    /// <summary>Art. 40.2: cuota líquida menos retenciones del último periodo declarado.</summary>
    decimal BaseUltimoPeriodo,
    /// <summary>Art. 40.3: resultado y ajustes desde el inicio del periodo impositivo hasta el día anterior al plazo.</summary>
    decimal BaseAcumulada, decimal BinsDisponibles, decimal Bonificaciones, decimal Retenciones, decimal PagosAnteriores,
    /// <summary>Importe neto de la cifra de negocios de los doce meses anteriores al inicio del periodo impositivo.</summary>
    decimal Incn = 0);

/// <summary>
/// Impuesto sobre Sociedades: liquidación anual (200) y pagos fraccionados (202). Tipos de gravamen de la Ley 27/2014 con los
/// cambios de la Ley 7/2024 (reducida dimensión y microempresas en transición hasta 2029). VERIFICA el tipo que te corresponde
/// en cada ejercicio. Las casillas son las de los diseños de registro de 2025 (200: «00552» base imponible…; 202: «16» base del pago
/// fraccionado…); las pocas que no tienen número en el modelo llevan una clave descriptiva (TG1; BIN2019P/A/F: detalle de las bases
/// negativas de 2019 en la página 15).
/// </summary>
public static class CalculadorSociedades
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Tipo (y, en las microempresas, el del primer tramo de 50.000 € y el del resto) del periodo que empieza en <paramref name="anio"/>.</summary>
    public static (decimal Tramo1, decimal Resto) Tipos(TipoGravamenIs t, int anio) => t.Clase switch
    {
        "NUEVA_CREACION" => (15m, 15m),
        "REDUCIDA_DIMENSION" => anio switch { <= 2024 => (25m, 25m), 2025 => (24m, 24m), 2026 => (23m, 23m), 2027 => (22m, 22m), 2028 => (21m, 21m), _ => (20m, 20m) },
        "MICRO" => anio switch { <= 2022 => (25m, 25m), <= 2024 => (23m, 23m), 2025 => (21m, 22m), 2026 => (19m, 21m), _ => (17m, 20m) },
        "PERSONALIZADO" => (t.Porcentaje ?? throw new ReglaContableException("Indica el % del tipo de gravamen personalizado."),
                            t.Porcentaje.Value),
        _ => (25m, 25m),
    };

    /// <summary>Cuota íntegra: en las microempresas los primeros 50.000 € (proporcionales si el periodo dura menos de un año) al tipo reducido.</summary>
    public static decimal CuotaIntegra(decimal baseImponible, TipoGravamenIs t, DateOnly inicio, DateOnly fin)
    {
        if (baseImponible <= 0) return 0;
        var (t1, resto) = Tipos(t, inicio.Year);
        if (t1 == resto) return R(baseImponible * t1 / 100m);
        var dias = fin.DayNumber - inicio.DayNumber + 1;
        var tramo = dias >= 365 ? 50_000m : R(50_000m * dias / 365m);
        return R(Math.Min(baseImponible, tramo) * t1 / 100m + Math.Max(0, baseImponible - tramo) * resto / 100m);
    }

    /// <summary>Límite de compensación de bases negativas: 70 % de la base previa (50 % con INCN de 20 a 60 M€, 25 % desde 60 M€), con un mínimo de 1 M€.</summary>
    public static decimal LimiteBins(decimal basePrevia, decimal incn)
    {
        var pct = incn >= 60_000_000m ? 0.25m : incn >= 20_000_000m ? 0.50m : 0.70m;
        return Math.Max(1_000_000m, R(basePrevia * pct));
    }

    public static void Calcular200(Entrada200 e, DeclaracionCalculada d)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        // Resultado de la cuenta de pérdidas y ganancias y corrección por el propio impuesto (página 12)
        Add("00500", "Resultado de la cuenta de pérdidas y ganancias", e.ResultadoAntesDeImpuestos - e.ImpuestoContabilizado);
        if (e.ImpuestoContabilizado > 0) Add("00301", "Corrección por Impuesto sobre Sociedades: aumentos", e.ImpuestoContabilizado);
        if (e.ImpuestoContabilizado < 0) Add("00302", "Corrección por Impuesto sobre Sociedades: disminuciones", -e.ImpuestoContabilizado);
        Add("00501", "Resultado antes del Impuesto sobre Sociedades", e.ResultadoAntesDeImpuestos);
        // Ajustes extracontables por clase: cada clase tiene su casilla de aumentos y de disminuciones
        decimal aumentos = 0, disminuciones = 0;
        foreach (var g in e.Ajustes.GroupBy(a => ClasesAjusteIs.De(a.Clase)).OrderBy(g => g.Key.Aumento))
        {
            var mas = g.Where(a => a.Importe > 0).Sum(a => a.Importe);
            var menos = -g.Where(a => a.Importe < 0).Sum(a => a.Importe);
            if (menos != 0 && g.Key.Disminucion is null)
                throw new ReglaContableException($"«{g.Key.Texto}» solo admite aumentos de la base imponible.",
                    "Indica la disminución como «Otras correcciones al resultado».");
            if (mas != 0) Add(g.Key.Aumento!, $"{g.Key.Texto}: aumentos", mas);
            if (menos != 0) Add(g.Key.Disminucion!, $"{g.Key.Texto}: disminuciones", menos);
            aumentos += mas; disminuciones += menos;
        }
        var basePrevia = e.ResultadoAntesDeImpuestos + aumentos - disminuciones;
        Add("00550", "Base imponible antes de la reserva de capitalización y de la compensación de bases negativas", basePrevia);

        var reserva = basePrevia > 0 ? Math.Min(e.ReservaCapitalizacion, basePrevia) : 0;
        if (e.ReservaCapitalizacion > 0 && basePrevia > 0 && e.ReservaCapitalizacion > R(basePrevia * 0.20m))
            d.Avisos.Add("La reserva de capitalización supera el 20 % de la base previa: revisa su límite.");
        if (reserva != 0) Add("01032", "Reserva de capitalización", reserva);
        var antesBins = basePrevia - reserva;
        var comp = antesBins > 0 ? Math.Min(e.BinsDisponibles, Math.Min(antesBins, LimiteBins(antesBins, e.ImporteNetoCifraNegocios))) : 0;
        Add("00547", "Compensación de bases imponibles negativas de periodos anteriores", comp);
        var bi = antesBins - comp;
        Add("00552", "Base imponible", bi);
        DetalleBins(e, comp, bi, Add);
        if (bi < 0) d.Avisos.Add($"Base imponible negativa: {(-bi):N2} € se podrán compensar en los periodos siguientes.");

        var (t1, resto) = Tipos(e.Tipo, e.Inicio.Year);
        Add("00558", t1 == resto ? "Tipo de gravamen (%)" : $"Tipo de gravamen (%): {t1} % hasta 50.000 € y {resto} % el resto", resto);
        if (t1 != resto) Add("TG1", "Tipo de gravamen del primer tramo (%)", t1);
        var integra = CuotaIntegra(bi, e.Tipo, e.Inicio, e.Fin);
        Add("00562", "Cuota íntegra", integra);
        var bonif = Math.Min(e.Bonificaciones, integra);
        if (bonif != 0) Add("00569", "Otras bonificaciones", bonif);
        Add("00582", "Cuota íntegra ajustada positiva", integra - bonif);
        var deduc = Math.Min(e.Deducciones, integra - bonif);
        if (e.Deducciones > deduc) d.Avisos.Add("Las deducciones superan la cuota: el exceso se aplica en periodos siguientes.");
        if (deduc != 0)
        {
            Add("00588", "Deducciones para incentivar determinadas actividades", deduc);
            d.Avisos.Add("Las deducciones van en la casilla 00588 (incentivos a determinadas actividades): detállalas en las páginas 17 y 18 " +
                         "del formulario o muévelas a su casilla si son de otra clase.");
        }
        var liquida = integra - bonif - deduc;
        Add("00592", "Cuota líquida", liquida);
        if (e.Retenciones != 0) Add("01798", "Retenciones por otros conceptos (efectuadas a la entidad)", e.Retenciones);
        Add("01766", "Total retenciones e ingresos a cuenta (efectuados a la entidad)", e.Retenciones);
        var cuotaEjercicio = liquida - e.Retenciones;
        Add("00599", "Cuota del ejercicio a ingresar o a devolver", cuotaEjercicio);
        if (e.Pagos.P1 != 0) Add("00601", "Primer pago fraccionado", e.Pagos.P1);
        if (e.Pagos.P2 != 0) Add("00603", "Segundo pago fraccionado", e.Pagos.P2);
        if (e.Pagos.P3 != 0) Add("00605", "Tercer pago fraccionado", e.Pagos.P3);
        var diferencial = R(cuotaEjercicio - e.PagosFraccionados);
        Add("00611", "Cuota diferencial", diferencial);
        Add("01586", "Resultado de la autoliquidación", diferencial);
        Add("00621", "Resultado a ingresar o a devolver", diferencial);
        d.Resultado = diferencial;
        if (diferencial >= 0) d.AIngresar = diferencial; else d.ADevolver = -diferencial;
        if (e.Retenciones != 0)
            d.Avisos.Add("Las retenciones (473) van como «otros conceptos» (01798): si son de capital mobiliario o de alquileres, muévelas a su casilla en el formulario.");
        d.Avisos.Add("VERIFICA el tipo de gravamen que te corresponde en el formulario del modelo 200 del año.");
    }

    /// <summary>
    /// Detalle de la página 15 del 200 por año de origen: pendiente al inicio (o generada en el periodo), aplicada y pendiente para
    /// periodos futuros. Se compensan primero las más antiguas.
    /// </summary>
    private static void DetalleBins(Entrada200 e, decimal comp, decimal bi, Action<string, string, decimal> add)
    {
        var porAnio = (e.BinsPorAnio ?? (e.BinsDisponibles > 0 ? [new BinAnio(e.Inicio.Year - 1, e.BinsDisponibles)] : []))
            .Where(b => b.Importe > 0).GroupBy(b => b.Anio).ToDictionary(g => g.Key, g => g.Sum(b => b.Importe));
        if (porAnio.Values.Sum() != e.BinsDisponibles)
            throw new ReglaContableException("El detalle de bases negativas por año no suma el total pendiente.");
        var generada = bi < 0 ? -bi : 0;
        if (generada > 0) porAnio[e.Inicio.Year] = porAnio.GetValueOrDefault(e.Inicio.Year) + generada;
        if (porAnio.Count == 0) return;
        var resto = comp;
        foreach (var (anio, pendiente) in porAnio.OrderBy(x => x.Key))
        {
            var aplicada = anio == e.Inicio.Year && generada > 0 ? 0 : Math.Min(resto, pendiente);
            resto -= aplicada;
            add($"BIN{anio}P", $"Bases negativas de {anio}: pendientes al inicio o generadas en el periodo", pendiente);
            if (aplicada != 0) add($"BIN{anio}A", $"Bases negativas de {anio}: aplicadas en esta liquidación", aplicada);
            add($"BIN{anio}F", $"Bases negativas de {anio}: pendientes para periodos futuros", pendiente - aplicada);
        }
        add("00670", "Total bases negativas pendientes al inicio o generadas en el periodo", porAnio.Values.Sum());
        add("00671", "Total bases negativas pendientes para periodos futuros", porAnio.Values.Sum() - comp);
    }

    /// <summary>Porcentaje del pago fraccionado del art. 40.3: 5/7 del tipo de gravamen, redondeado por defecto.</summary>
    public static decimal Porcentaje403(TipoGravamenIs t, int anio) => Math.Floor(Tipos(t, anio).Resto * 5m / 7m);

    /// <summary>Cifra de negocios desde la que el 40.3 es obligatorio, con porcentaje de 19/20 del tipo y pago mínimo.</summary>
    public const decimal IncnGranEmpresa = 10_000_000m;

    /// <summary>Pago mínimo de las grandes empresas (DA 14ª LIS): 23 % del resultado positivo del periodo.</summary>
    public const decimal PorcentajeMinimo = 23m;

    /// <summary>Porcentaje sobre un tipo: 5/7 redondeado por defecto o, con 10 M€ de cifra de negocios o más, 19/20 redondeado por exceso.</summary>
    public static decimal PorcentajeSobre(decimal tipo, decimal incn) => incn >= IncnGranEmpresa ? Math.Ceiling(tipo * 19m / 20m) : Math.Floor(tipo * 5m / 7m);

    public static void Calcular202(Entrada202 e, DeclaracionCalculada d)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        decimal pago;
        if (e.Modalidad == "ART_40_3")
        {
            var grande = e.Incn >= IncnGranEmpresa;
            // Página 1: resultado contable y base imponible previa
            Add("04", "Resultado contable del periodo", e.BaseAcumulada);
            Add("13", "Base imponible previa", e.BaseAcumulada);
            var comp = e.BaseAcumulada > 0 ? Math.Min(e.BinsDisponibles, e.BaseAcumulada) : 0;
            if (comp != 0) Add("14", "Compensación de bases negativas de ejercicios anteriores", comp);
            var bi = Math.Max(0, e.BaseAcumulada - comp);
            var (t1, resto) = Tipos(e.Tipo, e.AnioEjercicio);
            decimal cuota;
            if (t1 == resto)
            {
                // B1: caso general, un solo porcentaje
                var pct = PorcentajeSobre(resto, e.Incn);
                Add("16", "Base del pago fraccionado", bi);
                Add("17", grande ? "Porcentaje (19/20 del tipo de gravamen)" : "Porcentaje (5/7 del tipo de gravamen)", pct);
                cuota = R(bi * pct / 100m);
                Add("18", "Resultado previo", cuota);
            }
            else
            {
                // B2: más de un porcentaje (microempresas: primeros 50.000 € a un tipo y el resto a otro)
                var tramo1 = Math.Min(bi, 50_000m);
                var (p1, p2) = (PorcentajeSobre(t1, e.Incn), PorcentajeSobre(resto, e.Incn));
                Add("19", "Base del pago fraccionado", bi);
                Add("20", "Base a tipo 1", tramo1);
                Add("21", "Porcentaje sobre el tipo 1", p1);
                var c1 = R(tramo1 * p1 / 100m);
                Add("22", "Importe del pago fraccionado al tipo 1", c1);
                Add("23", "Base a tipo 2", bi - tramo1);
                Add("24", "Porcentaje sobre el tipo 2", p2);
                var c2 = R((bi - tramo1) * p2 / 100m);
                Add("25", "Importe del pago fraccionado al tipo 2", c2);
                cuota = c1 + c2;
                Add("26", "Resultado previo", cuota);
                d.Avisos.Add("Microempresa: los primeros 50.000 € de la base van al porcentaje del tipo reducido (B2). VERIFICA si el tramo debe " +
                             "reducirse en proporción a los meses transcurridos.");
            }
            if (e.Bonificaciones != 0) Add("27", "Bonificaciones del periodo", e.Bonificaciones);
            if (e.Retenciones != 0) Add("28", "Retenciones e ingresos a cuenta del periodo", e.Retenciones);
            Add("29", "Volumen de operaciones en territorio común (%)", 100m);
            if (e.PagosAnteriores != 0) Add("30", "Pagos fraccionados de periodos anteriores", e.PagosAnteriores);
            pago = cuota - e.Bonificaciones - e.Retenciones - e.PagosAnteriores;
            var res = Math.Max(0, R(pago));
            Add("32", "Resultado", res);
            var aIngresar = res;
            if (grande)
            {
                // Pago mínimo: 23 % del resultado positivo menos las bases negativas compensadas y los pagos anteriores
                var minimo = Math.Max(0, R(Math.Max(0, e.BaseAcumulada - comp) * PorcentajeMinimo / 100m) - e.PagosAnteriores);
                Add("33", "Mínimo a ingresar (23 % del resultado contable positivo)", minimo);
                aIngresar = Math.Max(res, minimo);
                d.Avisos.Add("Cifra de negocios de 10 M€ o más: porcentaje de 19/20 del tipo y pago mínimo del 23 % (25 % en las entidades al 30 %). " +
                             "VERIFICA la base del pago mínimo en el formulario.");
            }
            Add("34", "Cantidad a ingresar", aIngresar);
            d.Resultado = aIngresar;
        }
        else
        {
            var bas = Math.Max(0, e.BaseUltimoPeriodo);
            Add("01", "Base del pago fraccionado (cuota íntegra menos deducciones, bonificaciones y retenciones del último periodo declarado)", bas);
            pago = R(bas * 0.18m);
            Add("03", "A ingresar (18 % de la base)", pago);
            d.Resultado = pago;
        }
        d.AIngresar = d.Resultado;
        if (pago < 0) d.Avisos.Add("El cálculo sale negativo: no hay pago fraccionado (se presenta sin ingreso).");
        if (e.Modalidad != "ART_40_3" && e.Incn >= IncnGranEmpresa)
            throw new ReglaContableException("Con una cifra de negocios de 10 M€ o más el pago fraccionado se calcula por el art. 40.3.",
                "Cambia la modalidad del 202 en la configuración fiscal.");
    }
}
