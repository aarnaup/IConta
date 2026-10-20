using Contabilidad.Domain;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ITerceroRepositorio
{
    Task<IReadOnlyList<TerceroDto>> ListarAsync(long empresaId, string? tipo, string? q, CancellationToken ct);
    Task<TerceroDto> CrearAsync(long empresaId, CrearTerceroCmd cmd, CancellationToken ct);
    Task MarcarCriterioCajaAsync(long empresaId, long terceroId, bool criterioCaja, CancellationToken ct);
}

public sealed class TerceroRepositorio(IConexionFactory conexiones) : ITerceroRepositorio
{
    private sealed class Subcuenta { public long Id { get; set; } public string Codigo { get; set; } = ""; }

    public async Task<IReadOnlyList<TerceroDto>> ListarAsync(long empresaId, string? tipo, string? q, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT t.id, t.codigo, t.nombre, n.numero AS nif, t.es_cliente, t.es_proveedor,
                   t.recargo_equivalencia, t.criterio_caja, t.tipo_retencion_defecto_id, t.tipo_iva_defecto_id
            FROM conta.tercero t JOIN core.nif n ON n.id = t.nif_id
            WHERE t.empresa_id = @empresaId AND t.activo
              AND (@tipo::text IS NULL
                   OR (@tipo = 'CLIENTE' AND t.es_cliente)
                   OR (@tipo = 'PROVEEDOR' AND (t.es_proveedor OR t.es_acreedor)))
              AND (@q::text IS NULL OR t.nombre ILIKE '%' || @q || '%' OR n.numero ILIKE @q || '%')
            ORDER BY t.nombre LIMIT 500
            """;
        return (await cn.QueryAsync<TerceroDto>(new CommandDefinition(sql, new { empresaId, tipo, q }, cancellationToken: ct))).AsList();
    }

    /// <summary>Proveedor acogido (o no) al criterio de caja; afecta a las facturas que se contabilicen desde ahora.</summary>
    public async Task MarcarCriterioCajaAsync(long empresaId, long terceroId, bool criterioCaja, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE conta.tercero SET criterio_caja = @criterioCaja WHERE id = @terceroId AND empresa_id = @empresaId",
            new { criterioCaja, terceroId, empresaId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("El tercero no existe en la empresa.");
    }

    /// <summary>Alta de cliente/proveedor: reutiliza el NIF si ya existe y crea la subcuenta (4300... / 4000...).</summary>
    public async Task<TerceroDto> CrearAsync(long empresaId, CrearTerceroCmd cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Nif) || string.IsNullOrWhiteSpace(cmd.Nombre))
            throw new ReglaContableException("NIF y nombre son obligatorios.");
        if (!cmd.EsCliente && !cmd.EsProveedor)
            throw new ReglaContableException("Indica si es cliente, proveedor o ambos.");

        var nif = cmd.Nif.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var digitos = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT digitos_cuenta FROM core.empresa WHERE id = @empresaId", new { empresaId }, tx, cancellationToken: ct));

        var nifId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO core.nif (pais_id, tipo_documento, numero, razon_social)
            SELECT id, @tipo, @numero, @nombre FROM cat.pais WHERE iso2 = 'ES'
            ON CONFLICT (pais_id, numero) DO UPDATE SET razon_social = core.nif.razon_social
            RETURNING id
            """, new { tipo = cmd.TipoDocumento, numero = nif, nombre = cmd.Nombre.Trim() }, tx, cancellationToken: ct));

        var cli = cmd.EsCliente ? await SiguienteSubcuentaAsync(cn, tx, empresaId, "4300", digitos, $"Cliente {cmd.Nombre.Trim()}", "DEUDORA", ct) : null;
        var pro = cmd.EsProveedor ? await SiguienteSubcuentaAsync(cn, tx, empresaId, "4000", digitos, $"Proveedor {cmd.Nombre.Trim()}", "ACREEDORA", ct) : null;
        var codigo = (cli ?? pro)!.Codigo;

        long id;
        try
        {
            id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO conta.tercero (empresa_id, nif_id, codigo, nombre, es_cliente, es_proveedor,
                                           cuenta_cliente_id, cuenta_proveedor_id, recargo_equivalencia, criterio_caja,
                                           tipo_retencion_defecto_id, tipo_iva_defecto_id)
                VALUES (@empresaId, @nifId, @codigo, @nombre, @esCliente, @esProveedor,
                        @cuentaCliente, @cuentaProveedor, @recargo, @criterioCaja, @tipoRet, @tipoIva)
                RETURNING id
                """, new
            {
                empresaId, nifId, codigo, nombre = cmd.Nombre.Trim(), esCliente = cmd.EsCliente, esProveedor = cmd.EsProveedor,
                cuentaCliente = cli?.Id, cuentaProveedor = pro?.Id, recargo = cmd.RecargoEquivalencia, criterioCaja = cmd.CriterioCaja,
                tipoRet = cmd.TipoRetencionDefectoId, tipoIva = cmd.TipoIvaDefectoId
            }, tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Ya existe un tercero con ese NIF o código en la empresa.");
        }

        await tx.CommitAsync(ct);
        return new TerceroDto
        {
            Id = id, Codigo = codigo, Nombre = cmd.Nombre.Trim(), Nif = nif, EsCliente = cmd.EsCliente,
            EsProveedor = cmd.EsProveedor, RecargoEquivalencia = cmd.RecargoEquivalencia,
            TipoRetencionDefectoId = cmd.TipoRetencionDefectoId, TipoIvaDefectoId = cmd.TipoIvaDefectoId
        };
    }

    /// <summary>
    /// Siguiente subcuenta libre dentro de <paramref name="prefijo"/> (4300 clientes en euros, 4000 proveedores en euros).
    /// Se numera dentro de la subcuenta de 4 dígitos para no invadir otras del PGC, como la 4309 (facturas pendientes de formalizar).
    /// </summary>
    private static async Task<Subcuenta> SiguienteSubcuentaAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId,
        string prefijo, int digitos, string nombre, string naturaleza, CancellationToken ct)
    {
        var max = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COALESCE(MAX(codigo::bigint), 0) FROM conta.cuenta
            WHERE empresa_id = @empresaId AND codigo LIKE @prefijo || '%' AND length(codigo) = @digitos AND codigo ~ '^[0-9]+$'
            """, new { empresaId, prefijo, digitos }, tx, cancellationToken: ct));

        var siguiente = max == 0 ? long.Parse(prefijo.PadRight(digitos, '0')) + 1 : max + 1;
        var codigo = siguiente.ToString();
        if (!codigo.StartsWith(prefijo, StringComparison.Ordinal) || codigo.Length != digitos)
            throw new ReglaContableException($"No quedan subcuentas libres en la {prefijo}.",
                "Aumenta los dígitos de cuenta de la empresa o crea la subcuenta a mano.");

        // Cuelga de la cuenta de 3 dígitos (430 / 400) si la empresa tiene el plan cargado
        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, padre_id, naturaleza, es_imputable, requiere_tercero)
            VALUES (@empresaId, @codigo, @nombre, 4,
                    (SELECT id FROM conta.cuenta WHERE empresa_id = @empresaId AND codigo = @padre),
                    @naturaleza, true, true)
            RETURNING id
            """, new { empresaId, codigo, nombre, padre = prefijo[..3], naturaleza }, tx, cancellationToken: ct));
        return new Subcuenta { Id = id, Codigo = codigo };
    }
}
