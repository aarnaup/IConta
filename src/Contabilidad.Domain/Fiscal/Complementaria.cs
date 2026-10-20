namespace Contabilidad.Domain.Fiscal;

/// <summary>Lo presentado antes para el mismo modelo y periodo.</summary>
/// <param name="Resultado">Resultado de la última presentación.</param>
/// <param name="Ingresado">Suma de lo ingresado con todas las presentaciones del periodo.</param>
public sealed record PresentacionAnterior(short Numero, decimal Resultado, decimal Ingresado);

/// <summary>
/// Declaraciones complementarias y sustitutivas. La complementaria solo puede aumentar el resultado (más a ingresar o menos a
/// devolver/compensar) y descuenta lo ya ingresado en el periodo; la sustitutiva sustituye a la anterior por completo.
/// </summary>
public static class Complementaria
{
    public static readonly string[] Tipos = ["ORDINARIA", "COMPLEMENTARIA", "SUSTITUTIVA"];

    // Casilla de «a deducir (resultado de la anterior)» y de «resultado» en cada autoliquidación
    private static readonly Dictionary<string, (string Deducir, string Resultado)> Casillas = new()
    {
        ["303"] = ("70", "71"), ["111"] = ("29", "30"), ["115"] = ("04", "05"), ["130"] = ("18", "19"), ["131"] = ("DED", "RES"), ["420"] = ("DED", "LIQ"),
    };

    public static bool EsAutoliquidacion(string modelo) => Casillas.ContainsKey(modelo);

    public static void Aplicar(string modelo, string tipo, PresentacionAnterior anterior, DeclaracionCalculada d)
    {
        if (tipo == "SUSTITUTIVA")
        {
            d.Avisos.Add($"Sustituye a la presentación nº {anterior.Numero}.");
            if (EsAutoliquidacion(modelo) && Math.Max(0, d.Resultado) < anterior.Ingresado)
                d.Avisos.Add($"Con la anterior se ingresaron {anterior.Ingresado:N2}: la diferencia se recupera solicitando la devolución de ingresos indebidos.");
            if (modelo is "303" or "420")
                d.Avisos.Add("Revisa si el modelo admite sustitutiva o si hay que presentar una autoliquidación rectificativa.");
            return;
        }
        if (!EsAutoliquidacion(modelo))
        {
            d.Avisos.Add($"Complementa la presentación nº {anterior.Numero} (declaración informativa: incluye todos los datos del periodo).");
            return;
        }
        if (d.Resultado < anterior.Resultado)
            throw new ReglaContableException($"El resultado ({d.Resultado:N2}) es menor que el de la presentación anterior ({anterior.Resultado:N2}).",
                "Una complementaria solo puede aumentar lo que se ingresa o reducir lo que se devuelve o compensa; para reducirlo usa una sustitutiva o rectificativa.");
        var (kd, kr) = Casillas[modelo];
        var aIngresar = Math.Max(0, d.Resultado) - anterior.Ingresado;
        if (aIngresar < 0) aIngresar = 0;
        d.Casillas.RemoveAll(c => c.Clave == kd);
        d.Casillas.Add(new Casilla(kd, "A deducir: ingresado con las presentaciones anteriores del periodo", anterior.Ingresado));
        var i = d.Casillas.FindIndex(c => c.Clave == kr);
        var res = new Casilla(kr, "Resultado a ingresar de la complementaria", aIngresar);
        if (i >= 0) d.Casillas[i] = res; else d.Casillas.Add(res);
        d.AIngresar = aIngresar;
        d.Avisos.Add($"Complementaria de la presentación nº {anterior.Numero}: resultado {d.Resultado:N2}, ya ingresado {anterior.Ingresado:N2}.");
    }
}
