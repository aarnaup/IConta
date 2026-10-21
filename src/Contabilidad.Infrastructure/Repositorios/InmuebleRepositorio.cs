using System.Text.RegularExpressions;
using Contabilidad.Domain;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IInmuebleRepositorio
{
    Task<IReadOnlyList<InmuebleDto>> ListarAsync(long empresaId, long? terceroId, CancellationToken ct);
    Task<long> GuardarAsync(long empresaId, InmuebleDto i, CancellationToken ct);
    Task EliminarAsync(long empresaId, long id, CancellationToken ct);
}

/// <summary>Inmuebles arrendados de cada arrendador: van al 180 con las retenciones de sus facturas.</summary>
public sealed partial class InmuebleRepositorio(IConexionFactory conexiones) : IInmuebleRepositorio
{
    [GeneratedRegex("^[0-9A-Z]{20}$")] private static partial Regex RefCatastral();

    internal const string Select = """
        SELECT i.id, i.tercero_id, t.nombre AS arrendador, i.situacion, i.referencia_catastral, i.tipo_via, i.nombre_via, i.numero, i.complemento,
               i.municipio, i.codigo_municipio, i.codigo_postal
        FROM conta.inmueble i JOIN conta.tercero t ON t.id = i.tercero_id
        """;

    public async Task<IReadOnlyList<InmuebleDto>> ListarAsync(long empresaId, long? terceroId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<InmuebleDto>(new CommandDefinition(Select +
            " WHERE i.empresa_id = @empresaId AND (@terceroId::bigint IS NULL OR i.tercero_id = @terceroId) ORDER BY t.nombre, i.nombre_via, i.id",
            new { empresaId, terceroId }, cancellationToken: ct))).AsList();
    }

    public async Task<long> GuardarAsync(long empresaId, InmuebleDto i, CancellationToken ct)
    {
        if (i.Situacion is < 1 or > 4) throw new ReglaContableException("La situación del inmueble es 1, 2, 3 o 4.");
        var rc = string.IsNullOrWhiteSpace(i.ReferenciaCatastral) ? null : i.ReferenciaCatastral.Trim().ToUpperInvariant().Replace(" ", "");
        if (i.Situacion != 4 && rc is null) throw new ReglaContableException("Indica la referencia catastral o elige la situación 4 (sin referencia catastral).");
        if (i.Situacion == 4) rc = null;
        if (rc is not null && !RefCatastral().IsMatch(rc)) throw new ReglaContableException("La referencia catastral tiene 20 caracteres (letras y números).");
        var via = (i.NombreVia ?? "").Trim();
        if (via.Length is 0 or > 100) throw new ReglaContableException("Indica la dirección del inmueble (nombre de la vía, máximo 100 caracteres).");
        string? T(string? s, int max, string campo)
        {
            var v = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            if (v is { Length: var n } && n > max) throw new ReglaContableException($"{campo} admite como mucho {max} caracteres.");
            return v;
        }
        var muni = T(i.CodigoMunicipio, 5, "El código de municipio");
        if (muni is not null && !Regex.IsMatch(muni, "^[0-9]{5}$")) throw new ReglaContableException("El código INE del municipio son 5 dígitos.");
        var cp = T(i.CodigoPostal, 5, "El código postal");
        if (cp is not null && !Regex.IsMatch(cp, "^[0-9]{5}$")) throw new ReglaContableException("El código postal son 5 dígitos.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM conta.tercero WHERE id = @t AND empresa_id = @empresaId)", new { t = i.TerceroId, empresaId }, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("El arrendador no existe en la empresa.");
        var p = new { empresaId, t = i.TerceroId, sit = i.Situacion, rc, tv = T(i.TipoVia, 5, "El tipo de vía"), via, num = T(i.Numero, 10, "El número"),
                      comp = T(i.Complemento, 40, "El complemento"), mun = T(i.Municipio, 60, "El municipio"), muni, cp, id = i.Id };
        return i.Id == 0
            ? await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO conta.inmueble (empresa_id, tercero_id, situacion, referencia_catastral, tipo_via, nombre_via, numero, complemento, municipio,
                                            codigo_municipio, codigo_postal)
                VALUES (@empresaId, @t, @sit, @rc, @tv, @via, @num, @comp, @mun, @muni, @cp) RETURNING id
                """, p, cancellationToken: ct))
            : await cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
                UPDATE conta.inmueble SET tercero_id = @t, situacion = @sit, referencia_catastral = @rc, tipo_via = @tv, nombre_via = @via, numero = @num,
                       complemento = @comp, municipio = @mun, codigo_municipio = @muni, codigo_postal = @cp
                WHERE id = @id AND empresa_id = @empresaId RETURNING id
                """, p, cancellationToken: ct)) ?? throw new ReglaContableException("El inmueble no existe.");
    }

    public async Task EliminarAsync(long empresaId, long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        if (await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM fiscal.factura WHERE inmueble_id = @id)", new { id }, cancellationToken: ct)))
            throw new ReglaContableException("Hay facturas de ese inmueble: no se puede borrar.");
        if (await cn.ExecuteAsync(new CommandDefinition("DELETE FROM conta.inmueble WHERE id = @id AND empresa_id = @empresaId", new { id, empresaId }, cancellationToken: ct)) == 0)
            throw new ReglaContableException("El inmueble no existe.");
    }
}
