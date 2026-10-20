using System.Globalization;
using System.Text;

namespace Contabilidad.Domain.Tesoreria;

/// <summary>Movimiento de un extracto bancario. <see cref="Importe"/>: positivo, abono (ingreso); negativo, cargo.</summary>
public sealed record LineaExtracto(DateOnly Fecha, DateOnly? FechaValor, decimal Importe, string Concepto, string? ConceptoComun = null,
    string? Documento = null, string? Referencia1 = null, string? Referencia2 = null)
{
    /// <summary>Clave para detectar la misma línea importada dos veces.</summary>
    public string Clave => string.Join('|', Fecha.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        Importe.ToString("0.00", CultureInfo.InvariantCulture), Concepto.Trim().ToUpperInvariant(), Documento ?? "", Referencia1 ?? "", Referencia2 ?? "");
}

/// <summary>Extracto de una cuenta: en Norma 43, entidad + oficina + número de cuenta (sin dígitos de control).</summary>
public sealed record ExtractoLeido(string? Entidad, string? Oficina, string? Cuenta, DateOnly Desde, DateOnly Hasta,
    decimal? SaldoInicial, decimal? SaldoFinal, IReadOnlyList<LineaExtracto> Lineas)
{
    /// <summary>¿Corresponde a este IBAN español? (ES kk EEEE OOOO DC NNNNNNNNNN)</summary>
    public bool EsDeIban(string iban)
    {
        var i = iban.Replace(" ", "").ToUpperInvariant();
        return Cuenta is not null && i.Length == 24 && i.StartsWith("ES", StringComparison.Ordinal)
               && i[4..8] == Entidad && i[8..12] == Oficina && i[14..24] == Cuenta;
    }
}

public sealed record ResultadoLectura(string Formato, IReadOnlyList<ExtractoLeido> Extractos, IReadOnlyList<string> Avisos);

