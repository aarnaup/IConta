using System.Globalization;
using System.Text;

namespace Contabilidad.Domain.Fiscal.Presentacion;

public enum TipoCampoAeat { Alfanumerico, Alfabetico, Numerico, Booleano }

/// <summary>Elemento de un diseño de registro: un campo o un bloque (página, registro de detalle repetido…).</summary>
public abstract record ElementoAeat(string Nombre);

/// <summary>
/// Campo de longitud fija. El valor sale de <see cref="Fijo"/>, de <see cref="Plantilla"/> (texto con {clave}) o de <see cref="Clave"/>
/// (dato de la declaración: «C:27» casilla, «D:nif» dato general, «I:nif» dato del registro repetido). <see cref="Literal"/> se escribe
/// tal cual, sin pasar por el formato (el salto de línea CRLF de algunos modelos).
/// </summary>
public sealed record CampoAeat(string Nombre, TipoCampoAeat Tipo, int Longitud, int Decimales = 0, bool Signo = false,
    char Positivo = '0', char Negativo = 'N', string? Fijo = null, string? Plantilla = null, string? Clave = null, bool Derecha = false,
    char Si = 'X', char No = ' ', string? Literal = null) : ElementoAeat(Nombre);

/// <summary>
/// Grupo de elementos: una página que solo va si <see cref="Condicion"/> es verdadera, o un registro que se repite por cada
/// elemento de la colección <see cref="Coleccion"/> (terceros del 347, operadores del 349…).
/// </summary>
public sealed record BloqueAeat(string Nombre, IReadOnlyList<ElementoAeat> Elementos, string? Condicion = null, string? Coleccion = null)
    : ElementoAeat(Nombre);

/// <summary>Diseño de registro de un modelo, vigente para los periodos con inicio entre <see cref="Desde"/> y <see cref="Hasta"/>.</summary>
public sealed record DisenoAeat(string Modelo, DateOnly Desde, DateOnly? Hasta, IReadOnlyList<ElementoAeat> Elementos)
{
    /// <summary>Longitud sin las páginas condicionales ni los registros repetidos.</summary>
    public int Longitud => Medir(Elementos);
    public static int Medir(IEnumerable<ElementoAeat> es)
        => es.Sum(e => e switch
        {
            CampoAeat c => c.Literal?.Length ?? c.Longitud,
            BloqueAeat b when b.Coleccion is null && b.Condicion is null => Medir(b.Elementos),
            _ => 0,
        });
}

/// <summary>Datos con los que se rellena un diseño. Las claves sin valor salen en blanco o a cero.</summary>
public interface IDatosAeat
{
    object? Valor(string clave);
    IEnumerable<IDatosAeat> Coleccion(string clave);
}

