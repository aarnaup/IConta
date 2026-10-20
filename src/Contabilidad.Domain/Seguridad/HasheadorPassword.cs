using System.Security.Cryptography;

namespace Contabilidad.Domain.Seguridad;

/// <summary>PBKDF2-SHA256 con sal aleatoria. Formato: pbkdf2-sha256$iteraciones$sal$hash (base64).</summary>
public static class HasheadorPassword
{
    private const int Iteraciones = 600_000;   // recomendación OWASP para PBKDF2-HMAC-SHA256
    private const int LongSal = 16;
    private const int LongHash = 32;
    private const string Prefijo = "pbkdf2-sha256";
    public const int LongitudMinima = 12;
    public const int LongitudMaxima = 128;

    private static readonly Lazy<string> HashFalso = new(() => Hash("contraseña-falsa-para-igualar-tiempos"));

    public static string Hash(string password)
    {
        var sal = RandomNumberGenerator.GetBytes(LongSal);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, sal, Iteraciones, HashAlgorithmName.SHA256, LongHash);
        return $"{Prefijo}${Iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string password, string? almacenado)
    {
        if (string.IsNullOrEmpty(almacenado)) return false;
        var partes = almacenado.Split('$');
        if (partes.Length != 4 || partes[0] != Prefijo || !int.TryParse(partes[1], out var it) || it < 1) return false;
        try
        {
            var sal = Convert.FromBase64String(partes[2]);
            var esperado = Convert.FromBase64String(partes[3]);
            var calculado = Rfc2898DeriveBytes.Pbkdf2(password, sal, it, HashAlgorithmName.SHA256, esperado.Length);
            return CryptographicOperations.FixedTimeEquals(calculado, esperado);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Gasta el mismo tiempo que una verificación real (usuario inexistente) para no revelar si el email existe.</summary>
    public static void VerificarFalso(string password) => Verificar(password, HashFalso.Value);

    public static void ValidarPolitica(string password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < LongitudMinima)
            throw new ReglaContableException($"La contraseña debe tener al menos {LongitudMinima} caracteres.");
        if (password.Length > LongitudMaxima)
            throw new ReglaContableException($"La contraseña no puede superar {LongitudMaxima} caracteres.");
        if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            throw new ReglaContableException("La contraseña no puede ser igual al email.");
        if (password.Distinct().Count() < 5)
            throw new ReglaContableException("La contraseña es demasiado repetitiva.");
    }
}
