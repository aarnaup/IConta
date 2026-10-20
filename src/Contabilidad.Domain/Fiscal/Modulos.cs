namespace Contabilidad.Domain.Fiscal;

/// <summary>
/// Datos de estimación objetiva (módulos) de una actividad para un año, calculados con la orden de módulos de ese año:
/// rendimiento neto previo del IRPF y, en el régimen simplificado del IVA, la cuota devengada y la soportada por operaciones
/// corrientes, el % de ingreso a cuenta trimestral de la actividad y el % de cuota mínima.
/// </summary>
public sealed record ModulosActividad(
    long? ActividadId, string Actividad, decimal RendimientoNetoIrpf, decimal PersonasAsalariadas, bool Agricola,
    decimal CuotaIvaDevengadaAnual, decimal CuotaIvaSoportadaAnual, decimal PorcentajeIngresoIva, decimal PorcentajeCuotaMinima);

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
    /// Modelo 131: cada trimestre, el % del rendimiento neto anual de módulos de las actividades no agrícolas y el 2 % del volumen
    /// de ingresos del trimestre de las agrícolas, menos las retenciones soportadas en el trimestre.
    /// </summary>
    public static void Calcular131(IReadOnlyList<ModulosActividad> actividades, decimal volumenAgricolaTrimestre, decimal retenciones,
        DeclaracionCalculada d)
    {
        if (actividades.Count == 0)
            throw new ReglaContableException("La empresa no tiene los módulos del año.",
                "Indícalos en Modelos fiscales → Módulos (rendimiento neto de cada actividad y personas asalariadas).");
        decimal pagosNoAgricolas = 0;
        var i = 0;
        foreach (var a in actividades.Where(x => !x.Agricola))
        {
            i++;
            var pct = Porcentaje131(a.PersonasAsalariadas);
            var cuota = R(a.RendimientoNetoIrpf * pct / 100m);
            d.Casillas.Add(new Casilla($"A{i}R", $"{a.Actividad}: rendimiento neto de módulos", R(a.RendimientoNetoIrpf)));
            d.Casillas.Add(new Casilla($"A{i}P", $"{a.Actividad}: % del pago fraccionado", pct));
            d.Casillas.Add(new Casilla($"A{i}C", $"{a.Actividad}: pago fraccionado", cuota));
            pagosNoAgricolas += cuota;
        }
        var agricolas = actividades.Any(x => x.Agricola);
        var pagoAgricola = agricolas ? R(volumenAgricolaTrimestre * 0.02m) : 0m;
        d.Casillas.Add(new Casilla("NA", "Actividades no agrícolas: suma de pagos fraccionados", pagosNoAgricolas));
        if (agricolas)
        {
            d.Casillas.Add(new Casilla("AGV", "Actividades agrícolas, ganaderas y forestales: volumen de ingresos del trimestre", R(volumenAgricolaTrimestre)));
            d.Casillas.Add(new Casilla("AG", "Actividades agrícolas, ganaderas y forestales: 2 % del volumen", pagoAgricola));
        }
        var suma = pagosNoAgricolas + pagoAgricola;
        var resultado = R(suma - retenciones);
        d.Casillas.Add(new Casilla("SUMA", "Suma de pagos fraccionados", suma));
        d.Casillas.Add(new Casilla("RET", "A deducir: retenciones e ingresos a cuenta soportados en el trimestre", R(retenciones)));
        d.Casillas.Add(new Casilla("DED", "A deducir: resultado a ingresar de la autoliquidación anterior (solo complementarias)", 0m));
        d.Casillas.Add(new Casilla("RES", "Resultado de la autoliquidación", resultado));
        d.Resultado = resultado;
        d.AIngresar = Math.Max(resultado, 0m);
        if (resultado < 0) d.Avisos.Add("El resultado es negativo: no hay nada que ingresar este trimestre.");
        d.Avisos.Add("Claves descriptivas: VERIFICA la numeración de casillas con el formulario del 131 del año antes de presentar.");
    }
}
