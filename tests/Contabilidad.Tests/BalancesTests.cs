using Contabilidad.Domain.Balances;

namespace Contabilidad.Tests;

public class GeneradorEstadosTests
{
    private static SaldoBalance S(string codigo, decimal saldo) => new(codigo, "Cuenta " + codigo, saldo);

    // Contabilidad pequeña que cuadra: capital 3000, préstamo 2000, ventas 5000, compras 1000, sueldos 1500,
    // amortización 300 (acumulada 300), IVA, clientes, proveedores, banco.
    private static readonly SaldoBalance[] Libro =
    [
        S("10000000", -3000m), S("17000000", -2000m),
        S("21300000", 4000m), S("28130000", -300m),
        S("43000001", 1210m), S("40000001", -605m), S("47200000", 105m), S("47700000", -210m),
        S("57200000", 3000m),
        S("60000000", 1000m), S("64000000", 1500m), S("68100000", 300m), S("70000000", -5000m),
    ];

    [Fact]
    public void El_balance_cuadra_e_incluye_el_resultado_antes_de_regularizar()
    {
        var b = GeneradorEstados.Calcular(ModelosEstados.Balance, Libro);
        Assert.Empty(b.SinClasificar);
        Assert.Equal(b["TA"], b["TP"]);
        Assert.Equal(2200m, b["PA1VII"]);                           // 5000 − 1000 − 1500 − 300
        Assert.Equal(3700m, b["AII"]);                              // la amortización acumulada resta
        Assert.Equal(1210m, b["BII1"]);
        Assert.Equal(105m, b["BII3"]);
        Assert.Equal(605m, b["PCIV1"]);
        Assert.Equal(210m, b["PCIV2"]);
        Assert.Equal(2000m, b["PBII1"]);
        Assert.Equal(8015m, b["TA"]);
    }

    [Fact]
    public void Tras_regularizar_el_resultado_sale_de_la_129()
    {
        var regularizado = Libro.Where(s => s.Codigo[0] is not ('6' or '7')).Append(S("12900000", -2200m));
        var b = GeneradorEstados.Calcular(ModelosEstados.Balance, regularizado);
        Assert.Equal(2200m, b["PA1VII"]);
        Assert.Equal(b["TA"], b["TP"]);
    }

    [Fact]
    public void Las_cuentas_corrientes_van_al_activo_o_al_pasivo_segun_su_saldo()
    {
        var deudora = GeneradorEstados.Calcular(ModelosEstados.Balance, [S("55100000", 500m), S("10000000", -500m)]);
        Assert.Equal(500m, deudora["BIV"]);
        Assert.Equal(0m, deudora["PCII3"]);

        var acreedora = GeneradorEstados.Calcular(ModelosEstados.Balance, [S("55100000", -500m), S("57000000", 500m)]);
        Assert.Equal(0m, acreedora["BIV"]);
        Assert.Equal(500m, acreedora["PCII3"]);
    }

    [Fact]
    public void Gana_el_prefijo_mas_largo()
    {
        var b = GeneradorEstados.Calcular(ModelosEstados.Balance, [S("29350000", -10m), S("29330000", -20m)]);
        Assert.Equal(-10m, b["AV"]);                                // 2935: otras partes vinculadas
        Assert.Equal(-20m, b["AIV"]);                               // 2933: grupo (por la regla 293)
    }

    [Fact]
    public void Las_cuentas_sin_partida_se_informan()
    {
        var b = GeneradorEstados.Calcular(ModelosEstados.Balance, [S("58000000", 100m), S("10000000", -100m)]);
        Assert.Equal("58000000", Assert.Single(b.SinClasificar).Codigo);
    }

    [Fact]
    public void La_cuenta_de_perdidas_y_ganancias_agrupa_ingresos_y_gastos()
    {
        var p = GeneradorEstados.Calcular(ModelosEstados.PerdidasGanancias,
            Libro.Concat([S("66230000", 80m), S("76900000", -20m), S("63000000", 300m), S("70800000", 200m)]));
        Assert.Empty(p.SinClasificar);                              // las cuentas de balance quedan fuera del alcance
        Assert.Equal(4800m, p["1"]);                                // ventas − devoluciones
        Assert.Equal(-1000m, p["4"]);
        Assert.Equal(-1500m, p["6"]);
        Assert.Equal(-300m, p["8"]);
        Assert.Equal(2000m, p["RA"]);
        Assert.Equal(-60m, p["RB"]);
        Assert.Equal(1940m, p["RC"]);
        Assert.Equal(1640m, p["RD"]);
    }

