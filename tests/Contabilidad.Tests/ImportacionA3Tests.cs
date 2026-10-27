using System.Buffers.Binary;
using System.Text;
using Contabilidad.Domain;
using Contabilidad.Domain.Importacion.A3Eco;

namespace Contabilidad.Tests;

public class ImportacionA3Tests
{
    // ---------------------------------------------------------------- ficheros ISAM sintéticos con el formato de a3ECO

    /// <summary>Fichero ISAM de Micro Focus: cabecera de 128 bytes y registros con cabecera de 2 bytes (tipo y longitud), alineados a 4.</summary>
    private static byte[] Isam(params (int Tipo, byte[] Datos)[] registros)
    {
        var ms = new MemoryStream();
        var cab = new byte[128]; cab[0] = 0x30; cab[1] = 0x7E;
        ms.Write(cab);
        foreach (var (tipo, d) in registros)
        {
            ms.WriteByte((byte)((tipo << 4) | (d.Length >> 8))); ms.WriteByte((byte)d.Length);
            ms.Write(d);
            while (ms.Length % 4 != 0) ms.WriteByte(0);
        }
        return ms.ToArray();
    }
    private static byte[] Isam(params byte[][] registros) => Isam(registros.Select(r => (4, r)).ToArray());

    private static void Texto(byte[] r, int o, int n, string s) => Encoding.Latin1.GetBytes(s.PadRight(n)[..n]).CopyTo(r, o);
    private static void Le6(byte[] r, int o, long v) { for (var i = 0; i < 6; i++) r[o + i] = (byte)(v >> (8 * i)); }

