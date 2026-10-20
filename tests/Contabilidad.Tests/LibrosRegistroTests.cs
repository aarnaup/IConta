using System.Text;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class LibrosRegistroTests
{
    private static LineaLibroDto L(string numero, decimal pct, decimal @base, decimal cuota, bool primera = true, string op = "INTERIOR",
        decimal recargoPct = 0, decimal recargo = 0, decimal deducible = 0, int? registro = null, string nombre = "Cliente S.L.")
        => new()
        {
            FacturaId = 1, NumeroRegistro = registro, FechaExpedicion = new DateOnly(2026, 7, 15), FechaContable = new DateOnly(2026, 7, 20),
            Numero = numero, TipoFactura = "ORDINARIA", Nif = "B12345678", Nombre = nombre, TipoOperacion = op, Base = @base, Porcentaje = pct,
            Cuota = cuota, PorcentajeRecargo = recargoPct, CuotaRecargo = recargo, CuotaDeducible = deducible, PrimeraLinea = primera,
            TotalFactura = primera ? @base + cuota : null, AsientoNumero = 7
        };

    [Fact]
    public void Totales_por_tipo_de_operacion_y_porcentaje()
    {
        var t = LibrosRegistro.Totales([L("1", 21, 100, 21), L("2", 21, 200, 42), L("2", 10, 50, 5, primera: false), L("3", 0, 80, 0, op: "EXENTA"),
                                        L("4", 21, 100, 21, recargoPct: 5.2m, recargo: 5.2m)]);
        Assert.Equal(4, t.Count);
        var g21 = t.Single(x => x.TipoOperacion == "INTERIOR" && x.Porcentaje == 21 && x.PorcentajeRecargo == 0);
        Assert.Equal((2, 300m, 63m), (g21.Lineas, g21.Base, g21.Cuota));
        Assert.Equal(5.2m, t.Single(x => x.PorcentajeRecargo == 5.2m).CuotaRecargo);
        Assert.Equal("EXENTA", t[0].TipoOperacion);                         // ordenado por tipo de operación
    }

    [Fact]
    public void Csv_de_expedidas_en_formato_espanol()
    {
        var bytes = LibrosRegistro.Csv("EMITIDAS", "IVA", [L("F-1", 21, 1234.5m, 259.25m)]);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);              // BOM para Excel
        var lineas = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lineas.Length);
        Assert.StartsWith("Fecha expedición;Fecha operación;Serie;Número;", lineas[0]);
        Assert.Contains("Tipo IVA %", lineas[0]);
        var c = lineas[1].Split(';');
        Assert.Equal("15/07/2026", c[0]);
        Assert.Equal("", c[1]);                                              // sin fecha de operación
        Assert.Equal("F-1", c[3]);
        Assert.Equal("1234,50", c[8]);
        Assert.Equal("21,00", c[9]);
        Assert.Equal("259,25", c[10]);
        Assert.Equal("1493,75", c[14]);
    }

    [Fact]
    public void Csv_de_recibidas_lleva_numero_y_fecha_de_registro_y_deducible()
    {
        var texto = Encoding.UTF8.GetString(LibrosRegistro.Csv("RECIBIDAS", "IGIC", [L("P-9", 7, 400, 28, deducible: 28, registro: 3)])[3..]);
        var lineas = texto.Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Nº registro;Fecha registro;", lineas[0]);
        Assert.Contains("Cuota IGIC soportada", lineas[0]);
        var c = lineas[1].Split(';');
        Assert.Equal(("3", "20/07/2026"), (c[0], c[1]));
        Assert.Equal("28,00", c[15]);                                        // cuota deducible
        Assert.Equal("No", c[16]);                                           // bien de inversión
    }

    [Fact]
    public void Los_campos_con_punto_y_coma_o_comillas_se_entrecomillan()
    {
        var texto = Encoding.UTF8.GetString(LibrosRegistro.Csv("EMITIDAS", "IVA", [L("1", 21, 1, 0.21m, nombre: "Pérez; \"El Rápido\" S.L.")])[3..]);
        Assert.Contains(";\"Pérez; \"\"El Rápido\"\" S.L.\";", texto);
    }

    [Fact]
    public void En_la_segunda_linea_de_una_factura_no_se_repite_el_total()
    {
        var texto = Encoding.UTF8.GetString(LibrosRegistro.Csv("EMITIDAS", "IVA", [L("1", 21, 100, 21), L("1", 10, 50, 5, primera: false)])[3..]);
        var segunda = texto.Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)[2].Split(';');
        Assert.Equal("", segunda[14]);
    }
}
