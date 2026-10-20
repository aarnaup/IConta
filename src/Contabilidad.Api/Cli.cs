using Contabilidad.Domain;
using Contabilidad.Domain.Seguridad;
using Contabilidad.Infrastructure.Repositorios;

namespace Contabilidad.Api;

/// <summary>dotnet run --project src/Contabilidad.Api -- crear-superadmin &lt;email&gt; "&lt;nombre&gt;"</summary>
public static class Cli
{
    public static async Task<int> CrearSuperadminAsync(IServiceProvider servicios, string email, string nombre)
    {
        try
        {
            var password = Environment.GetEnvironmentVariable("CONTABILIDAD_ADMIN_PASSWORD") ?? LeerPassword();
            HasheadorPassword.ValidarPolitica(password, email);

            using var scope = servicios.CreateScope();
            var usuarios = scope.ServiceProvider.GetRequiredService<IUsuarioRepositorio>();
            var id = await usuarios.CrearAsync(email.Trim(), nombre.Trim(), "GESTOR", esSuperadmin: true,
                HasheadorPassword.Hash(password), debeCambiar: false, CancellationToken.None);
            Console.WriteLine($"Superadmin creado (id {id}). Ya puedes entrar con {email.Trim()}.");
            return 0;
        }
        catch (ReglaContableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string LeerPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        Console.Write("Contraseña (mínimo 12 caracteres): ");
        var sb = new System.Text.StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
