using System.Text;
using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;

namespace Contabilidad.Tests;

public class LectorNorma43Tests
{
    // Construye registros de 80 posiciones como los de un fichero real
    private static string R11(string entidad, string oficina, string cuenta, string desde, string hasta, char clave, long saldoCent)
        => $"11{entidad}{oficina}{cuenta}{desde}{hasta}{clave}{saldoCent:D14}9783EMPRESA DEMO              ".PadRight(80)[..80];
    private static string R22(string fecha, string valor, string comun, char clave, long cent, string doc, string ref1, string ref2)
        => $"22    0001{fecha}{valor}{comun}000{clave}{cent:D14}{doc,-10}{ref1,-12}{ref2,-16}".PadRight(80)[..80];
    private static string R23(string a, string b) => $"2301{a,-38}{b,-38}".PadRight(80)[..80];
    private static string R33(string entidad, string oficina, string cuenta, int nDebe, long debe, int nHaber, long haber, char clave, long saldo)
        => $"33{entidad}{oficina}{cuenta}{nDebe:D5}{debe:D14}{nHaber:D5}{haber:D14}{clave}{saldo:D14}978    ".PadRight(80)[..80];

    private static byte[] Fichero(params string[] registros) => Encoding.Latin1.GetBytes(string.Join("\r\n", registros) + "\r\n");

    private static readonly string[] Basico =
    [
        R11("2100", "0418", "0200051332", "260701", "260731", '2', 100000),
        R22("260705", "260705", "04", '2', 107000, "0000000001", "F-1", "CLIENTE DEMO"),
        R23("TRANSFERENCIA DE DEMO CLIENTE SL", "FACTURA F-1"),
        R22("260710", "260709", "17", '1', 1250, "", "", "COMISION"),
        R33("2100", "0418", "0200051332", 1, 1250, 1, 107000, '2', 205750),
        "88" + new string('9', 18) + "000005".PadRight(60)
    ];

    [Fact]
    public void Lee_cabecera_movimientos_y_saldos()
    {
        var r = LectorExtractos.Leer(Fichero(Basico));
        Assert.Equal("NORMA43", r.Formato);
        var e = Assert.Single(r.Extractos);
        Assert.Equal(("2100", "0418", "0200051332"), (e.Entidad, e.Oficina, e.Cuenta));
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)), (e.Desde, e.Hasta));
        Assert.Equal((1000m, 2057.5m), (e.SaldoInicial, e.SaldoFinal));
        Assert.Equal(2, e.Lineas.Count);
        var abono = e.Lineas[0];
        Assert.Equal(1070m, abono.Importe);
        Assert.Equal("04", abono.ConceptoComun);
        Assert.Equal("0000000001", abono.Documento);
        Assert.Contains("TRANSFERENCIA DE DEMO CLIENTE SL", abono.Concepto);
        Assert.Contains("FACTURA F-1", abono.Concepto);
        var cargo = e.Lineas[1];
        Assert.Equal(-12.5m, cargo.Importe);
        Assert.Equal(new DateOnly(2026, 7, 9), cargo.FechaValor);
        Assert.Empty(r.Avisos);
    }

    [Fact]
    public void Reconoce_el_iban_de_la_cuenta()
    {
        var e = LectorExtractos.Leer(Fichero(Basico)).Extractos[0];
        Assert.True(e.EsDeIban("ES91 2100 0418 4502 0005 1332"));
        Assert.False(e.EsDeIban("ES7921000813610123456789"));
    }

    [Fact]
    public void Totales_que_no_cuadran_rechazan_el_fichero()
    {
        var mal = (string[])Basico.Clone();
        mal[4] = R33("2100", "0418", "0200051332", 1, 1250, 1, 999999, '2', 205750);
        var ex = Assert.Throws<ReglaContableException>(() => LectorExtractos.Leer(Fichero(mal)));
        Assert.Contains("registro 33", ex.Message);
    }

    [Fact]
    public void Un_saldo_final_incoherente_solo_avisa()
    {
        var mal = (string[])Basico.Clone();
        mal[4] = R33("2100", "0418", "0200051332", 1, 1250, 1, 107000, '2', 300000);
        Assert.Single(LectorExtractos.Leer(Fichero(mal)).Avisos);
    }

    [Fact]
    public void Fichero_sin_registro_33_se_rechaza()
        => Assert.Throws<ReglaContableException>(() => LectorExtractos.Leer(Fichero(Basico[0], Basico[1])));

    [Fact]
    public void Los_acentos_en_latin1_se_leen_bien()
    {
        var regs = (string[])Basico.Clone();
        regs[2] = R23("RECIBO ENERGÍA ELÉCTRICA", "");
        Assert.Contains("ENERGÍA ELÉCTRICA", LectorExtractos.Leer(Fichero(regs)).Extractos[0].Lineas[0].Concepto);
    }
}

