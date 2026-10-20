namespace Contabilidad.Domain.Fiscal;

/// <summary>Ajuste extracontable del Impuesto sobre Sociedades: positivo aumenta la base imponible, negativo la disminuye.</summary>
public sealed record AjusteIs(string Concepto, decimal Importe);

/// <summary>Tipo de gravamen configurado: GENERAL, NUEVA_CREACION, REDUCIDA_DIMENSION, MICRO o PERSONALIZADO (con su %).</summary>
public sealed record TipoGravamenIs(string Clase, decimal? Porcentaje = null)
{
    public static readonly string[] Clases = ["GENERAL", "NUEVA_CREACION", "REDUCIDA_DIMENSION", "MICRO", "PERSONALIZADO"];
}

public sealed record Entrada200(
    decimal ResultadoAntesDeImpuestos, IReadOnlyList<AjusteIs> Ajustes, decimal ReservaCapitalizacion, decimal BinsDisponibles,
    decimal ImporteNetoCifraNegocios, decimal Bonificaciones, decimal Deducciones, decimal Retenciones, decimal PagosFraccionados,
    TipoGravamenIs Tipo, DateOnly Inicio, DateOnly Fin);

public sealed record Entrada202(
    string Modalidad, TipoGravamenIs Tipo, int AnioEjercicio,
    /// <summary>Art. 40.2: cuota líquida menos retenciones del último periodo declarado.</summary>
    decimal BaseUltimoPeriodo,
    /// <summary>Art. 40.3: resultado y ajustes desde el inicio del periodo impositivo hasta el día anterior al plazo.</summary>
    decimal BaseAcumulada, decimal BinsDisponibles, decimal Bonificaciones, decimal Retenciones, decimal PagosAnteriores);

