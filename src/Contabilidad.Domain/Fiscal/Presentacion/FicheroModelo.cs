using System.Globalization;

namespace Contabilidad.Domain.Fiscal.Presentacion;

/// <summary>Datos que el usuario indica al descargar el fichero (domiciliación, persona de contacto…).</summary>
public sealed class FicheroAeatCmd
{
    /// <summary>Domiciliar el ingreso en esta cuenta (tipo U) o recibir la devolución en ella.</summary>
    public string? Iban { get; set; }
    public bool Domiciliar { get; set; }
    public string? Telefono { get; set; }
    public string? Contacto { get; set; }
    public string? Email { get; set; }
    /// <summary>Clave de actividad e IAE de la página 4 del 303 (exonerados del 390, último periodo).</summary>
    public bool Exonerado390 { get; set; }
    public string? ActividadClave { get; set; }
    public string? ActividadIae { get; set; }
    /// <summary>Descripción de la actividad principal (datos estadísticos del 390).</summary>
    public string? ActividadNombre { get; set; }
    // ---- XML de la ATC (420 y 425) ----
    /// <summary>Siglas de la vía pública del domicilio (CL, AV…).</summary>
    public string? SiglasVia { get; set; }
    /// <summary>Código del municipio del domicilio.</summary>
    public string? CodigoMunicipio { get; set; }
    /// <summary>Forma de pago del ingreso en la ATC: 1 efectivo, 2 adeudo en cuenta, 3 pago fraccionado, 4 domiciliación, 5 pago telemático.</summary>
    public string? FormaPagoAtc { get; set; }
    // ---- 200 y 202 ----
    /// <summary>Código CNAE de la actividad principal (4 dígitos).</summary>
    public string? Cnae { get; set; }
}

/// <summary>Registro de detalle (un tercero del 347, un operador del 349, un perceptor del 190…).</summary>
public sealed class RegistroAeat : Dictionary<string, object?>
{
    public RegistroAeat() : base(StringComparer.Ordinal) { }
}

/// <summary>
/// Todo lo que hace falta para escribir el fichero de una declaración guardada. <see cref="Inicio"/> y <see cref="Fin"/>: periodo
/// impositivo (200 y 202). <see cref="Extra"/>: valores por clave completa que no salen de las casillas guardadas (balance del 200
/// «B:nnnnn», caracteres de la declaración «F:nnnnn», datos de la empresa «D:x»).
/// </summary>
public sealed record DeclaracionParaFichero(
    long Id, string Modelo, int Anio, string Periodo, string TipoDeclaracion, string Nif, string Nombre,
    IReadOnlyList<Casilla> Casillas, decimal Resultado, decimal ADevolver, string? JustificanteAnterior,
    IReadOnlyDictionary<string, IReadOnlyList<RegistroAeat>> Registros,
    bool CriterioCaja = false, bool DestinatarioCaja = false, bool RegimenSimplificado = false, bool PresentaModelo347 = false,
    DateOnly? Inicio = null, DateOnly? Fin = null, IReadOnlyDictionary<string, object?>? Extra = null);

/// <summary>
/// Fichero de presentación de un modelo con el diseño de registro vigente en su periodo, para importarlo en el formulario de la
/// Sede electrónica de la AEAT («Importar» en la presentación por formulario) o presentarlo por fichero.
/// </summary>
public static class FicheroModelo
{
    public const string VersionPrograma = "1.00";

    public static readonly string[] Modelos = ["303", "390", "111", "115", "130", "131", "309", "347", "349", "190", "180", "200", "202"];

    private static IEnumerable<DisenoAeat> Disenos => DisenosAeat.Todos.Concat(DisenosAeatOficiales.Todos).Append(Diseno180.M180);

    public static DisenoAeat Diseno(string modelo, int anio)
    {
        var inicio = new DateOnly(anio, 1, 1);
        return Disenos.Where(d => d.Modelo == modelo && d.Desde <= inicio && (d.Hasta is null || d.Hasta >= inicio))
                   .OrderByDescending(d => d.Desde).FirstOrDefault()
               ?? throw new ReglaContableException($"No hay diseño de registro del modelo {modelo} para {anio}.",
                   Disenos.Any(d => d.Modelo == modelo)
                       ? $"Disponible desde {Disenos.Where(d => d.Modelo == modelo).Min(d => d.Desde).Year}."
                       : $"El fichero está disponible para los modelos {string.Join(", ", Modelos)}.");
    }

    public static (byte[] Contenido, string Nombre) Generar(DeclaracionParaFichero d, FicheroAeatCmd cmd, string? nifDesarrollador = null)
    {
        var diseno = Diseno(d.Modelo, d.Anio);
        var datos = new Datos(d, cmd, nifDesarrollador);
        var contenido = GeneradorFicheroAeat.Generar(diseno, datos);
        return (contenido, $"{d.Nif}_{d.Modelo}_{d.Anio}_{d.Periodo}.txt");
    }

