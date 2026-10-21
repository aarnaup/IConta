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
        Assert.Throws<ReglaContableException>(() => FicheroModelo.Diseno("200", 2024));
        Assert.Throws<ReglaContableException>(() => FicheroModelo.Diseno("425", 2025));
    }

    // ------------------------------------------------------------------------------------------------ 180
    [Fact]
    public void El_180_lleva_declarante_y_un_registro_de_500_posiciones_por_arrendador_e_inmueble()
    {
        var inmueble = new InmuebleDto
        {
            Situacion = 1, ReferenciaCatastral = "9872023VH5797S0001WX", TipoVia = "CL", NombreVia = "Mayor", Numero = "12 bis",
            Complemento = "3º B", Municipio = "Madrid", CodigoMunicipio = "28079", CodigoPostal = "28013",
        };
        var p = new PerceptorResumen("ES12345678Z", "García López, Ana", "28", "", null, 19m, 12_000m, 2_280m, Inmueble: inmueble);
        var reg = Diseno180.Registro(p, inmueble);
        var sinRef = Diseno180.Registro(p with { Nif = "11111111H", Base = -100m, Retencion = -19m },
            new InmuebleDto { Situacion = 4, ReferenciaCatastral = "X", NombreVia = "Sin número", Numero = "S/N", CodigoPostal = "08001" });
        var (b, nombre) = FicheroModelo.Generar(Decl("180", "0A", [], 0m, new() { ["registros"] = [reg, sinRef] }, anio: 2025),
            new FicheroAeatCmd { Telefono = "910 000 000", Contacto = "Pérez Ruiz, Juan" });
        var lineas = Texto(b).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("B12345678_180_2025_0A.txt", nombre);
        Assert.Equal(3, lineas.Length);
        Assert.All(lineas, l => Assert.Equal(500, l.Length));
        var d = lineas[0];
        Assert.Equal("11802025B12345678", d[..17]);
        Assert.Equal("T910000000PEREZ RUIZ, JUAN", d.Substring(57, 26));
        Assert.Equal("180", d.Substring(107, 3));
        Assert.Equal("000000002", d.Substring(135, 9));           // número de perceptores
        Assert.Equal(" 000000001190000", d.Substring(144, 16));   // base total 11.900,00 (12.000 − 100)
        Assert.Equal("000000000229900", d.Substring(160, 15));    // retenciones 2.280 + 19 (sin signo)
        var r = lineas[1];
        Assert.Equal("21802025B1234567812345678Z", r[..26]);
        Assert.StartsWith("GARCIA LOPEZ, ANA", r.Substring(35, 40));
        Assert.Equal("281", r.Substring(75, 3));                  // provincia y modalidad dineraria
        Assert.Equal(" 0000001200000", r.Substring(78, 14));
        Assert.Equal("1900", r.Substring(92, 4));
        Assert.Equal("0000000228000", r.Substring(96, 13));
        Assert.Equal("00001", r.Substring(109, 5));               // ejercicio de devengo a ceros y situación 1
        Assert.Equal("9872023VH5797S0001WX", r.Substring(114, 20));
        Assert.Equal("CL   MAYOR", r.Substring(134, 10));
        Assert.Equal("NUM12   ", r.Substring(189, 8));
        Assert.StartsWith("BIS 3O B", r.Substring(215, 40));
        Assert.StartsWith("MADRID", r.Substring(285, 30));
        Assert.Equal("280792828013", r.Substring(315, 12));
        var n = lineas[2];
        Assert.Equal("N0000000010000", n.Substring(78, 14));     // reintegro: base negativa con N
        Assert.Equal("4" + new string(' ', 20), n.Substring(113, 21)); // sin referencia catastral
        Assert.Equal("S/N     ", n.Substring(189, 8));
        Assert.Equal("08", n.Substring(320, 2));
    }

    // ------------------------------------------------------------------------------------------------ 202
    [Fact]
    public void El_202_de_la_modalidad_del_articulo_40_3_rellena_sus_dos_paginas()
    {
        var c = new DeclaracionCalculada { Modelo = "202" };
        CalculadorSociedades.Calcular202(new Entrada202("ART_40_3", new("GENERAL"), 2025, 0, 60_000m, 10_000m, 0, 500m, 2_000m), c);
        var d = new DeclaracionParaFichero(7, "202", 2025, "2P", "ORDINARIA", "B12345678", "Ñandú Café, S.L.", c.Casillas, c.Resultado, 0, null, new Dictionary<string, IReadOnlyList<RegistroAeat>>(),
            Inicio: new DateOnly(2025, 1, 1), Fin: new DateOnly(2025, 9, 30),
            Extra: new Dictionary<string, object?> { ["D:incn"] = 7_000_000m, ["D:tipoGravamen202"] = "25", ["D:reducidaDimension202"] = "" });
        var (b, nombre) = FicheroModelo.Generar(d, new FicheroAeatCmd { Domiciliar = true, Iban = "ES91 2100 0418 4502 0005 1332", Cnae = "6201" });
        var t = Texto(b);
        Assert.Equal("B12345678_202_2025_2P.txt", nombre);
        Assert.Equal(346 + 700 + 900, t.Length);
        Assert.StartsWith("<T202020252P0000><AUX>", t);
        Assert.EndsWith("</T202020252P0000>", t);
        var p1 = t.IndexOf("<T20201000>", StringComparison.Ordinal);
        Assert.Equal("U", t.Substring(p1 + 12, 1));
        Assert.Equal("B12345678", t.Substring(p1 + 13, 9));
        Assert.Equal("20252P01012025" + "6201", t.Substring(p1 + 103, 18));
        Assert.Equal("X", t.Substring(p1 + 126, 1));              // cifra de negocios de más de 6 M€
        Assert.StartsWith("25 ", t.Substring(p1 + 131, 15));
        Assert.Equal("0B", t.Substring(p1 + 146, 2));             // tramo de cifra de negocios y modalidad 40.3
        Assert.Equal("00000000006000000", t.Substring(p1 + 199, 17));   // [04] resultado contable
        Assert.Equal("00000000006000000", t.Substring(p1 + 352, 17));   // [13] base imponible previa
        Assert.Equal("00000000001000000", t.Substring(p1 + 386, 17));   // [14] compensación
        var p2 = t.IndexOf("<T20202000>", StringComparison.Ordinal);
        Assert.Equal("00000000005000000" + "01700", t.Substring(p2 + 12, 22));   // [16] base y [17] 17 %
        Assert.Equal("00000000000850000", t.Substring(p2 + 102, 17));  // [18] resultado previo
        Assert.Equal("00000000000050000", t.Substring(p2 + 394, 17));  // [28] retenciones
        Assert.Equal("10000", t.Substring(p2 + 411, 5));               // [29] 100 % en territorio común
        Assert.Equal("00000000000600000", t.Substring(p2 + 484, 17));  // [34] a ingresar
        Assert.StartsWith("ES9121000418450200051332", t.Substring(p2 + 725, 34));
    }

    // ------------------------------------------------------------------------------------------------ 200
    [Fact]
    public void El_balance_del_200_reparte_las_cuentas_por_casilla_con_gastos_en_negativo()
    {
        var saldos = new List<Contabilidad.Domain.Balances.SaldoBalance>
        {
            new("2170000", "Equipos", 10_000m), new("2817000", "Amort.", -2_000m), new("4300001", "Cliente", 5_000m), new("5720001", "Banco", 9_000m),
            new("1000000", "Capital", -8_000m), new("1130000", "Reservas", -1_000m), new("4000001", "Proveedor", -2_500m),
            new("4750000", "HP IVA", -500m), new("7000000", "Ventas", -40_000m), new("7050000", "Servicios", -10_000m),
            new("6000000", "Compras", 20_000m), new("6400000", "Sueldos", 12_000m), new("6420000", "SS", 4_000m), new("6230000", "Asesoría", 1_000m),
            new("6290000", "Otros", 500m), new("6810000", "Amortización", 2_000m), new("6620000", "Intereses", 500m),
        };
        var c = Estados200.Casillas(saldos, out var sinClasificar);
        Assert.Empty(sinClasificar);
        Assert.Equal(8_000m, c["B:00113"]);
        Assert.Equal(8_000m, c["B:00101"]);
        Assert.Equal(5_000m, c["B:00152"]);
        Assert.Equal(9_000m, c["B:00178"]);
        Assert.Equal(22_000m, c["B:00180"]);
        Assert.Equal(10_000m, c["B:00199"]);                     // resultado del ejercicio antes de regularizar
        Assert.Equal(c["B:00180"], c["B:00252"]);                // el balance cuadra
        Assert.Equal(50_000m, c["B:00255"]);
        Assert.Equal(-20_000m, c["B:00260"]);
        Assert.Equal(-16_000m, c["B:00270"]);
        Assert.Equal(-1_000m, c["B:00253"]);
        Assert.Equal(-1_500m, c["B:00279"]);
        Assert.Equal(10_500m, c["B:00296"]);
        Assert.Equal(-500m, c["B:00307"]);
        Assert.Equal(10_000m, c["B:00325"]);
        Assert.Equal(10_000m, c["B:00500"]);
    }

    [Fact]
    public void El_200_lleva_identificacion_balance_liquidacion_e_ingreso()
    {
        var c = new DeclaracionCalculada { Modelo = "200" };
        CalculadorSociedades.Calcular200(new Entrada200(100_000m, [new("Multas", 2_000m, "MULTAS")], 0, 0, 0, 0, 0, 1_000m, (5_000m, 0, 0),
            new("MICRO"), new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), c);
        var extra = new Dictionary<string, object?> { ["D:incn"] = 800_000m, ["F:00006"] = 1, ["F:00088"] = 1, ["D:tipoEjercicio"] = 1,
            ["B:00180"] = 22_000m, ["B:00500"] = 100_000m, ["B:00270"] = -16_000m };
        var d = new DeclaracionParaFichero(9, "200", 2025, "0A", "ORDINARIA", "B12345678", "Ñandú Café, S.L.", c.Casillas, c.Resultado, c.ADevolver,
            null, new Dictionary<string, IReadOnlyList<RegistroAeat>>(), Inicio: new DateOnly(2025, 1, 1), Fin: new DateOnly(2025, 12, 31), Extra: extra);
        var (b, nombre) = FicheroModelo.Generar(d, new FicheroAeatCmd { Telefono = "910000000", Cnae = "6201" }, "B00000000");
        var t = Texto(b);
        Assert.Equal("B12345678_200_2025_0A.txt", nombre);
        Assert.Equal(FicheroModelo.Diseno("200", 2025).Longitud, t.Length);
        Assert.StartsWith("<T200020250A0000><AUX>", t);
        Assert.EndsWith("</T200020250A0000>", t);
        int P(string pagina) => t.IndexOf($"<T200{pagina}>", StringComparison.Ordinal);
        var p1 = P("01000");
        Assert.Equal("IB12345678", t.Substring(p1 + 12, 10));
        Assert.Equal("20250A" + "20250101" + "20251231" + "1" + "6201", t.Substring(p1 + 102, 27));
        Assert.Equal("1", t.Substring(p1 + 192, 1));             // [00006] reducida dimensión
        Assert.Equal("1", t.Substring(p1 + 226, 1));             // [00088] tipo reducido con INCN inferior a 1 M€
        Assert.Equal("100", t.Substring(p1 + 240, 3));           // INCN inferior a 20 M€
        Assert.Equal("00000000002200000", t.Substring(P("04000") + 12 + 31 * 17, 17));   // [00180] total activo
        Assert.Equal("N0000000001600000", t.Substring(P("07000") + 471, 17));             // [00270] gastos de personal, en negativo
        var p12 = P("12000");
        Assert.Equal("00000000010000000", t.Substring(p12 + 12, 17));                     // [00500]
        Assert.Equal("00000000010000000", t.Substring(p12 + 80, 17));                     // [00501]
        Assert.Equal("00000000000200000", t.Substring(p12 + 1083, 17));                   // [01815] multas
        var p14 = P("14000");
        Assert.Equal("00000000010200000", t.Substring(p14 + 12, 17));                     // [00550]
        Assert.Equal("00000000010200000", t.Substring(p14 + 165, 17));                    // [00552]
        Assert.Equal("2122", t.Substring(p14 + 437, 4));                                  // [00558] 21 % y 22 % (2025)
        Assert.Equal("00000000002194000", t.Substring(p14 + 645, 17));                    // [00562] 50.000 × 21 % + 52.000 × 22 %
        var p14b = P("14B00");
        Assert.Equal("00000000002194000", t.Substring(p14b + 250, 17));                   // [00592] cuota líquida
        Assert.Equal("00000000000100000", t.Substring(p14b + 540, 17));                   // [01766] retenciones
        Assert.Equal("00000000000500000", t.Substring(p14b + 608, 17));                   // [00601] primer pago
        Assert.Equal("00000000001594000", t.Substring(p14b + 710, 17));                   // [00611] cuota diferencial
        Assert.Equal("00000000001594000", t.Substring(p14b + 1050, 17));                  // [01586]
        var p15 = P("15000");                                                             // página 15: detalle de bases negativas
        Assert.True(p15 > 0);
        var did = P("DID00");
        Assert.Equal("0" + "2025" + "1" + "0A" + "010125" + "311225" + "B12345678", t.Substring(did + 12, 29));
        Assert.Equal("I" + "00000000001594000", t.Substring(did + 424, 18));
        Assert.Equal("0", t.Substring(did + 561, 1));
    }
}
