namespace Contabilidad.Domain.Fiscal;

/// <summary>Periodo de una declaración: 1T–4T, 01–12 (mensual) o 0A (anual).</summary>
public sealed record PeriodoFiscal(int Anio, string Codigo, DateOnly Inicio, DateOnly Fin)
{
    public DateOnly InicioAnio => new(Anio, 1, 1);
    public bool EsUltimoDelAnio => Codigo is "4T" or "12" or "0A";
    public bool EsAnual => Codigo == "0A";
    public bool EsTrimestral => Codigo.EndsWith('T');

    public static PeriodoFiscal Resolver(int anio, string? codigo)
    {
        if (anio is < 2000 or > 2100) throw new ReglaContableException("El año no es válido.");
        codigo = (codigo ?? "").Trim().ToUpperInvariant();
        if (codigo == "0A") return new(anio, codigo, new(anio, 1, 1), new(anio, 12, 31));
        if (codigo.Length == 2 && codigo[1] == 'T' && codigo[0] is >= '1' and <= '4')
        {
            var mesIni = (codigo[0] - '1') * 3 + 1;
            var ini = new DateOnly(anio, mesIni, 1);
            return new(anio, codigo, ini, ini.AddMonths(3).AddDays(-1));
        }
        if (codigo.Length == 2 && int.TryParse(codigo, out var mes) && mes is >= 1 and <= 12)
        {
            var ini = new DateOnly(anio, mes, 1);
            return new(anio, codigo, ini, ini.AddMonths(1).AddDays(-1));
        }
        throw new ReglaContableException($"Periodo '{codigo}' no válido: usa 1T–4T, 01–12 o 0A.");
    }
}

/// <summary>Plazo orientativo de presentación. No tiene en cuenta festivos, solo fines de semana.</summary>
public static class PlazoPresentacion
{
    public static (DateOnly Desde, DateOnly Hasta) Calcular(string modelo, PeriodoFiscal p)
    {
        DateOnly desde, hasta;
        switch (modelo)
        {
            case "347" or "415":
                desde = new(p.Anio + 1, 2, 1);
                hasta = new(p.Anio + 1, 2, DateTime.DaysInMonth(p.Anio + 1, 2));
                break;
            case "390" or "425":
                desde = new(p.Anio + 1, 1, 1); hasta = new(p.Anio + 1, 1, 30);
                break;
            default:   // 303, 420, 130
                if (p.Codigo == "4T" || p.Codigo == "12")
                { desde = new(p.Anio + 1, 1, 1); hasta = new(p.Anio + 1, 1, 30); }
                else
                { desde = p.Fin.AddDays(1); hasta = new(desde.Year, desde.Month, 20); }
                break;
        }
        return (desde, SiguienteDiaHabil(hasta));
    }

    private static DateOnly SiguienteDiaHabil(DateOnly d)
        => d.DayOfWeek switch { DayOfWeek.Saturday => d.AddDays(2), DayOfWeek.Sunday => d.AddDays(1), _ => d };
}
