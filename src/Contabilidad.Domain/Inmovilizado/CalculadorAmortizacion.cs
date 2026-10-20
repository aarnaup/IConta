using Contabilidad.Domain.Cierre;

namespace Contabilidad.Domain.Inmovilizado;

/// <summary>Datos de un elemento que intervienen en la amortización.</summary>
/// <param name="AmortizacionPrevia">Lo amortizado antes de llevarlo en la aplicación (ya está en la contabilidad).</param>
public sealed record FichaAmortizacion(decimal ValorAdquisicion, decimal ValorResidual, DateOnly Inicio, int? VidaUtilMeses, string Metodo,
    decimal AmortizacionPrevia = 0)
{
    public decimal BaseAmortizable => Math.Max(0, ValorAdquisicion - ValorResidual);
    public bool Amortiza => Metodo == "LINEAL" && VidaUtilMeses is > 0 && BaseAmortizable > 0;
    /// <summary>Último día de amortización: inicio + vida útil − 1 día.</summary>
    public DateOnly? Fin => Amortiza ? Inicio.AddMonths(VidaUtilMeses!.Value).AddDays(-1) : null;
}

public sealed record TramoCuadro(DateOnly Desde, DateOnly Hasta, decimal Cuota, decimal Acumulada, decimal ValorNeto);

/// <summary>
/// Amortización lineal por días: lo acumulado a una fecha es la base amortizable por la fracción de vida útil transcurrida
/// (días desde el inicio, incluido, entre los días totales), redondeado a céntimos. Las cuotas se calculan como diferencia de
/// acumulados, así que el redondeo nunca se arrastra y al final de la vida útil se llega exactamente a la base.
/// </summary>
public static class CalculadorAmortizacion
{
    public static readonly string[] Metodos = ["LINEAL", "NO_AMORTIZA"];

    /// <summary>Amortización acumulada que corresponde a la fecha (incluida).</summary>
    public static decimal AcumuladaA(FichaAmortizacion f, DateOnly fecha)
    {
        if (!f.Amortiza || fecha < f.Inicio) return 0;
        var fin = f.Fin!.Value;
        if (fecha >= fin) return f.BaseAmortizable;
        var transcurridos = fecha.DayNumber - f.Inicio.DayNumber + 1;
        var totales = fin.DayNumber - f.Inicio.DayNumber + 1;
        return Math.Round(f.BaseAmortizable * transcurridos / totales, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Lo que falta por amortizar hasta la fecha, descontando lo ya registrado (incluida la amortización previa).</summary>
    public static decimal Pendiente(FichaAmortizacion f, DateOnly hasta, decimal yaAmortizado)
        => Math.Max(0, AcumuladaA(f, hasta) - yaAmortizado);

    /// <summary>
    /// Cuadro de amortización en tramos anuales que empiezan el día 1 del mes <paramref name="mesInicioEjercicio"/>
    /// (los ejercicios de la empresa), desde el inicio de la amortización hasta el final de la vida útil.
    /// </summary>
    public static IReadOnlyList<TramoCuadro> Cuadro(FichaAmortizacion f, int mesInicioEjercicio)
    {
        if (!f.Amortiza) return [];
        var tramos = new List<TramoCuadro>();
        var inicioTramo = new DateOnly(f.Inicio.Month >= mesInicioEjercicio ? f.Inicio.Year : f.Inicio.Year - 1, mesInicioEjercicio, 1);
        decimal anterior = 0;
        while (inicioTramo <= f.Fin!.Value)
        {
            var finTramo = inicioTramo.AddYears(1).AddDays(-1);
            var desde = inicioTramo < f.Inicio ? f.Inicio : inicioTramo;
            var hasta = finTramo > f.Fin.Value ? f.Fin.Value : finTramo;
            var acumulada = AcumuladaA(f, hasta);
            tramos.Add(new TramoCuadro(desde, hasta, acumulada - anterior, acumulada, f.ValorAdquisicion - acumulada));
            anterior = acumulada;
            inicioTramo = finTramo.AddDays(1);
        }
        return tramos;
    }
}

/// <summary>Cuentas de un elemento según su categoría.</summary>
public sealed record CuentasInmovilizado(long Inmovilizado, long? AmortizacionAcumulada, long? GastoAmortizacion);

public sealed record ResultadoBaja(decimal ValorAdquisicion, decimal AmortizacionAcumulada, decimal ValorNeto, decimal ImporteVenta)
{
    /// <summary>Positivo, beneficio (771); negativo, pérdida (671).</summary>
    public decimal Resultado => ImporteVenta - ValorNeto;
}

/// <summary>
/// Asiento de baja o venta: se carga toda la amortización acumulada (28x), se abona el valor de adquisición (21x),
/// se carga lo que se cobra (si es una venta) y la diferencia va a pérdidas (671) o beneficios (771) procedentes del inmovilizado.
/// La amortización hasta la fecha de baja se contabiliza antes, en el mismo asiento.
/// </summary>
public static class GeneradorBaja
{
    public static (IReadOnlyList<ApunteGenerado> Apuntes, ResultadoBaja Resultado) Generar(
        decimal valorAdquisicion, decimal acumuladaTotal, decimal amortizacionPendiente, decimal importeVenta, CuentasInmovilizado c,
        long? cuentaCobro, long cuentaPerdidas, long cuentaBeneficios, long canalId)
    {
        if (importeVenta < 0) throw new ReglaContableException("El importe de venta no puede ser negativo.");
        if (importeVenta > 0 && cuentaCobro is null) throw new ReglaContableException("Indica la cuenta en la que se cobra la venta.");
        var acumulada = acumuladaTotal + amortizacionPendiente;
        if (acumulada > 0 && c.AmortizacionAcumulada is null)
            throw new ReglaContableException("La categoría no tiene cuenta de amortización acumulada.");

        var apuntes = new List<ApunteGenerado>();
        if (amortizacionPendiente > 0)
        {
            apuntes.Add(new(c.GastoAmortizacion ?? throw new ReglaContableException("La categoría no tiene cuenta de gasto por amortización."),
                canalId, amortizacionPendiente, 0));
            apuntes.Add(new(c.AmortizacionAcumulada!.Value, canalId, 0, amortizacionPendiente));
        }
        if (acumulada > 0) apuntes.Add(new(c.AmortizacionAcumulada!.Value, canalId, acumulada, 0));
        if (importeVenta > 0) apuntes.Add(new(cuentaCobro!.Value, canalId, importeVenta, 0));

        var r = new ResultadoBaja(valorAdquisicion, acumulada, valorAdquisicion - acumulada, importeVenta);
        if (r.Resultado < 0) apuntes.Add(new(cuentaPerdidas, canalId, -r.Resultado, 0));
        apuntes.Add(new(c.Inmovilizado, canalId, 0, valorAdquisicion));
        if (r.Resultado > 0) apuntes.Add(new(cuentaBeneficios, canalId, 0, r.Resultado));
        return (apuntes, r);
    }
}