    private static byte[] Cuenta(int cuenta4, uint sub, string nombre, long apDebe = 0, long apHaber = 0,
        long[]? debe = null, long[]? haber = null, int tercero = 0)
    {
        var r = new byte[256];
        BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)cuenta4);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(2), sub);
        Texto(r, 6, 30, nombre);
        Le6(r, 36, apDebe); Le6(r, 42, apHaber);
        for (var i = 0; i < 12; i++) { Le6(r, 54 + 6 * i, debe?[i] ?? 0); Le6(r, 126 + 6 * i, haber?[i] ?? 0); }
        r[204] = (byte)(cuenta4 > 0 ? '5' : '0' + sub.ToString().Length);
        r[208] = (byte)(tercero >> 16); r[209] = (byte)(tercero >> 8); r[210] = (byte)tercero;
        return r;
    }

    private static byte[] Apunte(int fecha, int asiento, long cuenta12, char dh, long centimos, string concepto, string doc = "")
    {
        var r = new byte[128];
        BinaryPrimitives.WriteInt32BigEndian(r, fecha);
        Texto(r, 13, 30, concepto); Texto(r, 43, 10, doc);
        r[53] = (byte)(cuenta12 >> 32); BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(54), (uint)cuenta12);
        r[58] = (byte)dh;
        Texto(r, 59, 14, centimos.ToString("D14"));
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(108), asiento);
        return r;
    }

    private static byte[] Tercero(int codigo, string nif, string nombre, uint cp = 0, string via = "", string poblacion = "")
    {
        var r = new byte[256];
        Array.Fill(r, (byte)' ');
        BinaryPrimitives.WriteInt32BigEndian(r, codigo);
        Texto(r, 4, 14, nif); Texto(r, 18, 40, nombre); Texto(r, 60, 5, "C/"); Texto(r, 65, 30, via); Texto(r, 95, 5, "12");
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(106), cp);
        Texto(r, 112, 20, poblacion); Texto(r, 147, 15, "");
        return r;
    }

    private static byte[] Directorio(int codigo, string nombre, string nif, string carpeta)
    {
        var r = new byte[513];
        Array.Fill(r, (byte)' ');
        r[0] = 0; r[1] = 0; r[2] = (byte)codigo;
        Texto(r, 3, 40, nombre); Texto(r, 47, 14, nif); Texto(r, 101, 40, $@"\A3\A3ECO\{carpeta}\");
        return r;
    }

    private static long[] Mes(int mes, long importe) { var m = new long[12]; m[mes - 1] = importe; return m; }

    /// <summary>Empresa con un año (2025): una venta en marzo (cuadrada) y un asiento descuadrado en abril.</summary>
    private static Dictionary<string, byte[]> Ficheros(bool sumasCorrectas = true)
    {
        var cu = Isam(
            Cuenta(0, 4, "ACREEDORES Y DEUDORES"),
            Cuenta(4300, 10000, "CLIENTE UNO", apDebe: 50000, debe: Mes(3, 121000), tercero: 7),
            Cuenta(4770, 0, "IVA REPERCUTIDO", haber: Mes(3, 21000)),
            Cuenta(7000, 0, "VENTAS", haber: Mes(3, sumasCorrectas ? 100000 : 99999)),
            Cuenta(5700, 0, "CAJA", apHaber: 50000, debe: Mes(4, 1000)),
            Cuenta(6290, 0, "OTROS SERVICIOS", haber: Mes(4, 500)));
        var marzo = Isam(
            Apunte(20250310, 1, 430000010000, 'D', 121000, "Nuestra Fra. 25/1", "25/1"),
            Apunte(20250310, 1, 477000000000, 'H', 21000, "IVA R./CLIENTE UNO", "25/1"),
            Apunte(20250310, 1, 700000000000, 'H', 100000, "Ventas a CLIENTE UNO", "25/1"));
        var abril = Isam((4, Apunte(20250402, 2, 570000000000, 'D', 1000, "Descuadrado")),
                         (2, Apunte(20250402, 2, 570000000000, 'D', 9999, "Borrado")),      // registro borrado: no cuenta
                         (4, Apunte(20250402, 2, 629000000000, 'H', 500, "Descuadrado")));
        return new()
        {
            ["A3ECO/TCLIPRO.DAT"] = Isam(Tercero(7, "B12345674", "CLIENTE UNO SL", 8001, "MAYOR", "BARCELONA")),
            ["A3ECO/TECODIR.DAT"] = Isam(Directorio(1, "EMPRESA DEMO", "B99999999", "E00001")),
            ["A3ECO/E00001/000015CU.DAT"] = cu,
            ["A3ECO/E00001/0000153A.DAT"] = marzo,
            ["A3ECO/E00001/0000154A.DAT"] = abril,
            ["A3ECO/E00001/0000153R.DAT"] = Isam(new byte[128]),                        // libro de IVA: no son apuntes
        };
    }

    private static readonly DateOnly Hoy = new(2026, 10, 27);

    // ---------------------------------------------------------------- lector

    [Fact]
    public void Isam_LeeLosRegistrosVivosYSaltaLosBorrados()
    {
        var f = Isam((4, [1, 2, 3]), (2, [9, 9]), (4, [4, 5, 6, 7, 8]));
        var r = FicheroIsam.Registros(f, "x.dat");
        Assert.Equal(2, r.Count);
        Assert.Equal(new byte[] { 4, 5, 6, 7, 8 }, r[1]);
    }

    [Fact]
    public void Isam_RechazaUnFicheroQueNoLoEs()
        => Assert.Throws<ReglaContableException>(() => FicheroIsam.Registros(new byte[200], "x.dat"));

    [Fact]
    public void Lector_LeeCuentasAsientosYTerceros()
    {
        var d = LectorA3Eco.Leer(Ficheros(), null, Hoy);

        Assert.Equal("E00001", d.Carpeta);
        Assert.Equal("EMPRESA DEMO", d.Empresa!.Nombre);
        Assert.Equal(8, d.DigitosCuenta);
        var anio = Assert.Single(d.Anios);
        Assert.Equal(2025, anio.Anio);
        Assert.Equal(new DateOnly(2025, 1, 1), anio.Inicio);

        Assert.Equal(5, anio.Cuentas.Count(c => c.EsSubcuenta));
        var cliente = anio.Cuentas.Single(c => c.Codigo == "430000010000");
        Assert.Equal(("CLIENTE UNO", 50000L, 7), (cliente.Nombre, cliente.AperturaDebe, cliente.Tercero!.Value));

        Assert.Equal(2, anio.Asientos.Count);
        var venta = anio.Asientos[0];
        Assert.True(venta.Cuadra);
        Assert.Equal(["430000010000", "477000000000", "700000000000"], venta.Apuntes.Select(a => a.Cuenta));
        Assert.Equal("25/1", venta.Apuntes[0].Documento);
        Assert.False(anio.Asientos[1].Cuadra);                        // 10,00 al debe y 5,00 al haber
        Assert.Equal(2, anio.Asientos[1].Apuntes.Count);              // el registro borrado no está

        var t = Assert.Single(d.Terceros);
        Assert.Equal(("B12345674", "CLIENTE UNO SL", "08001", "BARCELONA", "C/ MAYOR 12"), (t.Nif, t.Nombre, t.CodigoPostal, t.Poblacion, t.Direccion));
    }

    [Fact]
    public void Lector_ComparaLasSumasConLasQueGuardaA3()
    {
        Assert.Empty(LectorA3Eco.Leer(Ficheros(), null, Hoy).Anios[0].Descuadres());
        Assert.Equal(["700000000000"], LectorA3Eco.Leer(Ficheros(sumasCorrectas: false), null, Hoy).Anios[0].Descuadres());
    }

    [Fact]
    public void Lector_ConVariasEmpresasPideCual()
    {
        var f = Ficheros();
        f["A3ECO/E00002/000025CU.DAT"] = Isam(Cuenta(5700, 0, "CAJA"));
        Assert.Equal(["E00001", "E00002"], LectorA3Eco.Carpetas(f));
        var ex = Assert.Throws<ReglaContableException>(() => LectorA3Eco.Leer(f, null, Hoy));
        Assert.Contains("E00002", ex.Detalle);
        Assert.Equal("E00002", LectorA3Eco.Leer(f, "e00002", Hoy).Carpeta);
    }

    [Fact]
    public void Lector_SinAsientosDeduceElAnioPorSuUltimoDigito()
    {
        var f = new Dictionary<string, byte[]> { ["E00001/000013CU.DAT"] = Isam(Cuenta(5700, 0, "CAJA", apDebe: 100)) };
        var d = LectorA3Eco.Leer(f, null, Hoy);
        Assert.Equal(2023, d.Anios[0].Anio);
        Assert.Contains(d.Avisos, a => a.Contains("TCLIPRO"));
    }

    // ---------------------------------------------------------------- códigos de cuenta

    [Theory]
    [InlineData("430000070000", 8, 8, "43000007")]
    [InlineData("430000070000", 8, 10, "4300000007")]
    [InlineData("430000000000", 8, 6, "430000")]
    [InlineData("430000070000", 8, 6, "430007")]
    [InlineData("430012340000", 8, 6, null)]            // el subnúmero 1234 no cabe en 2 dígitos
    [InlineData("410000000001", 8, 8, null)]            // dígitos más allá de los de la empresa en a3
    public void Convertir_AjustaElSubnumeroALosDigitosDeDestino(string codigo, int digitosA3, int destino, string? esperado)
        => Assert.Equal(esperado, LectorA3Eco.Convertir(codigo, digitosA3, destino));

    [Fact]
    public void DigitosA3_NoSeDejaLlevarPorUnaSubcuentaSuelta()
    {
        var codigos = Enumerable.Range(1, 100).Select(i => $"4300{i:D4}0000").Append("410000000001");
        Assert.Equal(8, LectorA3Eco.DigitosA3(codigos));
    }
}
