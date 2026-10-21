namespace Contabilidad.Domain.Fiscal.Presentacion;

/// <summary>
/// Diseño de registro del modelo 180 (resumen anual de retenciones por arrendamiento de inmuebles urbanos), según el documento de la
/// Agencia Tributaria «DR_Mod_180» (ejercicio 2023, vigente desde entonces): un registro de declarante (tipo 1) y uno por perceptor e
/// inmueble (tipo 2), de 500 posiciones cada uno.
/// </summary>
public static class Diseno180
{
    private static CampoAeat A(string n, int l, string? clave = null, string? fijo = null) => new(n, TipoCampoAeat.Alfanumerico, l, Clave: clave, Fijo: fijo);
    private static CampoAeat N(string n, int l, string? clave = null, string? fijo = null, int dec = 0) => new(n, TipoCampoAeat.Numerico, l, dec, Clave: clave, Fijo: fijo);
    // Importe con signo: «N» delante si es negativo y blanco si no
    private static CampoAeat S(string n, int l, string clave) => new(n, TipoCampoAeat.Numerico, l, 2, Signo: true, Positivo: ' ', Clave: clave);

    // Cada registro termina en CRLF, como en los demás modelos informativos
    private static readonly CampoAeat Fin = new("Fin de registro (CRLF)", TipoCampoAeat.Alfanumerico, 2, Literal: "\r\n");

    public static readonly DisenoAeat M180 = new("180", new DateOnly(2023, 1, 1), null,
    [
        new BloqueAeat("Registro de declarante (tipo 1)",
        [
            N("1 Tipo de registro", 1, fijo: "1"), N("2-4 Modelo", 3, fijo: "180"), N("5-8 Ejercicio", 4, "D:ejercicio"),
            A("9-17 NIF del declarante", 9, "D:nif"), A("18-57 Apellidos y nombre o razón social", 40, "D:nombre"),
            A("58 Tipo de soporte", 1, fijo: "T"), N("59-67 Teléfono de contacto", 9, "D:telefono"), A("68-107 Persona de contacto", 40, "D:contacto"),
            N("108-120 Número identificativo de la declaración", 13, "D:numeroDeclaracion"),
            A("121-122 Declaración complementaria o sustitutiva", 2, "D:complementaria347"),
            N("123-135 Número identificativo de la declaración anterior", 13, "D:justificanteAnterior"),
            N("136-144 Número total de perceptores", 9, "D:numeroRegistros"),
            S("145-160 Base de retenciones e ingresos a cuenta", 16, "D:importeTotal"),
            N("161-175 Retenciones e ingresos a cuenta", 15, "D:retencionTotal", dec: 2),
            A("176-237 Blancos", 62), A("238-500 Sello electrónico", 263), Fin,
        ]),
        new BloqueAeat("Registro de perceptor (tipo 2)",
        [
            N("1 Tipo de registro", 1, fijo: "2"), N("2-4 Modelo", 3, fijo: "180"), N("5-8 Ejercicio", 4, "D:ejercicio"),
            A("9-17 NIF del declarante", 9, "D:nif"), A("18-26 NIF del perceptor", 9, "I:nif"), A("27-35 NIF del representante legal", 9, "I:nifRepresentante"),
            A("36-75 Apellidos y nombre o razón social del perceptor", 40, "I:nombre"), N("76-77 Código de provincia", 2, "I:provincia"),
            N("78 Modalidad", 1, "I:modalidad"), S("79-92 Base de retenciones e ingresos a cuenta", 14, "I:base"),
            N("93-96 % de retención", 4, "I:porcentaje", dec: 2), N("97-109 Retenciones e ingresos a cuenta", 13, "I:retencion", dec: 2),
            N("110-113 Ejercicio de devengo", 4, "I:ejercicioDevengo"), N("114 Situación del inmueble", 1, "I:situacion"),
            A("115-134 Referencia catastral", 20, "I:referenciaCatastral"),
            A("135-139 Tipo de vía", 5, "I:tipoVia"), A("140-189 Nombre de la vía pública", 50, "I:nombreVia"),
            A("190-192 Tipo de numeración", 3, "I:tipoNumeracion"), A("193-197 Número de casa", 5, "I:numero"),
            A("198-200 Calificador del número", 3), A("201-203 Bloque", 3), A("204-206 Portal", 3), A("207-209 Escalera", 3),
            A("210-212 Planta o piso", 3), A("213-215 Puerta", 3), A("216-255 Complemento", 40, "I:complemento"),
            A("256-285 Localidad o población", 30), A("286-315 Municipio", 30, "I:municipio"), A("316-320 Código de municipio", 5, "I:codigoMunicipio"),
            N("321-322 Código de provincia del inmueble", 2, "I:provinciaInmueble"), A("323-327 Código postal", 5, "I:codigoPostal"),
            A("328-500 Blancos", 173), Fin,
        ], Coleccion: "R:registros"),
    ]);

    /// <summary>Registro de perceptor del 180: arrendador, importes del año e inmueble.</summary>
    public static RegistroAeat Registro(PerceptorResumen l, InmuebleDto i)
    {
        // El número de casa va en su campo de 5 posiciones; si no es numérico («S/N», «3 BIS»…) se separa el calificador
        var numero = (i.Numero ?? "").Trim().ToUpperInvariant();
        var digitos = new string(numero.TakeWhile(char.IsAsciiDigit).ToArray());
        var cp = (i.CodigoPostal ?? "").Trim();
        return new RegistroAeat
        {
            ["nif"] = Nif(l.Nif), ["nifRepresentante"] = "", ["nombre"] = l.Nombre, ["provincia"] = l.Provincia,
            ["modalidad"] = l.Especie != 0 && l.Base == 0 ? 2m : 1m,
            ["base"] = l.Base, ["importe"] = l.Base, ["porcentaje"] = l.Porcentaje, ["retencion"] = Math.Abs(l.Retencion),
            ["ejercicioDevengo"] = 0m, ["situacion"] = (decimal)i.Situacion,
            ["referenciaCatastral"] = i.Situacion == 4 ? "" : i.ReferenciaCatastral ?? "",
            ["tipoVia"] = i.TipoVia ?? "", ["nombreVia"] = i.NombreVia,
            ["tipoNumeracion"] = digitos.Length > 0 ? "NUM" : numero == "" || numero == "S/N" ? "S/N" : "",
            ["numero"] = digitos.Length > 0 ? digitos : "",
            ["complemento"] = string.Join(" ", new[] { digitos.Length > 0 ? numero[digitos.Length..].Trim() : numero == "S/N" ? "" : numero,
                                                       i.Complemento ?? "" }.Where(x => x.Length > 0)),
            ["municipio"] = i.Municipio ?? "", ["codigoMunicipio"] = i.CodigoMunicipio ?? "",
            ["provinciaInmueble"] = cp.Length >= 2 ? cp[..2] : (i.CodigoMunicipio ?? "").Length >= 2 ? i.CodigoMunicipio![..2] : "",
            ["codigoPostal"] = cp,
        };
    }

    private static string Nif(string nif)
    {
        var n = new string(nif.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return n.StartsWith("ES", StringComparison.Ordinal) && n.Length == 11 ? n[2..] : n;
    }
}
