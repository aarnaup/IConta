using System.Text;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;

namespace Contabilidad.Tests;

public class FicheroAeatTests
{
    private static string Texto(byte[] b) => Encoding.Latin1.GetString(b);

    private static DeclaracionParaFichero Decl(string modelo, string periodo, IReadOnlyList<Casilla> casillas, decimal resultado,
        Dictionary<string, IReadOnlyList<RegistroAeat>>? registros = null, string tipo = "ORDINARIA", int anio = 2026, decimal aDevolver = 0)
        => new(42, modelo, anio, periodo, tipo, "B12345678", "Ñandú Café, S.L.", casillas, resultado, aDevolver,
            tipo == "ORDINARIA" ? null : "3031234567890", registros ?? []);

    [Fact]
    public void Numeros_con_ceros_sin_decimales_y_N_si_son_negativos()
    {
        Assert.Equal("00000000000001250", GeneradorFicheroAeat.Numero(12.5m, 14, 2, true));
        Assert.Equal("N0000000000001250", GeneradorFicheroAeat.Numero(-12.5m, 14, 2, true));
        Assert.Equal("0000000000001250", GeneradorFicheroAeat.Numero(12.5m, 14, 2, false));
        Assert.Equal("00012", GeneradorFicheroAeat.Numero(12m, 5, 0, false));
        Assert.Throws<ReglaContableException>(() => GeneradorFicheroAeat.Numero(-1m, 5, 0, false));
        Assert.Throws<ReglaContableException>(() => GeneradorFicheroAeat.Numero(1_000_000m, 5, 0, false));
    }

    [Fact]
    public void Textos_en_mayusculas_sin_acentos_conservando_la_enie()
    {
        Assert.Equal("ÑANDU CAFE, S.L.    ", GeneradorFicheroAeat.Texto("Ñandú Café, S.L.", 20));
        Assert.Equal("MULLER 1º->MULLER 1O", "MULLER 1º->" + GeneradorFicheroAeat.Texto("Müller 1º", 9).Trim());
        Assert.Equal("ABC", GeneradorFicheroAeat.Texto("a\"b·c", 3));
        Assert.Equal("  12", GeneradorFicheroAeat.Texto("12", 4, derecha: true));
    }

    [Fact]
    public void El_303_tiene_su_cabecera_paginas_y_casillas_en_su_sitio()
    {
        var c = new List<Casilla>
        {
            new("07", "", 1000m), new("08", "", 21m), new("09", "", 210m), new("27", "", 210m), new("28", "", 500m), new("29", "", 105m),
            new("45", "", 105m), new("46", "", 105m), new("64", "", 105m), new("65", "", 100m), new("66", "", 105m),
            new("69", "", 105m), new("71", "", 105m),
        };
        var (b, nombre) = FicheroModelo.Generar(Decl("303", "1T", c, 105m), new FicheroAeatCmd(), "B00000000");
        var t = Texto(b);
        Assert.Equal("B12345678_303_2026_1T.txt", nombre);
        Assert.Equal(FicheroModelo.Diseno("303", 2026).Longitud, t.Length);
        Assert.StartsWith("<T30302026" + "1T" + "0000><AUX>", t);
        Assert.Contains("1.00", t[..120]);
        var p1 = t.IndexOf("<T30301000>", StringComparison.Ordinal);
        Assert.True(p1 > 0);
        // Página 1: tipo I, NIF, nombre a 80, ejercicio y periodo
        Assert.Equal("I", t.Substring(p1 + 12, 1));
        Assert.Equal("B12345678", t.Substring(p1 + 13, 9));
        Assert.StartsWith("ÑANDU CAFE, S.L.", t.Substring(p1 + 22, 80));
        Assert.Equal("20261T", t.Substring(p1 + 102, 6));
        // Base al 21 % [07], tipo [08] y cuota [09]
        Assert.Contains("00000000000100000" + "02100" + "00000000000021000", t);
        Assert.Contains("</T30301000>", t);
        Assert.Contains("<T30303000>", t);
        Assert.Contains("<T303DID00>", t);
        Assert.EndsWith("</T3030" + "2026" + "1T" + "0000>", t);
        Assert.DoesNotContain("<T30304000>", t);   // la página 4 solo para exonerados del 390 en el último periodo
    }

    [Fact]
    public void El_tipo_de_declaracion_depende_del_resultado()
    {
        var cmd = new FicheroAeatCmd();
        Assert.Equal("I", FicheroModelo.Tipo(Decl("303", "1T", [new("71", "", 10m)], 10m), cmd));
        Assert.Equal("U", FicheroModelo.Tipo(Decl("303", "1T", [new("71", "", 10m)], 10m), new FicheroAeatCmd { Domiciliar = true }));
        Assert.Equal("C", FicheroModelo.Tipo(Decl("303", "2T", [new("71", "", -10m)], -10m), cmd));
        Assert.Equal("D", FicheroModelo.Tipo(Decl("303", "4T", [new("71", "", -10m)], -10m, aDevolver: 10m), cmd));
        Assert.Equal("N", FicheroModelo.Tipo(Decl("111", "1T", [new("30", "", 0m)], 0m), cmd));
        Assert.Equal("B", FicheroModelo.Tipo(Decl("130", "2T", [new("19", "", -5m)], -5m), cmd));
        Assert.Equal("N", FicheroModelo.Tipo(Decl("130", "4T", [new("19", "", -5m)], -5m), cmd));
    }

