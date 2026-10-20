using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;

namespace Contabilidad.Tests;

public class HasheadorPasswordTests
{
    [Fact]
    public void Hash_y_verificacion_son_coherentes()
    {
        var h = HasheadorPassword.Hash("una-clave-larga-123");
        Assert.True(HasheadorPassword.Verificar("una-clave-larga-123", h));
        Assert.False(HasheadorPassword.Verificar("otra-clave-larga-123", h));
    }

    [Fact]
    public void Dos_hashes_de_la_misma_clave_difieren_por_la_sal()
        => Assert.NotEqual(HasheadorPassword.Hash("una-clave-larga-123"), HasheadorPassword.Hash("una-clave-larga-123"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura")]
    [InlineData("a$b$c")]
    public void Un_hash_almacenado_invalido_no_verifica_ni_lanza(string? almacenado)
        => Assert.False(HasheadorPassword.Verificar("lo-que-sea-123456", almacenado));

    [Fact]
    public void Politica_rechaza_cortas_largas_iguales_al_email_y_repetitivas()
    {
        Assert.Throws<ReglaContableException>(() => HasheadorPassword.ValidarPolitica("corta", "a@b.es"));
        Assert.Throws<ReglaContableException>(() => HasheadorPassword.ValidarPolitica(new string('x', 129) + "yz", "a@b.es"));
        Assert.Throws<ReglaContableException>(() => HasheadorPassword.ValidarPolitica("usuario@empresa.es", "USUARIO@empresa.es"));
        Assert.Throws<ReglaContableException>(() => HasheadorPassword.ValidarPolitica("aaaaaaaaaaaaaaaa", "a@b.es"));
        HasheadorPassword.ValidarPolitica("Clave-correcta-2026", "a@b.es");
    }
}

public class AccesoEmpresaTests
{
    [Theory]
    [InlineData("ADMIN", Nivel.Escritura)]
    [InlineData("CONTABLE", Nivel.Escritura)]
    [InlineData("LECTURA", Nivel.Lectura)]
    [InlineData("DESCONOCIDO", Nivel.Ninguno)]
    public void El_rol_da_el_nivel_por_defecto(string rol, Nivel esperado)
        => Assert.Equal(esperado, new AccesoEmpresa(rol, false, false).NivelEn(Modulo.Contabilidad));

    [Fact]
    public void El_permiso_explicito_manda_sobre_el_rol_en_los_dos_sentidos()
    {
        var a = new AccesoEmpresa("CONTABLE", false, false, new Dictionary<Modulo, Nivel>
        { [Modulo.Tesoreria] = Nivel.Ninguno, [Modulo.Fiscal] = Nivel.Lectura });
        Assert.Equal(Nivel.Ninguno, a.NivelEn(Modulo.Tesoreria));
        Assert.Equal(Nivel.Lectura, a.NivelEn(Modulo.Fiscal));
        Assert.Equal(Nivel.Escritura, a.NivelEn(Modulo.Contabilidad));

        var l = new AccesoEmpresa("LECTURA", false, false, new Dictionary<Modulo, Nivel> { [Modulo.Facturacion] = Nivel.Escritura });
        Assert.True(l.Puede(Modulo.Facturacion, Nivel.Escritura));
        Assert.False(l.Puede(Modulo.Contabilidad, Nivel.Escritura));
    }

    [Fact]
    public void Solo_gestor_superadmin_o_salta_revision_contabilizan_directo()
    {
        Assert.False(new AccesoEmpresa("CONTABLE", false, false).PuedeContabilizarDirecto);
        Assert.True(new AccesoEmpresa("CONTABLE", true, false).PuedeContabilizarDirecto);
        Assert.True(new AccesoEmpresa("CONTABLE", false, true).PuedeContabilizarDirecto);
        Assert.True(AccesoEmpresa.Total().PuedeContabilizarDirecto);
    }

    [Fact]
    public void Revisar_exige_escritura_en_el_modulo_y_contabilizar_directo()
    {
        Assert.False(new AccesoEmpresa("CONTABLE", false, false).PuedeRevisar(Modulo.Contabilidad));
        Assert.True(new AccesoEmpresa("CONTABLE", false, true).PuedeRevisar(Modulo.Contabilidad));
        Assert.False(new AccesoEmpresa("LECTURA", false, true).PuedeRevisar(Modulo.Contabilidad));
        var sinFact = new AccesoEmpresa("CONTABLE", false, true, new Dictionary<Modulo, Nivel> { [Modulo.Facturacion] = Nivel.Lectura });
        Assert.False(sinFact.PuedeRevisar(Modulo.Facturacion));
    }

    [Fact]
    public void El_superadmin_tiene_escritura_en_todo_y_el_resumen_lista_todos_los_modulos()
    {
        var t = AccesoEmpresa.Total();
        Assert.All(Enum.GetValues<Modulo>(), m => Assert.Equal(Nivel.Escritura, t.NivelEn(m)));
        var r = t.Resumen();
        Assert.Equal(Enum.GetValues<Modulo>().Length, r.Count);
        Assert.Equal("ESCRITURA", r["CONTABILIDAD"]);
    }
}
