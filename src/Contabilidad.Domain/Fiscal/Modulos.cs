namespace Contabilidad.Domain.Fiscal;

/// <summary>
/// Datos de estimación objetiva (módulos) de una actividad para un año, calculados con la orden de módulos de ese año:
/// rendimiento neto previo del IRPF y, en el régimen simplificado del IVA, la cuota devengada y la soportada por operaciones
/// corrientes, el % de ingreso a cuenta trimestral de la actividad y el % de cuota mínima.
/// </summary>
public sealed record ModulosActividad(
    long? ActividadId, string Actividad, decimal RendimientoNetoIrpf, decimal PersonasAsalariadas, bool Agricola,
    decimal CuotaIvaDevengadaAnual, decimal CuotaIvaSoportadaAnual, decimal PorcentajeIngresoIva, decimal PorcentajeCuotaMinima,
    string? Epigrafe = null);

/// <summary>Cálculos del régimen simplificado del IVA (303) y de la estimación objetiva del IRPF (131).</summary>
public static class CalculadorModulos
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Ingreso a cuenta de un trimestre (1T–3T): cuota devengada anual por operaciones corrientes × % de la actividad.</summary>
    public static decimal IngresoACuentaIva(IEnumerable<ModulosActividad> actividades)
        => R(actividades.Sum(a => a.CuotaIvaDevengadaAnual * a.PorcentajeIngresoIva / 100m));

    /// <summary>
    /// Cuota anual derivada del régimen simplificado: devengada − soportada por operaciones corrientes − 1 % de la devengada
    /// (gastos de difícil justificación), y como mínimo el % de cuota mínima de la devengada.
    /// </summary>
    public static decimal CuotaAnualIva(IEnumerable<ModulosActividad> actividades)
        => R(actividades.Sum(a => Math.Max(a.CuotaIvaDevengadaAnual - a.CuotaIvaSoportadaAnual - a.CuotaIvaDevengadaAnual * 0.01m,
                                           a.CuotaIvaDevengadaAnual * a.PorcentajeCuotaMinima / 100m)));

    /// <summary>Casillas del régimen simplificado en el 303 del trimestre; devuelve la cuota que se suma al resultado.</summary>
    public static decimal Simplificado303(IReadOnlyList<ModulosActividad> actividades, bool cuartoTrimestre, decimal ingresosAnteriores,
        DeclaracionCalculada d)
    {
        if (actividades.Count == 0)
            throw new ReglaContableException("La empresa está en el régimen simplificado y no tiene los módulos del año.",
                "Indícalos en Modelos fiscales → Módulos (cuota devengada, soportada y % de ingreso a cuenta de cada actividad).");
        if (!cuartoTrimestre)
        {
            var c = IngresoACuentaIva(actividades);
            d.Casillas.Add(new Casilla("RS", "Régimen simplificado: ingreso a cuenta del trimestre (cuota devengada anual × %)", c));
            return c;
        }
        var anual = CuotaAnualIva(actividades);
        var res = R(anual - ingresosAnteriores);
        d.Casillas.Add(new Casilla("RSD", "Régimen simplificado: cuota anual derivada (devengada − soportada − 1 %, o la mínima)", anual));
        d.Casillas.Add(new Casilla("RSA", "Régimen simplificado: ingresos a cuenta de los trimestres anteriores", R(ingresosAnteriores)));
        d.Casillas.Add(new Casilla("RS", "Régimen simplificado: resultado del último trimestre", res));
        return res;
    }

    /// <summary>% del pago fraccionado del 131 por actividad no agrícola: 4 % con más de un asalariado, 3 % con uno y 2 % sin asalariados.</summary>
    public static decimal Porcentaje131(decimal asalariados) => asalariados > 1 ? 4m : asalariados > 0 ? 3m : 2m;

    /// <summary>
    /// Modelo 131 con las casillas del diseño de registro: cada trimestre, el % del rendimiento neto anual de módulos de cada actividad no
    /// agrícola (hasta cinco: «A1R» rendimiento, «A1P» %, «A1C» resultado) [01]/[02], el 2 % del volumen de ingresos del trimestre de las
    /// agrícolas [05]/[06], menos las retenciones [08] y los resultados negativos de trimestres anteriores del año [11].
    /// </summary>
    public static void Calcular131(IReadOnlyList<ModulosActividad> actividades, decimal volumenAgricolaTrimestre, decimal retenciones,
        DeclaracionCalculada d, decimal negativosAnteriores = 0)
    {
        void Add(string k, string desc, decimal v) => d.Casillas.Add(new Casilla(k, desc, R(v)));
        if (actividades.Count == 0)
            throw new ReglaContableException("La empresa no tiene los módulos del año.",
                "Indícalos en Modelos fiscales → Módulos (rendimiento neto de cada actividad y personas asalariadas).");
        var noAgricolas = actividades.Where(x => !x.Agricola).ToList();
        if (noAgricolas.Count > 5)
            throw new ReglaContableException("El 131 admite cinco actividades no agrícolas.", "Agrupa las actividades del mismo epígrafe.");
        decimal rendimientos = 0, pagosNoAgricolas = 0;
        var i = 0;
        foreach (var a in noAgricolas)
        {
            i++;
            var pct = Porcentaje131(a.PersonasAsalariadas);
            var cuota = R(a.RendimientoNetoIrpf * pct / 100m);
            Add($"A{i}R", $"{a.Actividad}: rendimiento neto de módulos", a.RendimientoNetoIrpf);
            Add($"A{i}P", $"{a.Actividad}: % del pago fraccionado", pct);
            Add($"A{i}C", $"{a.Actividad}: pago fraccionado", cuota);
            rendimientos += a.RendimientoNetoIrpf;
            pagosNoAgricolas += cuota;
        }
        if (noAgricolas.Count > 0)
        {
            Add("01", "Suma de rendimientos netos", rendimientos);
            Add("02", "Pago fraccionado previo: suma de resultados", pagosNoAgricolas);
        }
        var agricolas = actividades.Any(x => x.Agricola);
        var pagoAgricola = agricolas ? R(volumenAgricolaTrimestre * 0.02m) : 0m;
        if (agricolas)
        {
            Add("05", "Actividades agrícolas, ganaderas y forestales: volumen de ingresos del trimestre", volumenAgricolaTrimestre);
            Add("06", "Actividades agrícolas, ganaderas y forestales: pago fraccionado previo (2 %)", pagoAgricola);
        }
        var suma = pagosNoAgricolas + pagoAgricola;
        Add("07", "Suma de los pagos fraccionados previos del trimestre", suma);
        Add("08", "A deducir: retenciones e ingresos a cuenta", retenciones);
        var diferencia = R(suma - retenciones);
        Add("10", "Diferencia", diferencia);
        var negativos = diferencia > 0 ? Math.Min(diferencia, Math.Max(0, negativosAnteriores)) : 0;
        if (negativos != 0) Add("11", "A deducir: resultados negativos de trimestres anteriores", negativos);
        var resultado = R(diferencia - negativos);
        Add("13", "Total", resultado);
        Add("14", "A deducir: resultado a ingresar de las anteriores declaraciones (solo complementarias)", 0m);
        Add("15", "Resultado de la declaración", resultado);
        d.Resultado = resultado;
        d.AIngresar = Math.Max(resultado, 0m);
        if (resultado < 0) d.Avisos.Add("El resultado es negativo: se podrá deducir en los trimestres siguientes del año.");
    }
}
