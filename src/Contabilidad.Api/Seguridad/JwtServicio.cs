using System.Text;
using Contabilidad.Domain;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Contabilidad.Api.Seguridad;

public sealed class JwtOpciones
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "contabilidad";
    public string Audience { get; set; } = "contabilidad-web";
    public int Minutos { get; set; } = 480;

    public byte[] KeyBytes => Encoding.UTF8.GetBytes(Key);

    public void Validar()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length < 32)
            throw new InvalidOperationException(
                "Falta Jwt:Key o es demasiado corta (mínimo 32 caracteres). En producción defínela con la variable de entorno Jwt__Key o con un gestor de secretos.");
    }
}

/// <summary>
/// Emite el token de acceso. NO lleva permisos: se consultan en base de datos en cada petición,
/// de modo que desactivar a un usuario o quitarle una empresa surte efecto al instante.
/// </summary>
public sealed class JwtServicio(JwtOpciones opciones)
{
    public (string Token, DateTime Expira) Crear(UsuarioDto u)
    {
        var ahora = DateTime.UtcNow;
        var expira = ahora.AddMinutes(opciones.Minutos);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = opciones.Issuer,
            Audience = opciones.Audience,
            NotBefore = ahora,
            Expires = expira,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = u.Id.ToString(),
                ["name"] = u.Nombre,
                ["role"] = u.Tipo,
                ["cpw"] = u.DebeCambiarPassword ? "1" : "0"      // 1 = solo puede usar /api/auth hasta cambiar la contraseña
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(opciones.KeyBytes), SecurityAlgorithms.HmacSha256)
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expira);
    }
}