/// <summary>
/// Escribe ficheros en el formato de los diseños de registro de la AEAT (presentación por fichero / importación en el formulario):
/// alfanuméricos alineados a la izquierda con blancos, en mayúsculas, sin acentos y en ISO-8859-1 (Ñ y Ç se conservan); numéricos
/// a la derecha con ceros, sin separador decimal y, si el campo lo lleva, con «N» delante cuando son negativos.
/// </summary>
public static class GeneradorFicheroAeat
{
    public static byte[] Generar(DisenoAeat diseno, IDatosAeat datos)
    {
        var sb = new StringBuilder();
        Escribir(sb, diseno.Elementos, datos);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static void Escribir(StringBuilder sb, IReadOnlyList<ElementoAeat> elementos, IDatosAeat datos)
    {
        foreach (var e in elementos)
        {
            switch (e)
            {
                case BloqueAeat b when b.Coleccion is not null:
                    foreach (var item in datos.Coleccion(b.Coleccion)) Escribir(sb, b.Elementos, item);
                    break;
                case BloqueAeat b:
                    if (b.Condicion is null || Verdadero(datos.Valor(b.Condicion))) Escribir(sb, b.Elementos, datos);
                    break;
                case CampoAeat c:
                    sb.Append(Campo(c, datos));
                    break;
            }
        }
    }

    public static string Campo(CampoAeat c, IDatosAeat datos)
    {
        if (c.Literal is not null) return c.Literal;
        object? v = c.Fijo;
        if (c.Plantilla is not null) v = RellenarPlantilla(c.Plantilla, datos);
        else if (c.Clave is not null) v = datos.Valor(c.Clave);
        var texto = c.Tipo switch
        {
            TipoCampoAeat.Booleano => (Verdadero(v) ? c.Si : c.No).ToString(),
            TipoCampoAeat.Numerico => Numero(Decimal(v), c.Longitud - c.Decimales - (c.Signo ? 1 : 0), c.Decimales, c.Signo, c.Positivo, c.Negativo),
            TipoCampoAeat.Alfabetico => Texto(SinDigitos(Cadena(v)), c.Longitud, c.Derecha),
            _ => Texto(Cadena(v), c.Longitud, c.Derecha),
        };
        if (texto.Length != c.Longitud)
            throw new ReglaContableException($"El campo «{c.Nombre}» no cabe en {c.Longitud} posiciones.", $"Valor: {v}");
        return texto;
    }

    private static string RellenarPlantilla(string plantilla, IDatosAeat datos)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < plantilla.Length; i++)
        {
            if (plantilla[i] == '{')
            {
                var fin = plantilla.IndexOf('}', i);
                sb.Append(Cadena(datos.Valor(plantilla[(i + 1)..fin])));
                i = fin;
            }
            else sb.Append(plantilla[i]);
        }
        return sb.ToString();
    }

    public static bool Verdadero(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0 && s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
        decimal d => d != 0,
        int n => n != 0,
        long n => n != 0,
        _ => true,
    };

    private static string Cadena(object? v) => v switch
    {
        null => "",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static decimal Decimal(object? v) => v switch
    {
        null => 0m,
        decimal d => d,
        int n => n,
        long n => n,
        short n => n,
        double x => (decimal)x,
        string s when s.Trim().Length == 0 => 0m,
        string s => decimal.Parse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    /// <summary>Número sin separador decimal, con ceros a la izquierda y, si lleva signo, «N» delante de los negativos.</summary>
    public static string Numero(decimal valor, int enteros, int decimales, bool signo, char positivo = '0', char negativo = 'N')
    {
        var redondeado = Math.Round(Math.Abs(valor), decimales, MidpointRounding.AwayFromZero);
        var digitos = (redondeado * Pow10(decimales)).ToString("0", CultureInfo.InvariantCulture).PadLeft(enteros + decimales, '0');
        if (digitos.Length > enteros + decimales)
            throw new ReglaContableException($"El importe {valor:N2} no cabe en el fichero ({enteros} enteros).");
        if (!signo && valor < 0 && redondeado != 0)
            throw new ReglaContableException($"El importe {valor:N2} es negativo y la casilla no admite signo.");
        return (signo ? (valor < 0 && redondeado != 0 ? negativo : positivo).ToString() : "") + digitos;
    }

    private static decimal Pow10(int n) { var r = 1m; for (var i = 0; i < n; i++) r *= 10; return r; }

    /// <summary>
    /// Texto en mayúsculas sin acentos (salvo Ñ y Ç), solo con los caracteres admitidos, recortado o rellenado con blancos.
    /// </summary>
    public static string Texto(string? s, int longitud, bool derecha = false)
    {
        var t = Normalizar(s ?? "");
        if (t.Length > longitud) t = t[..longitud];
        return derecha ? t.PadLeft(longitud) : t.PadRight(longitud);
    }

    public static string Normalizar(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch0 in s.ToUpperInvariant())
        {
            var ch = ch0 switch { 'º' => 'O', 'ª' => 'A', '€' => 'E', _ => ch0 };
            if (ch is 'Ñ' or 'Ç') { sb.Append(ch); continue; }
            var sinAcento = Quitar(ch);
            foreach (var c in sinAcento)
                if (Admitido(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Quitar(char c)
    {
        var d = c.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var x in d)
            if (CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark) sb.Append(x);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Letras, dígitos, blancos, el rango ASCII de la coma al subrayado (, - . / : ; < = > ? @ [ \ ] ^ _) y & ' ´ ( )
    private static bool Admitido(char c)
        => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or >= ',' and <= '_' or '&' or '\'' or '´' or '(' or ')';

    private static string SinDigitos(string s) => new(s.Where(c => !char.IsAsciiDigit(c) && c != '-').ToArray());
}