    [Fact]
    public void El_resultado_de_la_pyg_coincide_con_el_del_balance()
    {
        var p = GeneradorEstados.Calcular(ModelosEstados.PerdidasGanancias, Libro);
        var b = GeneradorEstados.Calcular(ModelosEstados.Balance, Libro);
        Assert.Equal(b["PA1VII"], p["RD"]);
    }

    [Fact]
    public void El_detalle_de_cada_partida_lleva_sus_cuentas()
    {
        var b = GeneradorEstados.Calcular(ModelosEstados.Balance, Libro);
        var aii = b.Partidas.First(x => x.Id == "AII");
        Assert.Equal(["21300000", "28130000"], aii.Cuentas.Select(c => c.Codigo));
        Assert.Equal([4000m, -300m], aii.Cuentas.Select(c => c.Importe));
    }

    // Todas las subcuentas del PGC (Normal y PYMES) deben tener partida, con saldo deudor y con saldo acreedor
    [Fact]
    public void Todas_las_subcuentas_del_pgc_tienen_partida()
    {
        var sinPartida = new List<string>();
        foreach (var codigo in SubcuentasPgc())
        {
            var excluidas = codigo.StartsWith("58") || codigo.StartsWith("599");   // activos en venta: solo modelo normal
            if (excluidas) continue;
            var modelo = codigo[0] is '6' or '7' ? ModelosEstados.PerdidasGanancias : ModelosEstados.Balance;
            foreach (var saldo in new[] { 1m, -1m })
                if (GeneradorEstados.Calcular(modelo, [S(codigo, saldo)]).SinClasificar.Count > 0)
                    sinPartida.Add($"{codigo} ({(saldo > 0 ? "deudor" : "acreedor")})");
        }
        Assert.True(sinPartida.Count == 0, "Sin partida: " + string.Join(", ", sinPartida));
    }

    private static IEnumerable<string> SubcuentasPgc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "database", "07_pgc.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var lineas = File.ReadAllLines(Path.Combine(dir!.FullName, "database", "07_pgc.sql"))
            .Select(l => l.Split('|')).Where(p => p.Length == 3 && p[0].All(char.IsDigit) && p[0].Length is >= 3 and <= 4)
            .Select(p => p[0]).ToHashSet();
        // Subcuentas de 4 dígitos y cuentas de 3 sin subcuentas (que reciben la "xxx0")
        return lineas.Where(c => c.Length == 4 || !lineas.Any(x => x.Length == 4 && x.StartsWith(c)))
                     .Select(c => c.Length == 3 ? c + "0" : c).Distinct();
    }
}

public class SumasSaldosTests
{
    private static readonly MovimientoCuenta[] Movs =
    [
        new("43000001", 1210m, 0), new("43000002", 500m, 200m), new("47700000", 0, 210m), new("70000000", 0, 1000m),
        new("57200000", 200m, 500m)
    ];

    [Fact]
    public void Por_cuenta_completa_calcula_sumas_y_saldos()
    {
        var f = SumasSaldos.Agrupar(Movs, SumasSaldos.Detalle, c => c);
        Assert.Equal(5, f.Count);
        var c2 = f.Single(x => x.Codigo == "43000002");
        Assert.Equal((500m, 200m, 300m, 0m), (c2.Debe, c2.Haber, c2.SaldoDeudor, c2.SaldoAcreedor));
        var banco = f.Single(x => x.Codigo == "57200000");
        Assert.Equal((0m, 300m), (banco.SaldoDeudor, banco.SaldoAcreedor));
    }

    [Theory]
    [InlineData(1, new[] { "4", "5", "7" })]
    [InlineData(2, new[] { "43", "47", "57", "70" })]
    [InlineData(4, new[] { "4300", "4770", "5720", "7000" })]
    public void Agrupa_por_niveles(int nivel, string[] codigos)
        => Assert.Equal(codigos, SumasSaldos.Agrupar(Movs, nivel, c => c).Select(x => x.Codigo));

    [Fact]
    public void Las_sumas_siempre_cuadran_al_agrupar()
    {
        foreach (var nivel in new[] { 1, 2, 3, 4, 5 })
        {
            var f = SumasSaldos.Agrupar(Movs, nivel, c => c);
            Assert.Equal(Movs.Sum(m => m.Debe), f.Sum(x => x.Debe));
            Assert.Equal(f.Sum(x => x.SaldoDeudor) - f.Sum(x => x.SaldoAcreedor), f.Sum(x => x.Debe) - f.Sum(x => x.Haber));
        }
    }

    [Fact]
    public void Un_grupo_suma_todas_sus_cuentas()
    {
        var g4 = SumasSaldos.Agrupar(Movs, 1, c => "Grupo " + c).Single(x => x.Codigo == "4");
        Assert.Equal(("Grupo 4", 1710m, 410m), (g4.Nombre, g4.Debe, g4.Haber));
    }
}