    [Fact]
    public void El_111_y_el_115_son_de_una_pagina_con_la_cabecera_auxiliar()
    {
        var (b, _) = FicheroModelo.Generar(Decl("111", "1T", [new("07", "", 2m), new("08", "", 3300m), new("09", "", 495m), new("28", "", 495m),
            new("30", "", 495m)], 495m), new FicheroAeatCmd { Domiciliar = true, Iban = "ES91 2100 0418 4502 0005 1332" });
        var t = Texto(b);
        Assert.Equal(1348, t.Length);
        Assert.EndsWith("\r\n", t);
        var p = t.IndexOf("<T11101000>", StringComparison.Ordinal);
        Assert.Equal("U", t.Substring(p + 12, 1));
        Assert.Contains("ES9121000418450200051332", t);
        Assert.Equal(848, FicheroModelo.Generar(Decl("115", "1T", [new("05", "", 0m)], 0m), new FicheroAeatCmd()).Contenido.Length);
    }

    [Fact]
    public void La_complementaria_lleva_la_marca_y_el_justificante_anterior()
    {
        var (b, _) = FicheroModelo.Generar(Decl("111", "1T", [new("30", "", 10m)], 10m, tipo: "COMPLEMENTARIA"), new FicheroAeatCmd());
        Assert.Contains("X3031234567890", Texto(b));
    }

    [Fact]
    public void El_347_lleva_un_registro_de_500_por_tercero()
    {
        var reg = new Dictionary<string, IReadOnlyList<RegistroAeat>>
        {
            ["registros"] =
            [
                new() { ["nif"] = "A11111111", ["nombre"] = "Cliente Uno", ["provincia"] = "28", ["clave"] = "B", ["importe"] = 12100m,
                        ["t1"] = 12100m, ["t2"] = 0m, ["t3"] = 0m, ["t4"] = 0m, ["ejercicioMetalico"] = "0000" },
                new() { ["nif"] = "B22222222", ["nombre"] = "Proveedor Dos", ["provincia"] = "08", ["clave"] = "A", ["importe"] = 4000m,
                        ["t1"] = 0m, ["t2"] = 4000m, ["t3"] = 0m, ["t4"] = 0m, ["ejercicioMetalico"] = "0000" },
            ],
        };
        var (b, _) = FicheroModelo.Generar(Decl("347", "0A", [new("N", "", 2m)], 0m, reg, anio: 2025),
            new FicheroAeatCmd { Telefono = "600 111 222", Contacto = "Ana Pérez" });
        var t = Texto(b);
        Assert.Equal(1500, t.Length);
        Assert.StartsWith("13472025B12345678ÑANDU CAFE, S.L.", t);
        Assert.Equal("T", t.Substring(57, 1));                  // soporte telemático
        Assert.Equal("600111222", t.Substring(58, 9));
        Assert.StartsWith("ANA PEREZ ", t.Substring(67, 40));
        Assert.Equal("3470000000042", t.Substring(107, 13));
        Assert.Equal("000000002", t.Substring(135, 9));        // número de terceros
        Assert.Equal(" 000000001610000", t.Substring(144, 16)); // importe total con signo en blanco
        var r1 = t.Substring(500, 500);
        Assert.StartsWith("23472025B12345678A11111111", r1);
        Assert.Equal("D28", r1.Substring(75, 3));
        Assert.Equal("B", r1.Substring(81, 1));
        Assert.Equal(" 000000001210000", r1.Substring(82, 16));
    }

    [Fact]
    public void El_349_separa_operaciones_y_rectificaciones()
    {
        var reg = new Dictionary<string, IReadOnlyList<RegistroAeat>>
        {
            ["registros"] = [new() { ["nif"] = "FR12345678901", ["nombre"] = "Client FR", ["clave"] = "E", ["base"] = 5000m }],
            ["rectificaciones"] = [new() { ["nif"] = "DE123456789", ["nombre"] = "Lieferant", ["clave"] = "A", ["ejercicio"] = "2026",
                                           ["periodo"] = "1T", ["base"] = 900m, ["baseAnterior"] = 1000m }],
        };
        var (b, _) = FicheroModelo.Generar(Decl("349", "2T", [new("01", "", 1m), new("02", "", 5000m), new("03", "", 1m), new("04", "", 900m)], 0m, reg),
            new FicheroAeatCmd());
        var t = Texto(b);
        Assert.Equal(1500, t.Length);
        Assert.Equal("2T", t.Substring(135, 2));
        Assert.Equal("FR12345678901    ", t.Substring(500 + 75, 17));
        Assert.Equal("E0000000500000", t.Substring(500 + 132, 14));
        Assert.Equal("20261T", t.Substring(1000 + 146, 6));
    }

    [Fact]
    public void El_190_tiene_registros_de_502_con_salto_de_linea()
    {
        var reg = new Dictionary<string, IReadOnlyList<RegistroAeat>>
        {
            ["registros"] = [new() { ["nif"] = "12345678Z", ["nombre"] = "Abogada Uno", ["provincia"] = "28", ["clave"] = "G", ["subclave"] = "01",
                                     ["signoBase"] = " ", ["base"] = 3300m, ["retencion"] = 495m }],
        };
        var (b, _) = FicheroModelo.Generar(Decl("190", "0A", [new("01", "", 1m), new("02", "", 3300m), new("03", "", 495m)], 0m, reg, anio: 2025),
            new FicheroAeatCmd());
        var t = Texto(b);
        Assert.Equal(1004, t.Length);
        Assert.Equal("\r\n", t.Substring(500, 2));
        Assert.Equal("G01 0000000330000", t.Substring(502 + 77, 17));
    }

    [Fact]
    public void Sin_diseno_para_el_ano_avisa()
    {
        Assert.Throws<ReglaContableException>(() => FicheroModelo.Diseno("303", 2024));
        Assert.Throws<ReglaContableException>(() => FicheroModelo.Diseno("390", 2024));
        Assert.Throws<ReglaContableException>(() => FicheroModelo.Diseno("180", 2025));
    }
}
