namespace Contabilidad.Domain.Facturacion;

/// <summary>
/// Convierte una factura calculada + una plantilla en los apuntes del asiento.
///  - CUOTA / RECARGO se expanden UNA POR TIPO de IVA/IGIC (cada tipo puede tener su cuenta).
///    En facturas recibidas, CUOTA es la cuota deducible.
///  - BASE, CUOTA_NO_DEDUCIBLE, RETENCION, TOTAL: importes agregados de la factura.
///  - EXPRESION: fórmula con BASE, CUOTA, CUOTA_DEDUCIBLE, CUOTA_NO_DEDUCIBLE, RECARGO, RETENCION, TOTAL.
///  - Factura en negativo (rectificativa): importes absolutos y Debe/Haber invertidos.
///  - Cuota autorrepercutida (adquisición intracomunitaria, inversión del sujeto pasivo): además de lo que diga la plantilla
///    (472 por la parte deducible, gasto por la no deducible), se abona por tipo a la cuenta del rol IVA_REPERCUTIDO (477).
/// </summary>
public static class GeneradorAsientoFactura
{
    public static IReadOnlyList<ApunteCmd> Generar(
        LibroFactura libro, FacturaCalculada f, IEnumerable<LineaPlantilla> plantilla, ResolutorCuentas cuentas,
        long terceroId, long? actividadId, long canalFactura,
        string terceroNombre, string numeroFactura, DateOnly fecha, string? conceptoModelo)
    {
        var t = f.Totales;
        var invertir = t.Base < 0 || (t.Base == 0 && t.Total < 0);

        string Sustituir(string s) => s.Replace("{tercero}", terceroNombre)
                                       .Replace("{factura}", numeroFactura)
                                       .Replace("{fecha}", fecha.ToString("dd/MM/yyyy"));
        var concepto = Sustituir(conceptoModelo ?? "Factura {factura} {tercero}");

        var vars = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["BASE"] = Math.Abs(t.Base),
            ["CUOTA"] = Math.Abs(t.Cuota),
            ["CUOTA_DEDUCIBLE"] = Math.Abs(t.CuotaDeducible),
            ["CUOTA_NO_DEDUCIBLE"] = Math.Abs(t.CuotaNoDeducible),
            ["RECARGO"] = Math.Abs(t.Recargo),
            ["RETENCION"] = Math.Abs(t.Retencion),
            ["TOTAL"] = Math.Abs(t.Total)
        };

        var apuntes = new List<ApunteCmd>();

        void Añade(LineaPlantilla l, long cuentaId, decimal importe)
        {
            importe = Math.Round(Math.Abs(importe), 2, MidpointRounding.AwayFromZero);
            if (importe == 0m) return;
            var esDebe = (l.Lado == "DEBE") ^ invertir;
            apuntes.Add(new ApunteCmd
            {
                CuentaId = cuentaId,
                TerceroId = l.UsaTercero ? terceroId : null,
                ActividadId = actividadId,
                CanalId = l.CanalId ?? canalFactura,
                Debe = esDebe ? importe : 0m,
                Haber = esDebe ? 0m : importe,
                Concepto = l.Concepto is null ? concepto : Sustituir(l.Concepto)
            });
        }

        foreach (var l in plantilla.OrderBy(x => x.Orden))
        {
            if (l.OrigenImporte is "CUOTA" or "RECARGO")
            {
                foreach (var row in f.Lineas)
                {
                    var imp = l.OrigenImporte == "CUOTA"
                        ? (libro == LibroFactura.Recibidas ? row.CuotaDeducible : row.Cuota)
                        : row.CuotaRecargo;
                    if (imp == 0m) continue;
                    Añade(l, cuentas.Resolver(l.CuentaId, l.CuentaRol, row.TipoIvaId), imp);
                }
                continue;
            }

            var importe = l.OrigenImporte switch
            {
                "BASE" => vars["BASE"],
                "CUOTA_NO_DEDUCIBLE" => vars["CUOTA_NO_DEDUCIBLE"],
                "RETENCION" => vars["RETENCION"],
                "TOTAL" => vars["TOTAL"],
                "EXPRESION" => Canales.ExpresionEvaluador.Evaluar(l.Expresion ?? "", vars),
                _ => throw new ReglaContableException($"Origen de importe no soportado en facturas: {l.OrigenImporte}")
            };
            if (importe == 0m) continue;

            long? tipoRet = l.OrigenImporte == "RETENCION" ? f.Retencion?.TipoRetencionId : null;
            Añade(l, cuentas.Resolver(l.CuentaId, l.CuentaRol, null, tipoRet), importe);
        }

        if (libro == LibroFactura.Recibidas)
        {
            var autorrepercusion = new LineaPlantilla(short.MaxValue, null, "IVA_REPERCUTIDO", "HABER", "CUOTA", null, false, null, null);
            foreach (var row in f.Lineas.Where(r => OperacionesIva.Autorrepercutida(libro, r.TipoOperacion) && r.Cuota != 0))
                Añade(autorrepercusion, cuentas.Resolver(null, "IVA_REPERCUTIDO", row.TipoIvaId), row.Cuota);
        }

        return apuntes;
    }
}