public static class LectorExtractos
{
    /// <summary>Detecta el formato (Norma 43 si las líneas empiezan por 11/22/23/33/88) y lee el fichero.</summary>
    public static ResultadoLectura Leer(byte[] contenido)
    {
        var texto = Decodificar(contenido);
        var lineas = texto.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lineas.Length == 0) throw new ReglaContableException("El fichero está vacío.");
        // Cabecera Norma 43: «11» y 45 dígitos (entidad, oficina, cuenta, fechas, clave y saldo); los espacios finales pueden faltar
        var l0 = lineas[0];
        return l0.Length >= 47 && l0.StartsWith("11", StringComparison.Ordinal) && l0[2..47].All(char.IsAsciiDigit)
            ? Norma43(lineas)
            : Csv(lineas);
    }

    /// <summary>Los bancos generan Norma 43 en ISO-8859-1; si el fichero es UTF-8 válido, se lee como UTF-8.</summary>
    private static string Decodificar(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }

    // ------------------------------------------------------------------ Norma 43 (AEB, cuaderno 43)
    public static ResultadoLectura Norma43(IEnumerable<string> registros)
    {
        var extractos = new List<ExtractoLeido>();
        var avisos = new List<string>();
        string? entidad = null, oficina = null, cuenta = null;
        DateOnly desde = default, hasta = default;
        decimal saldoInicial = 0;
        List<LineaExtracto>? movs = null;
        LineaExtracto? actual = null;
        var complementos = new List<string>();
        var n = 0;

        void CerrarMovimiento()
        {
            if (actual is null) return;
            var extra = string.Join(' ', complementos.Where(c => c.Length > 0));
            movs!.Add(actual with { Concepto = Compactar($"{actual.Concepto} {extra}") });
            actual = null; complementos.Clear();
        }

        foreach (var bruta in registros)
        {
            n++;
            var r = bruta.TrimEnd('\r', '\n').PadRight(80);
            switch (r[..2])
            {
                case "11":
                    if (movs is not null) throw new ReglaContableException($"Norma 43, registro {n}: cabecera de cuenta sin cerrar la anterior (falta el registro 33).");
                    entidad = r[2..6]; oficina = r[6..10]; cuenta = r[10..20];
                    desde = Fecha(r[20..26], n); hasta = Fecha(r[26..32], n);
                    saldoInicial = Importe(r[33..47], n) * (r[32] == '1' ? -1 : 1);
                    movs = [];
                    break;
                case "22":
                    if (movs is null) throw new ReglaContableException($"Norma 43, registro {n}: movimiento sin cabecera de cuenta.");
                    CerrarMovimiento();
                    var importe = Importe(r[28..42], n) * (r[27] == '1' ? -1 : 1);
                    if (importe == 0) { avisos.Add($"Registro {n}: movimiento de importe cero, se omite."); break; }
                    actual = new LineaExtracto(Fecha(r[10..16], n), FechaOpcional(r[16..22]), importe, Compactar($"{r[52..64]} {r[64..80]}"),
                        r[22..24], Vacio(r[42..52]), Vacio(r[52..64]), Vacio(r[64..80]));
                    break;
                case "23":
                    if (actual is null) throw new ReglaContableException($"Norma 43, registro {n}: concepto complementario sin movimiento.");
                    complementos.Add(Compactar(r[4..42] + " " + r[42..80]));
                    break;
                case "24":
                    break;                                         // equivalencia de divisa: no se usa
                case "33":
                    if (movs is null) throw new ReglaContableException($"Norma 43, registro {n}: final de cuenta sin cabecera.");
                    CerrarMovimiento();
                    var nDebe = int.Parse(r[20..25], CultureInfo.InvariantCulture);
                    var totalDebe = Importe(r[25..39], n);
                    var nHaber = int.Parse(r[39..44], CultureInfo.InvariantCulture);
                    var totalHaber = Importe(r[44..58], n);
                    var saldoFinal = Importe(r[59..73], n) * (r[58] == '1' ? -1 : 1);
                    var cargos = movs.Where(m => m.Importe < 0).ToList();
                    var abonos = movs.Where(m => m.Importe > 0).ToList();
                    if (cargos.Count != nDebe || -cargos.Sum(m => m.Importe) != totalDebe || abonos.Count != nHaber || abonos.Sum(m => m.Importe) != totalHaber)
                        throw new ReglaContableException($"Norma 43, cuenta {cuenta}: los totales del registro 33 no coinciden con los movimientos. El fichero puede estar incompleto.");
                    if (saldoInicial + movs.Sum(m => m.Importe) != saldoFinal)
                        avisos.Add($"Cuenta {cuenta}: el saldo inicial más los movimientos no da el saldo final del fichero.");
                    extractos.Add(new ExtractoLeido(entidad, oficina, cuenta, desde, hasta, saldoInicial, saldoFinal, movs));
                    movs = null;
                    break;
                case "88":
                    break;
                default:
                    throw new ReglaContableException($"Norma 43, registro {n}: tipo de registro «{r[..2]}» desconocido.");
            }
        }
        if (movs is not null) throw new ReglaContableException("Norma 43: el fichero termina sin el registro 33 de la última cuenta.");
        if (extractos.Count == 0) throw new ReglaContableException("Norma 43: el fichero no contiene ninguna cuenta.");
        return new ResultadoLectura("NORMA43", extractos, avisos);
    }

    private static DateOnly Fecha(string aammdd, int n)
        => FechaOpcional(aammdd) ?? throw new ReglaContableException($"Norma 43, registro {n}: fecha no válida «{aammdd}».");

    private static DateOnly? FechaOpcional(string aammdd)
        => DateOnly.TryParseExact(aammdd, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal Importe(string catorce, int n)
        => long.TryParse(catorce, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c / 100m
           : throw new ReglaContableException($"Norma 43, registro {n}: importe no válido «{catorce}».");

    private static string? Vacio(string s) => string.IsNullOrWhiteSpace(s) || s.Trim().All(ch => ch == '0') ? null : s.Trim();
    private static string Compactar(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------------ CSV
    /// <summary>
    /// CSV con cabecera. Columnas reconocidas (sin distinguir mayúsculas ni acentos): fecha, fecha valor, concepto (o descripción),
    /// importe — o bien cargo y abono por separado —, referencia y saldo. Separador «;», «,» o tabulador; fechas dd/mm/aaaa o aaaa-mm-dd;
    /// importes con coma o punto decimal.
    /// </summary>
    public static ResultadoLectura Csv(IReadOnlyList<string> lineas)
    {
        var sep = new[] { ';', '\t', ',' }.OrderByDescending(c => lineas[0].Count(ch => ch == c)).First();
        var cab = Partir(lineas[0], sep).Select(Normalizar).ToList();
        int Col(params string[] nombres) => cab.FindIndex(c => nombres.Contains(c));
        var iFecha = Col("fecha", "fecha operacion", "f. operacion", "fecha contable");
        var iValor = Col("fecha valor", "f. valor");
        var iConcepto = Col("concepto", "descripcion", "movimiento", "detalle");
        var iImporte = Col("importe", "cantidad");
        var iCargo = Col("cargo", "debe", "cargos");
        var iAbono = Col("abono", "haber", "abonos");
        var iRef = Col("referencia", "ref", "documento");
        var iSaldo = Col("saldo");
        if (iFecha < 0 || (iImporte < 0 && (iCargo < 0 || iAbono < 0)))
            throw new ReglaContableException("CSV: la primera fila debe tener las columnas «fecha» e «importe» (o «cargo» y «abono»).");

        var movs = new List<(LineaExtracto Linea, decimal? Saldo)>();
        var avisos = new List<string>();
        for (var i = 1; i < lineas.Count; i++)
        {
            var c = Partir(lineas[i], sep);
            if (c.All(string.IsNullOrWhiteSpace)) continue;
            string Dato(int k) => k >= 0 && k < c.Count ? c[k].Trim() : "";
            var fecha = FechaCsv(Dato(iFecha)) ?? throw new ReglaContableException($"CSV, fila {i + 1}: fecha no válida «{Dato(iFecha)}».");
            decimal importe;
            if (iImporte >= 0) importe = ImporteCsv(Dato(iImporte), i);
            else importe = (Dato(iAbono).Length > 0 ? Math.Abs(ImporteCsv(Dato(iAbono), i)) : 0) - (Dato(iCargo).Length > 0 ? Math.Abs(ImporteCsv(Dato(iCargo), i)) : 0);
            if (importe == 0) { avisos.Add($"Fila {i + 1}: importe cero, se omite."); continue; }
            var concepto = Dato(iConcepto);
            if (concepto.Length > 400) concepto = concepto[..400];
            var referencia = Dato(iRef);
            if (referencia.Length > 20) referencia = referencia[..20];
            movs.Add((new LineaExtracto(fecha, FechaCsv(Dato(iValor)), importe, Compactar(concepto), Referencia1: referencia.Length > 0 ? referencia : null),
                      iSaldo >= 0 && Dato(iSaldo).Length > 0 ? ImporteCsv(Dato(iSaldo), i) : null));
        }
        if (movs.Count == 0) throw new ReglaContableException("CSV: no hay movimientos.");

        // Muchos bancos exportan del más reciente al más antiguo: se ordenan por fecha manteniendo el orden del día
        var ordenados = movs.Select((m, k) => (m, k)).OrderBy(x => x.m.Linea.Fecha)
            .ThenBy(x => movs[0].Linea.Fecha > movs[^1].Linea.Fecha ? -x.k : x.k).Select(x => x.m).ToList();
        decimal? saldoFinal = ordenados[^1].Saldo;
        decimal? saldoInicial = ordenados[0].Saldo is { } s0 ? s0 - ordenados[0].Linea.Importe : null;
        if (saldoInicial is not null && saldoFinal is not null && saldoInicial + ordenados.Sum(m => m.Linea.Importe) != saldoFinal)
        {
            avisos.Add("CSV: la columna de saldo no cuadra con los importes; no se usa.");
            saldoInicial = saldoFinal = null;
        }
        var lineasOk = ordenados.Select(m => m.Linea).ToList();
        return new ResultadoLectura("CSV", [new ExtractoLeido(null, null, null, lineasOk[0].Fecha, lineasOk[^1].Fecha, saldoInicial, saldoFinal, lineasOk)], avisos);
    }

    private static List<string> Partir(string linea, char sep)
    {
        var campos = new List<string>(); var sb = new StringBuilder(); var comillas = false;
        for (var i = 0; i < linea.Length; i++)
        {
            var ch = linea[i];
            if (comillas)
            {
                if (ch == '"' && i + 1 < linea.Length && linea[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') comillas = false;
                else sb.Append(ch);
            }
            else if (ch == '"') comillas = true;
            else if (ch == sep) { campos.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        campos.Add(sb.ToString());
        return campos;
    }

    private static string Normalizar(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }

    private static DateOnly? FechaCsv(string s)
        => DateOnly.TryParseExact(s.Trim(), ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "dd/MM/yy", "dd.MM.yyyy"],
               CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal ImporteCsv(string s, int fila)
    {
        var t = s.Replace("€", "").Replace(" ", "").Replace(" ", "").Trim();
        // Con coma y punto, el último es el decimal (1.234,56 o 1,234.56); con solo coma, coma decimal
        var coma = t.LastIndexOf(','); var punto = t.LastIndexOf('.');
        if (coma >= 0 && punto >= 0) t = coma > punto ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (coma >= 0) t = t.Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
            ? v : throw new ReglaContableException($"CSV, fila {fila + 1}: importe no válido «{s}».");
    }
}

/// <summary>Movimiento contable de la cuenta del banco: apunte con importe = debe − haber (positivo, entra dinero).</summary>
public sealed record ApunteBanco(long ApunteId, DateOnly Fecha, decimal Importe, string Concepto);

public sealed record LineaPendiente(long LineaId, DateOnly Fecha, decimal Importe, string Concepto);

public static class Conciliador
{
    /// <summary>
    /// Líneas nuevas de un extracto que no estaban ya importadas. Se cuentan las repeticiones de cada clave: si el banco carga
    /// dos comisiones iguales el mismo día, las dos son líneas distintas, pero reimportar el fichero no las duplica.
    /// </summary>
    public static IReadOnlyList<LineaExtracto> Nuevas(IEnumerable<LineaExtracto> leidas, IEnumerable<string> clavesExistentes)
    {
        var existentes = clavesExistentes.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var nuevas = new List<LineaExtracto>();
        foreach (var l in leidas)
        {
            if (existentes.TryGetValue(l.Clave, out var n) && n > 0) existentes[l.Clave] = n - 1;
            else nuevas.Add(l);
        }
        return nuevas;
    }

    /// <summary>
    /// Casación automática uno a uno: mismo importe y fecha a <paramref name="diasMargen"/> días como mucho, eligiendo el apunte más cercano.
    /// Líneas o apuntes con la misma fecha e importe son intercambiables. Si hay candidatos igual de cerca en fechas distintas, o
    /// otra línea de otra fecha está igual o más cerca de ese apunte, no se casa: lo decide una persona.
    /// </summary>
    public static IReadOnlyList<(long LineaId, long ApunteId)> Automatica(IEnumerable<LineaPendiente> lineas, IEnumerable<ApunteBanco> apuntes, int diasMargen)
    {
        static int Dias(DateOnly a, DateOnly b) => Math.Abs(a.DayNumber - b.DayNumber);
        var libres = apuntes.OrderBy(a => a.Fecha).ThenBy(a => a.ApunteId).ToList();
        var lineasLibres = lineas.OrderBy(l => l.Fecha).ThenBy(l => l.LineaId).ToList();
        var casadas = new List<(long, long)>();
        foreach (var l in lineasLibres.ToList())
        {
            var candidatos = libres.Where(a => a.Importe == l.Importe && Dias(a.Fecha, l.Fecha) <= diasMargen)
                                   .OrderBy(a => Dias(a.Fecha, l.Fecha)).ToList();
            if (candidatos.Count == 0) continue;
            var mejor = Dias(candidatos[0].Fecha, l.Fecha);
            var empatados = candidatos.TakeWhile(a => Dias(a.Fecha, l.Fecha) == mejor).ToList();
            if (empatados.Select(a => a.Fecha).Distinct().Count() > 1) continue;
            var elegido = empatados[0];
            if (lineasLibres.Any(o => o.LineaId != l.LineaId && o.Importe == l.Importe && o.Fecha != l.Fecha && Dias(o.Fecha, elegido.Fecha) <= mejor))
                continue;
            casadas.Add((l.LineaId, elegido.ApunteId));
            libres.Remove(elegido);
            lineasLibres.Remove(l);
        }
        return casadas;
    }
}
