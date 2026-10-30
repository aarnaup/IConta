using Contabilidad.Domain.Balances;

namespace Contabilidad.Tests;

public class PlanSaldosTests
{
    private static readonly CuentaPlan[] Plan =
    [
        new("4", "Acreedores y deudores", false), new("43", "Clientes", false), new("430", "Clientes", false),
        new("43000001", "Cliente uno", true), new("43000002", "Cliente dos", true),
        new("5", "Cuentas financieras", false), new("57", "Tesorería", false), new("572", "Bancos", false), new("57200000", "Banco", true),
        new("6", "Compras y gastos", false),
    ];

    [Fact]
    public void Construir_SumaLasSubcuentasEnCadaNivelYCalculaElSaldoFinal()
    {
        var anterior = new[] { new MovimientoCuenta("43000001", 500, 0), new MovimientoCuenta("57200000", 0, 500) };
        var periodo = new[] { new MovimientoCuenta("43000001", 0, 200), new MovimientoCuenta("57200000", 200, 0), new MovimientoCuenta("43000002", 50, 0) };

        var f = PlanSaldos.Construir(Plan, anterior, periodo, soloConSaldo: true).ToDictionary(x => x.Codigo);

        Assert.DoesNotContain("6", f.Keys);                                     // sin saldo ni movimientos
        Assert.Equal((500m, 50m, 200m, 350m), (f["430"].SaldoAnterior, f["430"].Debe, f["430"].Haber, f["430"].SaldoFinal));
        Assert.Equal((500m, 0m, 200m, 300m), (f["43000001"].SaldoAnterior, f["43000001"].Debe, f["43000001"].Haber, f["43000001"].SaldoFinal));
        Assert.Equal(-300m, f["5"].SaldoFinal);
        Assert.Equal((1, 2, 3, 5), (f["4"].Nivel, f["43"].Nivel, f["430"].Nivel, f["43000001"].Nivel));
    }

    [Fact]
    public void Construir_OrdenaJerarquicamenteYPuedeMostrarTodas()
    {
        var filas = PlanSaldos.Construir(Plan, [], [new MovimientoCuenta("43000002", 10, 0)], soloConSaldo: false);
        Assert.Equal(["4", "43", "430", "43000001", "43000002", "5", "57", "572", "57200000", "6"], filas.Select(x => x.Codigo));
    }

    [Fact]
    public void Construir_IncluyeLasCuentasConMovimientosQueNoEstanEnElPlan()
    {
        var filas = PlanSaldos.Construir(Plan, [], [new MovimientoCuenta("47200000", 21, 0)], soloConSaldo: true);
        Assert.Contains(filas, x => x.Codigo == "47200000" && x.EsSubcuenta && x.Debe == 21);
        Assert.Contains(filas, x => x.Codigo == "4" && x.Debe == 21);
    }
}