    /// <summary>Letra del tipo de declaración (I ingreso, U domiciliación, N sin actividad o negativa, C compensar, D devolución, B a deducir).</summary>
    public static string Tipo(DeclaracionParaFichero d, FicheroAeatCmd cmd)
    {
        var r = d.Modelo switch
        {
            "303" => Importe(d.Casillas, "71") ?? d.Resultado,
            "111" => Importe(d.Casillas, "30") ?? d.Resultado,
            "115" => Importe(d.Casillas, "05") ?? d.Resultado,
            "130" => Importe(d.Casillas, "19") ?? d.Resultado,
            "131" => Importe(d.Casillas, "15") ?? d.Resultado,
            "200" => Importe(d.Casillas, "00621") ?? d.Resultado,
            _ => d.Resultado,
        };
        if (r == 0) return d.Modelo == "309" ? "I" : "N";
        if (r > 0) return cmd.Domiciliar ? "U" : "I";
        return d.Modelo switch
        {
            "303" => d.ADevolver > 0 ? "D" : "C",
            "130" or "131" => d.Periodo == "4T" ? "N" : "B",
            "200" => "D",
            _ => "N",
        };
    }

    public static decimal? Importe(IReadOnlyList<Casilla> casillas, string clave)
    {
        var c = casillas.FirstOrDefault(x => x.Clave == clave);
        if (c is null && int.TryParse(clave, out var n))
            c = casillas.FirstOrDefault(x => int.TryParse(x.Clave, out var m) && m == n);
        return c?.Importe;
    }

