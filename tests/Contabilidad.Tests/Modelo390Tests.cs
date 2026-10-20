using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;

namespace Contabilidad.Tests;

public class Modelo390Tests
{
    private static GrupoIva G(string libro, string tipo, decimal pct, decimal b, decimal c, bool rect = false, bool bi = false,
        decimal pctRec = 0, decimal cRec = 0, decimal? ded = null, bool caja = false)
        => new(libro, rect, tipo, pct, pctRec, bi, b, c, cRec, ded ?? c, caja);

    private static DeclaracionCalculada Calcular(Entrada390 e)
    {
        var d = new DeclaracionCalculada { Modelo = "390", Anio = 2025, Periodo = "0A" };
        Calculador390.Calcular(e, d);
        return d;
    }

    [Fact]
    public void Separa_devengado_y_deducible_por_tipo_y_clase_de_operacion()
    {
        var grupos = new List<GrupoIva>
        {
            G("EMITIDAS", "INTERIOR", 21, 10000, 2100), G("EMITIDAS", "INTERIOR", 10, 1000, 100),
            G("EMITIDAS", "INTERIOR", 21, -500, -105, rect: true),
            G("EMITIDAS", "INTERIOR", 21, 2000, 420, pctRec: 5.2m, cRec: 104),
            G("EMITIDAS", "INTERIOR", 21, 300, 63, caja: true),
            G("EMITIDAS", "INTRACOM_ENTREGA", 0, 4000, 0), G("EMITIDAS", "EXPORTACION", 0, 2500, 0), G("EMITIDAS", "EXENTA", 0, 700, 0),
            G("RECIBIDAS", "INTERIOR", 21, 5000, 1050), G("RECIBIDAS", "INTERIOR", 21, 3000, 630, bi: true),
            G("RECIBIDAS", "IMPORTACION", 21, 2000, 420),
            G("RECIBIDAS", "INTRACOM_ADQUISICION", 21, 1000, 210), G("RECIBIDAS", "INTRACOM_SERVICIOS", 21, 400, 84),
            G("RECIBIDAS", "INVERSION_SUJETO_PASIVO", 21, 600, 126),
            G("RECIBIDAS", "INTERIOR", 21, -100, -21, rect: true),
            G("RECIBIDAS", "INTERIOR", 21, 200, 42, ded: 0),
        };
        var autoliq = new List<Resumen303>
        {
            new("1T", 500, 500, 0, 0, 120, 0, 0), new("2T", 400, 400, 0, 0, 0, 0, 0), new("3T", -50, 0, 0, 50, 0, 0, 50),
            new("4T", -30, 0, 0, 30, 50, 0, 80),
        };
        var d = Calcular(new Entrada390(grupos, 15m, -8m, autoliq, null, [new Prorrata390("GENERAL", 10000, 9000, 90)]));

        // Devengado: régimen ordinario por tipo, criterio de caja aparte, adquisiciones intracomunitarias e ISP
        Assert.Equal((12000m, 2520m), (d["05"], d["06"]));
        Assert.Equal((1000m, 100m), (d["03"], d["04"]));
        Assert.Equal((300m, 63m), (d["647"], d["648"]));
        Assert.Equal((1000m, 210m), (d["25"], d["26"]));
        Assert.Equal((400m, 84m), (d["551"], d["552"]));
        Assert.Equal((600m, 126m), (d["27"], d["28"]));
        Assert.Equal((-500m, -105m), (d["29"], d["30"]));
        Assert.Equal(12000m + 1000 + 300 + 1000 + 400 + 600 - 500, d["33"]);
        Assert.Equal(2520m + 100 + 63 + 210 + 84 + 126 - 105, d["34"]);
        Assert.Equal((2000m, 104m), (d["601"], d["602"]));
        Assert.Equal(d["34"] + 104m, d["47"]);

        // Deducible por bloque y tipo, con sus totales
        Assert.Equal((5000m + 600 + 200, 1050m + 126), (d["605"], d["606"]));
        Assert.Equal(d["606"], d["49"]);
        Assert.Equal((3000m, 630m), (d["613"], d["51"]));
        Assert.Equal((2000m, 420m), (d["621"], d["53"]));
        Assert.Equal((1000m, 210m), (d["629"], d["57"]));
        Assert.Equal((400m, 84m), (d["637"], d["598"]));
        Assert.Equal((-100m, -21m), (d["639"], d["62"]));
        Assert.Equal(15m, d["63"]);
        Assert.Equal(-8m, d["522"]);
        Assert.Equal(1176m + 630 + 420 + 210 + 84 - 21 + 15 - 8, d["64"]);
        Assert.Equal(d["47"] - d["64"], d["65"]);

        // Resultados de las autoliquidaciones, compensaciones y volumen de operaciones
        Assert.Equal(900m, d["95"]);
        Assert.Equal(30m, d["97"]);
        Assert.Equal(80m, d["662"]);
        Assert.Equal(0m, d["85"]);
        Assert.Equal(10000m + 1000 - 500 + 2000, d["99"]);
        Assert.Equal(300m, d["653"]);
        Assert.Equal((4000m, 2500m, 700m), (d["103"], d["104"], d["105"]));
        Assert.Equal(12500m + 300 + 4000 + 2500 + 700, d["108"]);
        Assert.Equal(200m, d["232"]);
        Assert.Equal((10000m, 9000m, 0m, 90m), (d["115"], d["116"], d["117"], d["118"]));
        Assert.Equal(0m, d.Resultado);
    }

    [Fact]
    public void La_compensacion_del_ejercicio_anterior_no_supera_lo_pendiente_al_empezar()
    {
        var autoliq = new List<Resumen303> { new("1T", 0, 0, 0, 0, 100, 100, 0), new("2T", 0, 0, 0, 0, 0, 40, 0) };
        var d = Calcular(new Entrada390([G("EMITIDAS", "INTERIOR", 21, 1000, 210)], 0, 0, autoliq, null, []));
        Assert.Equal(100m, d["85"]);
        Assert.Equal(d["84"] - 100m, d["86"]);
    }

    [Fact]
    public void Un_tipo_sin_casilla_detiene_el_calculo()
    {
        Assert.Throws<ReglaContableException>(() => Calcular(new Entrada390([G("EMITIDAS", "INTERIOR", 7, 100, 7)], 0, 0, [], null, [])));
    }
}