/// <summary>
/// Impuesto sobre Sociedades: liquidación anual (200) y pagos fraccionados (202). Tipos de gravamen de la Ley 27/2014 con los
/// cambios de la Ley 7/2024 (reducida dimensión y microempresas en transición hasta 2029). VERIFICA el tipo que te corresponde
/// y la numeración de casillas con el formulario del año: las claves aquí son descriptivas.
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
        var aumentos = e.Ajustes.Where(a => a.Importe > 0).Sum(a => a.Importe);
        var disminuciones = -e.Ajustes.Where(a => a.Importe < 0).Sum(a => a.Importe);
        var basePrevia = e.ResultadoAntesDeImpuestos + aumentos - disminuciones;
        Add("RC", "Resultado contable antes del Impuesto sobre Sociedades", e.ResultadoAntesDeImpuestos);
        Add("AUM", "Ajustes extracontables: aumentos", aumentos);
        Add("DIS", "Ajustes extracontables: disminuciones", disminuciones);
        Add("BIP", "Base imponible previa", basePrevia);

        var reserva = basePrevia > 0 ? Math.Min(e.ReservaCapitalizacion, basePrevia) : 0;
        if (e.ReservaCapitalizacion > 0 && basePrevia > 0 && e.ReservaCapitalizacion > R(basePrevia * 0.20m))
            d.Avisos.Add("La reserva de capitalización supera el 20 % de la base previa: revisa su límite.");
        Add("RCAP", "Reducción por reserva de capitalización", reserva);
        var antesBins = basePrevia - reserva;
        var comp = antesBins > 0 ? Math.Min(e.BinsDisponibles, Math.Min(antesBins, LimiteBins(antesBins, e.ImporteNetoCifraNegocios))) : 0;
        Add("BNP", "Bases imponibles negativas de periodos anteriores pendientes", e.BinsDisponibles);
        Add("COMP", "Compensación de bases imponibles negativas", comp);
        var bi = antesBins - comp;
        Add("BI", "Base imponible", bi);
        if (bi < 0) d.Avisos.Add($"Base imponible negativa: {(-bi):N2} € se podrán compensar en los periodos siguientes.");

        var (t1, resto) = Tipos(e.Tipo, e.Inicio.Year);
        Add("TG", t1 == resto ? "Tipo de gravamen (%)" : $"Tipo de gravamen (%): {t1} % hasta 50.000 € y {resto} % el resto", resto);
        var integra = CuotaIntegra(bi, e.Tipo, e.Inicio, e.Fin);
        Add("CI", "Cuota íntegra", integra);
        var bonif = Math.Min(e.Bonificaciones, integra);
        var deduc = Math.Min(e.Deducciones, integra - bonif);
        if (e.Deducciones > deduc) d.Avisos.Add("Las deducciones superan la cuota: el exceso se aplica en periodos siguientes.");
        Add("BON", "Bonificaciones", bonif);
        Add("DED", "Deducciones", deduc);
        var liquida = integra - bonif - deduc;
        Add("CL", "Cuota líquida", liquida);
        Add("RET", "Retenciones e ingresos a cuenta", e.Retenciones);
        Add("PF", "Pagos fraccionados (202) del periodo", e.PagosFraccionados);
        var diferencial = R(liquida - e.Retenciones - e.PagosFraccionados);
        Add("CD", "Cuota diferencial", diferencial);
        Add("RES", "Resultado de la autoliquidación", diferencial);
        d.Resultado = diferencial;
        if (diferencial >= 0) d.AIngresar = diferencial; else d.ADevolver = -diferencial;
        d.Avisos.Add("Claves descriptivas: VERIFICA la numeración de casillas y el tipo de gravamen con el formulario del modelo 200 del año.");
    }

    /// <summary>Porcentaje del pago fraccionado del art. 40.3: 5/7 del tipo de gravamen, redondeado por defecto.</summary>
    public static decimal Porcentaje403(TipoGravamenIs t, int anio) => Math.Floor(Tipos(t, anio).Resto * 5m / 7m);

    public static void Calcular202(Entrada202 e, DeclaracionCalculada d)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        decimal pago;
        if (e.Modalidad == "ART_40_3")
        {
            var comp = e.BaseAcumulada > 0 ? Math.Min(e.BinsDisponibles, e.BaseAcumulada) : 0;
            var bi = e.BaseAcumulada - comp;
            var pct = Porcentaje403(e.Tipo, e.AnioEjercicio);
            Add("BA", "Base imponible del periodo transcurrido (art. 40.3)", e.BaseAcumulada);
            Add("COMP", "Compensación de bases imponibles negativas", comp);
            Add("BI", "Base del pago fraccionado", bi);
            Add("PCT", "Porcentaje (5/7 del tipo de gravamen)", pct);
            var cuota = bi > 0 ? R(bi * pct / 100m) : 0;
            Add("CUOTA", "Resultado de aplicar el porcentaje", cuota);
            Add("BON", "Bonificaciones", e.Bonificaciones);
            Add("RET", "Retenciones e ingresos a cuenta del periodo", e.Retenciones);
            Add("PFA", "Pagos fraccionados anteriores del mismo periodo impositivo", e.PagosAnteriores);
            pago = cuota - e.Bonificaciones - e.Retenciones - e.PagosAnteriores;
        }
        else
        {
            Add("BASE", "Cuota íntegra menos deducciones, bonificaciones y retenciones del último periodo declarado (art. 40.2)", e.BaseUltimoPeriodo);
            Add("PCT", "Porcentaje", 18m);
            pago = e.BaseUltimoPeriodo > 0 ? R(e.BaseUltimoPeriodo * 0.18m) : 0;
        }
        var res = Math.Max(0, R(pago));
        Add("RES", "Pago fraccionado a ingresar", res);
        d.Resultado = res;
        d.AIngresar = res;
        if (pago < 0) d.Avisos.Add("El cálculo sale negativo: no hay pago fraccionado (se presenta sin ingreso).");
        d.Avisos.Add("Claves descriptivas: VERIFICA la numeración de casillas con el formulario del modelo 202. Con un importe neto de la cifra de negocios " +
                     "de 10 M€ o más la modalidad del art. 40.3 es obligatoria y tiene un porcentaje y un pago mínimo distintos.");
    }
}
