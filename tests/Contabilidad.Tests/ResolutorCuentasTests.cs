using Contabilidad.Domain;
using Contabilidad.Domain.Facturacion;

namespace Contabilidad.Tests;

public class ResolutorCuentasTests
{
    [Fact]
    public void La_regla_especifica_por_tipo_gana_a_la_generica()
    {
        var r = new ResolutorCuentas(
            [new("IVA_SOPORTADO", null, null, 100), new("IVA_SOPORTADO", 5, null, 200)], null, null);
        Assert.Equal(200, r.Resolver(null, "IVA_SOPORTADO", 5));
        Assert.Equal(100, r.Resolver(null, "IVA_SOPORTADO", 6));
    }

    [Fact]
    public void La_cuenta_fija_manda_sobre_el_rol()
        => Assert.Equal(7, new ResolutorCuentas([], null, null).Resolver(7, "LO_QUE_SEA"));

    [Fact]
    public void Faltas_de_configuracion_dan_error_de_regla()
    {
        var r = new ResolutorCuentas([], null, null);
        Assert.Throws<ReglaContableException>(() => r.Resolver(null, "COMPRAS"));
        Assert.Throws<ReglaContableException>(() => r.Resolver(null, "CLIENTE"));
        Assert.Throws<ReglaContableException>(() => r.Resolver(null, "PROVEEDOR"));
        Assert.Throws<ReglaContableException>(() => r.Resolver(null, null));
    }
}
