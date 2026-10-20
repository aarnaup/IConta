namespace Contabilidad.Domain.Tesoreria;

public static class Iban
{
    /// <summary>Quita espacios y guiones y pasa a mayúsculas.</summary>
    public static string Normalizar(string? iban)
        => new((iban ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>Formato (2 letras + 2 dígitos + alfanuméricos, 15–34) y dígitos de control ISO 7064 mod 97-10.</summary>
    public static bool EsValido(string? iban)
    {
        var s = Normalizar(iban);
        if (s.Length is < 15 or > 34) return false;
        if (!(char.IsAsciiLetterUpper(s[0]) && char.IsAsciiLetterUpper(s[1]) && char.IsAsciiDigit(s[2]) && char.IsAsciiDigit(s[3]))) return false;
        if (!s.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))) return false;
        if (s.StartsWith("ES") && s.Length != 24) return false;

        var resto = 0;
        foreach (var c in s[4..] + s[..4])
        {
            var valor = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;   // las letras ocupan dos cifras (A=10 … Z=35)
            resto = valor < 10 ? (resto * 10 + valor) % 97 : (resto * 100 + valor) % 97;
        }
        return resto == 1;
    }
}
