using System.Globalization;
using System.Text;

namespace Contabilidad.Domain.Tesoreria;

public sealed record ReglaConciliacion(long Id, long? CuentaBancariaId, string Texto, string Signo, int Orden);

/// <summary>
/// Reglas por concepto para las líneas del extracto que no corresponden a ningún apunte (comisiones, cuotas, recibos de suministros…):
/// la primera regla (por orden) cuyo texto aparece en el concepto, sin distinguir mayúsculas ni acentos, y cuyo signo coincide.
/// </summary>
public static class ReglasConciliacion
{
    public static string Normalizar(string? s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToUpperInvariant(c));
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool Coincide(ReglaConciliacion r, long cuentaBancariaId, decimal importe, string? concepto)
        => (r.CuentaBancariaId is null || r.CuentaBancariaId == cuentaBancariaId)
           && (r.Signo == "AMBOS" || (r.Signo == "CARGO" ? importe < 0 : importe > 0))
           && Normalizar(concepto).Contains(Normalizar(r.Texto), StringComparison.Ordinal);

    public static ReglaConciliacion? Primera(IEnumerable<ReglaConciliacion> reglas, long cuentaBancariaId, decimal importe, string? concepto)
        => reglas.OrderBy(r => r.Orden).ThenBy(r => r.Id).FirstOrDefault(r => Coincide(r, cuentaBancariaId, importe, concepto));
}