public class LectorCsvTests
{
    private static ResultadoLectura Leer(string texto) => LectorExtractos.Leer(Encoding.UTF8.GetBytes(texto));

    [Fact]
    public void Csv_espanol_con_importe_y_saldo()
    {
        var r = Leer("Fecha;Fecha valor;Concepto;Importe;Saldo\n05/07/2026;05/07/2026;Transferencia cliente;1.070,00;2.070,00\n10/07/2026;09/07/2026;Comisión mantenimiento;-12,50;2.057,50\n");
        Assert.Equal("CSV", r.Formato);
        var e = r.Extractos[0];
        Assert.Equal([1070m, -12.5m], e.Lineas.Select(l => l.Importe));
        Assert.Equal((1000m, 2057.5m), (e.SaldoInicial, e.SaldoFinal));
        Assert.Equal("Comisión mantenimiento", e.Lineas[1].Concepto);
    }

    [Fact]
    public void Csv_con_cargo_y_abono_ordenado_de_reciente_a_antiguo()
    {
        var r = Leer("fecha,descripción,cargo,abono\n2026-07-10,Comision,12.50,\n2026-07-05,\"Transferencia, cliente\",,1070.00\n");
        var e = r.Extractos[0];
        Assert.Equal([new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 10)], e.Lineas.Select(l => l.Fecha));
        Assert.Equal([1070m, -12.5m], e.Lineas.Select(l => l.Importe));
        Assert.Equal("Transferencia, cliente", e.Lineas[0].Concepto);
        Assert.Null(e.SaldoInicial);
    }

    [Fact]
    public void Csv_sin_columnas_reconocibles_se_rechaza()
        => Assert.Throws<ReglaContableException>(() => Leer("dia;texto;euros\n01/01/2026;x;1\n"));
}

public class ConciliadorTests
{
    private static readonly DateOnly D = new(2026, 7, 10);
    private static LineaExtracto E(int dia, decimal imp, string concepto = "X") => new(D.AddDays(dia), null, imp, concepto);

    [Fact]
    public void Reimportar_no_duplica_pero_dos_lineas_iguales_si_se_importan()
    {
        var fichero = new[] { E(0, -2m, "COMISION"), E(0, -2m, "COMISION"), E(1, 100m) };
        Assert.Equal(3, Conciliador.Nuevas(fichero, []).Count);
        Assert.Empty(Conciliador.Nuevas(fichero, fichero.Select(l => l.Clave)));
        Assert.Single(Conciliador.Nuevas(fichero, [fichero[0].Clave, fichero[2].Clave]));   // falta una de las comisiones
    }

    private static LineaPendiente L(long id, int dia, decimal imp) => new(id, D.AddDays(dia), imp, "");
    private static ApunteBanco A(long id, int dia, decimal imp) => new(id, D.AddDays(dia), imp, "");

    [Fact]
    public void Casa_por_importe_y_fecha_cercana()
    {
        var r = Conciliador.Automatica([L(1, 0, 1070m), L(2, 2, -12.5m)], [A(10, -1, 1070m), A(11, 2, -12.5m), A(12, 0, 500m)], 5);
        Assert.Equal([(1L, 10L), (2L, 11L)], r);
    }

    [Fact]
    public void Fuera_del_margen_no_casa()
        => Assert.Empty(Conciliador.Automatica([L(1, 0, 100m)], [A(10, 8, 100m)], 5));

    [Fact]
    public void Elige_el_apunte_mas_cercano()
        => Assert.Equal([(1L, 11L)], Conciliador.Automatica([L(1, 0, 100m)], [A(10, -4, 100m), A(11, 1, 100m)], 5));

    [Fact]
    public void Dos_apuntes_igual_de_cerca_en_fechas_distintas_no_se_casan()
        => Assert.Empty(Conciliador.Automatica([L(1, 0, 100m)], [A(10, -2, 100m), A(11, 2, 100m)], 5));

    [Fact]
    public void Lineas_y_apuntes_identicos_son_intercambiables()
    {
        var r = Conciliador.Automatica([L(1, 0, -2m), L(2, 0, -2m)], [A(10, 0, -2m), A(11, 0, -2m)], 5);
        Assert.Equal(2, r.Count);
        Assert.Equal(2, r.Select(x => x.ApunteId).Distinct().Count());
    }

    [Fact]
    public void Si_otra_linea_encaja_mejor_con_el_apunte_se_deja_para_ella()
    {
        // La línea del día 0 tiene el apunte a 3 días, pero la del día 3 lo tiene el mismo día
        var r = Conciliador.Automatica([L(1, 0, 50m), L(2, 3, 50m)], [A(10, 3, 50m)], 5);
        Assert.Equal([(2L, 10L)], r);
    }
}