    private static string? Iban(string? iban) => string.IsNullOrWhiteSpace(iban) ? null : new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private sealed class Datos(DeclaracionParaFichero d, FicheroAeatCmd cmd, string? nifDesarrollador) : IDatosAeat
    {
        // Prorrata del 390: G general o E especial, solo si el bloque tiene datos
        private string TipoProrrata(string porcentaje, string tipo)
            => Importe(d.Casillas, porcentaje) is null ? "" : Importe(d.Casillas, tipo) == 1 ? "E" : "G";

        private readonly string _tipo = Tipo(d, cmd);
        private bool UltimoPeriodo => d.Periodo is "4T" or "12";

        public IEnumerable<IDatosAeat> Coleccion(string clave)
            => d.Registros.TryGetValue(clave[2..], out var l) ? l.Select(r => (IDatosAeat)new Registro(r, this)) : [];

        private decimal R200 => Importe(d.Casillas, "00621") ?? d.Resultado;
        private string FechaD(DateOnly? f, string formato) => f?.ToString(formato, CultureInfo.InvariantCulture) ?? "";
        private decimal Suma(string campo) => d.Registros.TryGetValue("registros", out var l)
            ? l.Sum(x => x.TryGetValue(campo, out var v) && v is decimal m ? m : 0m) : 0m;
        private decimal? Extra(string k) => d.Extra is not null && d.Extra.TryGetValue(k, out var v) && v is decimal m ? m : null;

        /// <summary>Tipo de gravamen del 200 en 4 dígitos: 2500 con un solo tipo, 2122 con dos (21 % y 22 %).</summary>
        private string TipoGravamen200()
        {
            var resto = Importe(d.Casillas, "00558") ?? 0;
            var t1 = Importe(d.Casillas, "TG1") ?? resto;
            return t1 == resto ? $"{(int)resto:00}00" : $"{(int)t1:00}{(int)resto:00}";
        }

        public object? Valor(string clave)
        {
            if (d.Extra is not null && d.Extra.TryGetValue(clave, out var extra)) return extra;
            var (tipo, nombre) = (clave[..2], clave[2..]);
            if (tipo == "C:") return Importe(d.Casillas, nombre);
            if (tipo != "D:") return null;
            return nombre switch
            {
                "ejercicio" => d.Anio.ToString(CultureInfo.InvariantCulture),
                "periodo" => d.Periodo,
                "nif" => d.Nif,
                "nombre" => d.Nombre,
                "nombrePersonaFisica" => d.Nif.Length > 0 && char.IsAsciiDigit(d.Nif[0]) ? d.Nombre : "",
                "tipo" => _tipo,
                "sinActividad" => _tipo == "N" && d.Modelo == "303",
                "complementaria" => d.TipoDeclaracion == "COMPLEMENTARIA",
                "sustitutiva" => d.TipoDeclaracion == "SUSTITUTIVA",
                "complementaria347" => d.TipoDeclaracion switch { "COMPLEMENTARIA" => "C", "SUSTITUTIVA" => " S", _ => "" },
                "complementaria349" => d.TipoDeclaracion switch { "COMPLEMENTARIA" => "C", "SUSTITUTIVA" => "S", _ => "" },
                "justificanteAnterior" => d.TipoDeclaracion == "ORDINARIA" ? "" : d.JustificanteAnterior ?? "",
                "iban" => _tipo is "U" or "D" || (d.Modelo != "303" && _tipo == "U") ? Iban(cmd.Iban) : null,
                "marcaSepa" when d.Modelo != "200" => _tipo == "D" ? 1 : 0,
                "devolucionMensual" => false,
                "siiVoluntario" => false,
                "criterioCaja" => d.CriterioCaja ? "1" : "2",
                "destinatarioCaja" => d.DestinatarioCaja ? "1" : "2",
                "regimenSimplificado" => d.RegimenSimplificado ? "2" : "3",
                "exonerado390" => UltimoPeriodo ? (cmd.Exonerado390 ? 1 : 2) : 0,
                "volumenOperaciones" => UltimoPeriodo && cmd.Exonerado390 ? 1 : 0,
                "pagoCuentaCarburantes" => d.Anio == 2026 && d.Periodo == "01" ? "" : d.Periodo.EndsWith('T') ? 0 : 2,
                "pagina4" => UltimoPeriodo && cmd.Exonerado390,
                "actividad1Clave" => cmd.ActividadClave,
                "actividad1Iae" => cmd.ActividadIae,
                "actividad1Nombre" => cmd.ActividadNombre,
                "prorrata1Tipo" => TipoProrrata("118", "117"),
                "prorrata2Tipo" => TipoProrrata("123", "122"),
                "nunca" => false,
                "presenta347" => d.PresentaModelo347,
                "versionPrograma" => VersionPrograma,
                "nifDesarrollador" => nifDesarrollador ?? "",
                "soporte" => "T",
                "telefono" => new string((cmd.Telefono ?? "").Where(char.IsAsciiDigit).ToArray()),
                "contacto" => cmd.Contacto,
                "email" => cmd.Email,
                "numeroDeclaracion" => d.Modelo + (d.Id % 10_000_000_000L).ToString("0000000000", CultureInfo.InvariantCulture),
                "numeroRegistros" => d.Registros.TryGetValue("registros", out var r) ? r.Count : 0,
                "importeTotal" => d.Registros.TryGetValue("registros", out var t) ? t.Sum(x => x.TryGetValue("importe", out var v) && v is decimal m ? m : 0m) : 0m,
                "numeroInmuebles" => 0,
                "importeInmuebles" => 0m,
                "bdns" => d.Anio > 2024 ? "000000" : "",
                "cero" => d.Anio > 2023 ? "0" : "",
                // ---- 180
                "retencionTotal" => Suma("retencion"),
                // ---- 200 y 202: periodo impositivo e identificación
                "inicioAnio" => FechaD(d.Inicio, "yyyy"), "inicioMes" => FechaD(d.Inicio, "MM"), "inicioDia" => FechaD(d.Inicio, "dd"),
                "finAnio" => FechaD(d.Fin, "yyyy"), "finMes" => FechaD(d.Fin, "MM"), "finDia" => FechaD(d.Fin, "dd"),
                "inicioAnio2" => FechaD(d.Inicio, "yy"), "finAnio2" => FechaD(d.Fin, "yy"), "fechaInicio" => FechaD(d.Inicio, "ddMMyyyy"),
                "cnae" => new string((cmd.Cnae ?? "").Where(char.IsAsciiDigit).ToArray()),
                "rectificativa" => d.TipoDeclaracion == "ORDINARIA" ? 0 : 1,
                "tipoGravamen200" => TipoGravamen200(),
                "cuentaCorrienteTributaria" => "0",
                "devolucion" => R200 < 0 ? "D" : "",
                "importeDevolver" => R200 < 0 ? -R200 : 0m,
                "marcaSepa" when d.Modelo == "200" => R200 < 0 && Iban(cmd.Iban) is { } ib ? (ib.StartsWith("ES", StringComparison.Ordinal) ? 1 : 2) : 0,
                "ibanDevolucion" => R200 < 0 ? Iban(cmd.Iban) : null,
                "modalidadIngreso" => R200 > 0 ? _tipo : "",
                "importeIngresar" => R200 > 0 ? R200 : 0m,
                "ibanIngreso" => R200 > 0 && _tipo == "U" ? Iban(cmd.Iban) : null,
                "resultadoCero" => R200 == 0 ? 1 : 0,
                "incnMenor20M" => Extra("D:incn") is { } i1 && i1 < 20_000_000m ? 1 : 0,
                "incn20a60M" => Extra("D:incn") is >= 20_000_000m and < 60_000_000m ? 1 : 0,
                "incn60M" => Extra("D:incn") is >= 60_000_000m ? 1 : 0,
                // ---- 202
                "nombrePila" => "",
                "modalidad202" => Importe(d.Casillas, "01") is not null || Importe(d.Casillas, "03") is not null ? "A" : "B",
                "tramoIncn" => Extra("D:incn") switch { >= 60_000_000m => 3, >= 20_000_000m => 2, >= 10_000_000m => 1, _ => 0 },
                "incnMas6M" => Extra("D:incn") is > 6_000_000m ? "X" : "",
                "complementaria202" => d.TipoDeclaracion == "COMPLEMENTARIA" ? "X" : "",
                _ => null,
            };
        }
    }

    private sealed class Registro(RegistroAeat r, IDatosAeat padre) : IDatosAeat
    {
        public IEnumerable<IDatosAeat> Coleccion(string clave) => [];
        public object? Valor(string clave)
            => clave.StartsWith("I:", StringComparison.Ordinal) ? (r.TryGetValue(clave[2..], out var v) ? v : null) : padre.Valor(clave);
    }
}
