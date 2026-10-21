using Contabilidad.Domain;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public sealed class VigenciaDto
{
    public int Id { get; set; }
    public decimal Porcentaje { get; set; }
    public decimal Recargo { get; set; }
    public DateOnly FechaDesde { get; set; }
    public DateOnly? FechaHasta { get; set; }
}

public sealed class TipoImpuestoAdminDto
{
    public int Id { get; set; }
    public string Impuesto { get; set; } = "";
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public bool EsExento { get; set; }
    public bool Activo { get; set; }
    public List<VigenciaDto> Vigencias { get; set; } = [];
}

public sealed class TipoRetencionAdminDto
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Naturaleza { get; set; } = "";
    public string? ModeloIngreso { get; set; }
    public string? ClaveResumen { get; set; }
    public bool Activo { get; set; }
    public List<VigenciaDto> Vigencias { get; set; } = [];
}

public sealed class TipoImpuestoCmd
{
    public string Impuesto { get; set; } = "IVA";
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public bool EsExento { get; set; }
    public bool Activo { get; set; } = true;
    /// <summary>Solo al crear: primera vigencia.</summary>
    public decimal? Porcentaje { get; set; }
    public decimal Recargo { get; set; }
    public DateOnly? FechaDesde { get; set; }
}

public sealed class TipoRetencionCmd
{
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Naturaleza { get; set; } = "PROFESIONAL";
    public string? ModeloIngreso { get; set; }
    public string? ClaveResumen { get; set; }
    public bool Activo { get; set; } = true;
    public decimal? Porcentaje { get; set; }
    public DateOnly? FechaDesde { get; set; }
}

public sealed record NuevaVigenciaCmd(decimal Porcentaje, decimal Recargo, DateOnly FechaDesde);

public sealed class PaisDto
{
    public string Iso2 { get; set; } = "";
    public string Iso3 { get; set; } = "";
    public string Nombre { get; set; } = "";
    public bool EsUe { get; set; }
    public string? PrefijoVies { get; set; }
}

public sealed class BancoDto
{
    public int Id { get; set; }
    public string? CodigoEntidad { get; set; }
    public string? Bic { get; set; }
    public string Nombre { get; set; } = "";
    public string? Pais { get; set; }
    public bool Activo { get; set; } = true;
}

public sealed class EpigrafeDto
{
    public int Id { get; set; }
    public short Seccion { get; set; }
    public string Codigo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public bool EsProfesional { get; set; }
    /// <summary>Clave de actividad de la AEAT (303 y 390): A01 arrendamiento, A02 ganadería independiente, A03 empresarial, A04 artística, A05 profesional.</summary>
    public string? ClaveActividad { get; set; }
}

public sealed class ModeloFiscalAdminDto
{
    public int Id { get; set; }
    public string Administracion { get; set; } = "";
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Impuesto { get; set; } = "";
    public bool Activo { get; set; }
}

public interface ICatalogosAdminRepositorio
{
    Task<IReadOnlyList<TipoImpuestoAdminDto>> TiposImpuestoAsync(CancellationToken ct);
    Task<int> CrearTipoImpuestoAsync(TipoImpuestoCmd cmd, CancellationToken ct);
    Task ModificarTipoImpuestoAsync(int id, TipoImpuestoCmd cmd, CancellationToken ct);
    Task NuevaVigenciaImpuestoAsync(int id, NuevaVigenciaCmd cmd, CancellationToken ct);

    Task<IReadOnlyList<TipoRetencionAdminDto>> TiposRetencionAsync(CancellationToken ct);
    Task<int> CrearTipoRetencionAsync(TipoRetencionCmd cmd, CancellationToken ct);
    Task ModificarTipoRetencionAsync(int id, TipoRetencionCmd cmd, CancellationToken ct);
    Task NuevaVigenciaRetencionAsync(int id, NuevaVigenciaCmd cmd, CancellationToken ct);

    Task<IReadOnlyList<PaisDto>> PaisesAsync(CancellationToken ct);
    Task GuardarPaisAsync(PaisDto p, CancellationToken ct);
    Task<IReadOnlyList<BancoDto>> BancosAsync(CancellationToken ct);
    Task<int> GuardarBancoAsync(BancoDto b, CancellationToken ct);
    Task<IReadOnlyList<EpigrafeDto>> EpigrafesAsync(string? q, CancellationToken ct);
    Task<int> GuardarEpigrafeAsync(EpigrafeDto e, CancellationToken ct);
    Task<IReadOnlyList<ModeloFiscalAdminDto>> ModelosFiscalesAsync(CancellationToken ct);
    Task ActivarModeloFiscalAsync(int id, bool activo, CancellationToken ct);

    Task<IReadOnlyList<FormaPagoDto>> FormasPagoAsync(long empresaId, CancellationToken ct);
    Task<long> GuardarFormaPagoAsync(long empresaId, FormaPagoDto f, CancellationToken ct);
    Task EliminarFormaPagoAsync(long empresaId, long id, CancellationToken ct);
}

/// <summary>
/// Catálogos comunes a todas las empresas (tipos de IVA/IGIC y de retención con sus vigencias, países, bancos, epígrafes del IAE,
/// modelos fiscales) y formas de pago de cada empresa. Los tipos no se borran: se dan de baja o se les abre una vigencia nueva,
/// así las facturas antiguas siguen calculándose con el porcentaje de su fecha.
/// </summary>
public sealed class CatalogosAdminRepositorio(IConexionFactory conexiones) : ICatalogosAdminRepositorio
{
    private static string Texto(string? s, string campo, int max)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) throw new ReglaContableException($"{campo} es obligatorio.");
        if (t.Length > max) throw new ReglaContableException($"{campo} admite como mucho {max} caracteres.");
        return t;
    }

    private static async Task<T> UnicoAsync<T>(Func<Task<T>> f, string mensaje)
    {
        try { return await f(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new ReglaContableException(mensaje); }
    }

    // ---------------------------------------------------------------- tipos de IVA / IGIC
    public async Task<IReadOnlyList<TipoImpuestoAdminDto>> TiposImpuestoAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var tipos = (await cn.QueryAsync<TipoImpuestoAdminDto>(new CommandDefinition(
            "SELECT id, impuesto, codigo, nombre, es_exento, activo FROM cat.tipo_iva ORDER BY impuesto, codigo", cancellationToken: ct))).AsList();
        var vig = (await cn.QueryAsync<(int TipoId, int Id, decimal Porcentaje, decimal Recargo, DateOnly FechaDesde, DateOnly? FechaHasta)>(
            new CommandDefinition("SELECT tipo_iva_id, id, porcentaje, recargo_equivalencia, fecha_desde, fecha_hasta FROM cat.tipo_iva_vigencia ORDER BY fecha_desde",
                cancellationToken: ct))).ToLookup(v => v.TipoId);
        foreach (var t in tipos)
            t.Vigencias = vig[t.Id].Select(v => new VigenciaDto { Id = v.Id, Porcentaje = v.Porcentaje, Recargo = v.Recargo, FechaDesde = v.FechaDesde, FechaHasta = v.FechaHasta }).ToList();
        return tipos;
    }

    public async Task<int> CrearTipoImpuestoAsync(TipoImpuestoCmd cmd, CancellationToken ct)
    {
        var impuesto = (cmd.Impuesto ?? "").Trim().ToUpperInvariant();
        if (impuesto is not ("IVA" or "IGIC" or "IPSI")) throw new ReglaContableException("Impuesto no válido: IVA, IGIC o IPSI.");
        var codigo = Texto(cmd.Codigo, "El código", 20).ToUpperInvariant();
        var nombre = Texto(cmd.Nombre, "El nombre", 100);
        if (cmd.Porcentaje is not (>= 0 and <= 100) || cmd.FechaDesde is null)
            throw new ReglaContableException("Indica el porcentaje (0 a 100) y la fecha desde la que se aplica.");
        if (cmd.Recargo is < 0 or > 100) throw new ReglaContableException("El recargo de equivalencia va de 0 a 100.");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var id = await UnicoAsync(() => cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO cat.tipo_iva (impuesto, codigo, nombre, es_exento, activo) VALUES (@impuesto, @codigo, @nombre, @exento, true) RETURNING id
            """, new { impuesto, codigo, nombre, exento = cmd.EsExento }, tx, cancellationToken: ct)), $"Ya existe el tipo {codigo}.");
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cat.tipo_iva_vigencia (tipo_iva_id, porcentaje, recargo_equivalencia, fecha_desde) VALUES (@id, @p, @r, @desde)
            """, new { id, p = cmd.Porcentaje, r = cmd.Recargo, desde = cmd.FechaDesde }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task ModificarTipoImpuestoAsync(int id, TipoImpuestoCmd cmd, CancellationToken ct)
    {
        var nombre = Texto(cmd.Nombre, "El nombre", 100);
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("UPDATE cat.tipo_iva SET nombre = @nombre, activo = @activo, es_exento = @exento WHERE id = @id",
            new { id, nombre, activo = cmd.Activo, exento = cmd.EsExento }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("El tipo no existe.");
    }

    public Task NuevaVigenciaImpuestoAsync(int id, NuevaVigenciaCmd cmd, CancellationToken ct)
        => NuevaVigenciaAsync("cat.tipo_iva_vigencia", "tipo_iva_id", id, cmd, true, ct);

    /// <summary>Cierra la vigencia abierta en la fecha de la nueva (intervalos [desde, hasta)) y abre la nueva.</summary>
    private async Task NuevaVigenciaAsync(string tabla, string columna, int id, NuevaVigenciaCmd cmd, bool conRecargo, CancellationToken ct)
    {
        if (cmd.Porcentaje is < 0 or > 100 || cmd.Recargo is < 0 or > 100) throw new ReglaContableException("Los porcentajes van de 0 a 100.");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var abierta = await cn.QuerySingleOrDefaultAsync<(int Id, DateOnly Desde)?>(new CommandDefinition(
            $"SELECT id, fecha_desde FROM {tabla} WHERE {columna} = @id AND fecha_hasta IS NULL FOR UPDATE", new { id }, tx, cancellationToken: ct));
        var posterior = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS (SELECT 1 FROM {tabla} WHERE {columna} = @id AND fecha_desde >= @desde)", new { id, desde = cmd.FechaDesde }, tx,
            cancellationToken: ct));
        if (posterior) throw new ReglaContableException("Ya hay una vigencia que empieza en esa fecha o después.");
        if (abierta is { } a)
            await cn.ExecuteAsync(new CommandDefinition($"UPDATE {tabla} SET fecha_hasta = @desde WHERE id = @aid",
                new { desde = cmd.FechaDesde, aid = a.Id }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition(conRecargo
            ? $"INSERT INTO {tabla} ({columna}, porcentaje, recargo_equivalencia, fecha_desde) VALUES (@id, @p, @r, @desde)"
            : $"INSERT INTO {tabla} ({columna}, porcentaje, fecha_desde) VALUES (@id, @p, @desde)",
            new { id, p = cmd.Porcentaje, r = cmd.Recargo, desde = cmd.FechaDesde }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- tipos de retención
    private static readonly string[] Naturalezas = ["PROFESIONAL", "ARRENDAMIENTO", "CAPITAL", "AGRICOLA", "TRABAJO", "OTRA"];

    public async Task<IReadOnlyList<TipoRetencionAdminDto>> TiposRetencionAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var tipos = (await cn.QueryAsync<TipoRetencionAdminDto>(new CommandDefinition(
            "SELECT id, codigo, nombre, naturaleza, modelo_ingreso, clave_resumen, activo FROM cat.tipo_retencion_irpf ORDER BY codigo",
            cancellationToken: ct))).AsList();
        var vig = (await cn.QueryAsync<(int TipoId, int Id, decimal Porcentaje, DateOnly FechaDesde, DateOnly? FechaHasta)>(new CommandDefinition(
            "SELECT tipo_retencion_id, id, porcentaje, fecha_desde, fecha_hasta FROM cat.tipo_retencion_irpf_vigencia ORDER BY fecha_desde",
            cancellationToken: ct))).ToLookup(v => v.TipoId);
        foreach (var t in tipos)
            t.Vigencias = vig[t.Id].Select(v => new VigenciaDto { Id = v.Id, Porcentaje = v.Porcentaje, FechaDesde = v.FechaDesde, FechaHasta = v.FechaHasta }).ToList();
        return tipos;
    }

    private static (string Naturaleza, string? Modelo, string? Clave) ValidarRetencion(TipoRetencionCmd cmd)
    {
        var nat = (cmd.Naturaleza ?? "").Trim().ToUpperInvariant();
        if (!Naturalezas.Contains(nat)) throw new ReglaContableException($"Naturaleza no válida: {string.Join(", ", Naturalezas)}.");
        var modelo = string.IsNullOrWhiteSpace(cmd.ModeloIngreso) ? null : cmd.ModeloIngreso.Trim();
        if (modelo is not (null or "111" or "115" or "123" or "124" or "126" or "216"))
            throw new ReglaContableException("Modelo de ingreso no válido (111, 115, 123, 124, 126 o 216).");
        var clave = string.IsNullOrWhiteSpace(cmd.ClaveResumen) ? null : cmd.ClaveResumen.Trim().ToUpperInvariant();
        if (clave is { Length: > 5 }) throw new ReglaContableException("La clave del resumen anual admite como mucho 5 caracteres.");
        return (nat, modelo, clave);
    }

    public async Task<int> CrearTipoRetencionAsync(TipoRetencionCmd cmd, CancellationToken ct)
    {
        var codigo = Texto(cmd.Codigo, "El código", 20).ToUpperInvariant();
        var nombre = Texto(cmd.Nombre, "El nombre", 150);
        var (nat, modelo, clave) = ValidarRetencion(cmd);
        if (cmd.Porcentaje is not (>= 0 and <= 100) || cmd.FechaDesde is null)
            throw new ReglaContableException("Indica el porcentaje (0 a 100) y la fecha desde la que se aplica.");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var id = await UnicoAsync(() => cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO cat.tipo_retencion_irpf (codigo, nombre, naturaleza, modelo_ingreso, clave_resumen, activo)
            VALUES (@codigo, @nombre, @nat, @modelo, @clave, true) RETURNING id
            """, new { codigo, nombre, nat, modelo, clave }, tx, cancellationToken: ct)), $"Ya existe el tipo {codigo}.");
        await cn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO cat.tipo_retencion_irpf_vigencia (tipo_retencion_id, porcentaje, fecha_desde) VALUES (@id, @p, @desde)",
            new { id, p = cmd.Porcentaje, desde = cmd.FechaDesde }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task ModificarTipoRetencionAsync(int id, TipoRetencionCmd cmd, CancellationToken ct)
    {
        var nombre = Texto(cmd.Nombre, "El nombre", 150);
        var (nat, modelo, clave) = ValidarRetencion(cmd);
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE cat.tipo_retencion_irpf SET nombre = @nombre, naturaleza = @nat, modelo_ingreso = @modelo, clave_resumen = @clave, activo = @activo
            WHERE id = @id
            """, new { id, nombre, nat, modelo, clave, activo = cmd.Activo }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("El tipo no existe.");
    }

    public Task NuevaVigenciaRetencionAsync(int id, NuevaVigenciaCmd cmd, CancellationToken ct)
        => NuevaVigenciaAsync("cat.tipo_retencion_irpf_vigencia", "tipo_retencion_id", id, cmd with { Recargo = 0 }, false, ct);

    // ---------------------------------------------------------------- países, bancos, epígrafes y modelos
    public async Task<IReadOnlyList<PaisDto>> PaisesAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<PaisDto>(new CommandDefinition(
            "SELECT iso2, iso3, nombre, es_ue, prefijo_vies FROM cat.pais ORDER BY nombre", cancellationToken: ct))).AsList();
    }

    public async Task GuardarPaisAsync(PaisDto p, CancellationToken ct)
    {
        var iso2 = Texto(p.Iso2, "El código ISO de 2 letras", 2).ToUpperInvariant();
        var iso3 = Texto(p.Iso3, "El código ISO de 3 letras", 3).ToUpperInvariant();
        if (iso2.Length != 2 || iso3.Length != 3 || !iso2.All(char.IsAsciiLetter) || !iso3.All(char.IsAsciiLetter))
            throw new ReglaContableException("Los códigos ISO son de 2 y 3 letras.");
        var nombre = Texto(p.Nombre, "El nombre", 100);
        var vies = string.IsNullOrWhiteSpace(p.PrefijoVies) ? null : p.PrefijoVies.Trim().ToUpperInvariant();
        await using var cn = await conexiones.AbrirAsync(ct);
        await UnicoAsync(() => cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cat.pais (iso2, iso3, nombre, es_ue, prefijo_vies) VALUES (@iso2, @iso3, @nombre, @ue, @vies)
            ON CONFLICT (iso2) DO UPDATE SET iso3 = EXCLUDED.iso3, nombre = EXCLUDED.nombre, es_ue = EXCLUDED.es_ue, prefijo_vies = EXCLUDED.prefijo_vies
            """, new { iso2, iso3, nombre, ue = p.EsUe, vies }, cancellationToken: ct)), $"El código {iso3} ya es de otro país.");
    }

    public async Task<IReadOnlyList<BancoDto>> BancosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<BancoDto>(new CommandDefinition("""
            SELECT b.id, b.codigo_entidad, b.bic, b.nombre, p.iso2 AS pais, b.activo FROM cat.banco b LEFT JOIN cat.pais p ON p.id = b.pais_id
            ORDER BY b.nombre
            """, cancellationToken: ct))).AsList();
    }

    public async Task<int> GuardarBancoAsync(BancoDto b, CancellationToken ct)
    {
        var nombre = Texto(b.Nombre, "El nombre", 150);
        var entidad = string.IsNullOrWhiteSpace(b.CodigoEntidad) ? null : b.CodigoEntidad.Trim();
        if (entidad is not null && (entidad.Length != 4 || !entidad.All(char.IsAsciiDigit)))
            throw new ReglaContableException("El código de entidad son 4 dígitos.");
        var bic = string.IsNullOrWhiteSpace(b.Bic) ? null : b.Bic.Trim().ToUpperInvariant();
        if (bic is not null && bic.Length is not (8 or 11)) throw new ReglaContableException("El BIC tiene 8 u 11 caracteres.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var pais = string.IsNullOrWhiteSpace(b.Pais) ? "ES" : b.Pais.Trim().ToUpperInvariant();
        var paisId = await cn.ExecuteScalarAsync<short?>(new CommandDefinition("SELECT id FROM cat.pais WHERE iso2 = @pais", new { pais }, cancellationToken: ct))
                     ?? throw new ReglaContableException($"El país {pais} no está en el catálogo.");
        return await UnicoAsync(() => b.Id == 0
            ? cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT INTO cat.banco (codigo_entidad, bic, nombre, pais_id, activo) VALUES (@entidad, @bic, @nombre, @paisId, @activo) RETURNING id
                """, new { entidad, bic, nombre, paisId, activo = b.Activo }, cancellationToken: ct))
            : cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                UPDATE cat.banco SET codigo_entidad = @entidad, bic = @bic, nombre = @nombre, pais_id = @paisId, activo = @activo WHERE id = @id RETURNING id
                """, new { entidad, bic, nombre, paisId, activo = b.Activo, id = b.Id }, cancellationToken: ct)),
            $"Ya hay un banco con la entidad {entidad}.");
    }

    public async Task<IReadOnlyList<EpigrafeDto>> EpigrafesAsync(string? q, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<EpigrafeDto>(new CommandDefinition("""
            SELECT id, seccion, codigo, descripcion, es_profesional, clave_actividad FROM cat.epigrafe_iae
            WHERE @q::text IS NULL OR codigo LIKE @q || '%' OR descripcion ILIKE '%' || @q || '%'
            ORDER BY seccion, codigo LIMIT 300
            """, new { q = string.IsNullOrWhiteSpace(q) ? null : q.Trim() }, cancellationToken: ct))).AsList();
    }

    public async Task<int> GuardarEpigrafeAsync(EpigrafeDto e, CancellationToken ct)
    {
        if (e.Seccion is < 1 or > 3) throw new ReglaContableException("La sección del IAE es 1 (empresarial), 2 (profesional) o 3 (artística).");
        var codigo = Texto(e.Codigo, "El código", 10);
        var desc = Texto(e.Descripcion, "La descripción", 300);
        var clave = string.IsNullOrWhiteSpace(e.ClaveActividad) ? null : e.ClaveActividad.Trim().ToUpperInvariant();
        if (clave is not null and not ("A01" or "A02" or "A03" or "A04" or "A05"))
            throw new ReglaContableException("La clave de actividad es A01, A02, A03, A04 o A05.");
        await using var cn = await conexiones.AbrirAsync(ct);
        return await UnicoAsync(() => cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO cat.epigrafe_iae (seccion, codigo, descripcion, es_profesional, clave_actividad) VALUES (@s, @codigo, @desc, @prof, @clave)
            ON CONFLICT (seccion, codigo) DO UPDATE SET descripcion = EXCLUDED.descripcion, es_profesional = EXCLUDED.es_profesional,
                clave_actividad = EXCLUDED.clave_actividad
            RETURNING id
            """, new { s = e.Seccion, codigo, desc, prof = e.EsProfesional, clave }, cancellationToken: ct)), "El epígrafe ya existe.");
    }

    public async Task<IReadOnlyList<ModeloFiscalAdminDto>> ModelosFiscalesAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<ModeloFiscalAdminDto>(new CommandDefinition(
            "SELECT id, administracion, codigo, nombre, impuesto, activo FROM fiscal.modelo_fiscal ORDER BY administracion, codigo", cancellationToken: ct))).AsList();
    }

    public async Task ActivarModeloFiscalAsync(int id, bool activo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        if (await cn.ExecuteAsync(new CommandDefinition("UPDATE fiscal.modelo_fiscal SET activo = @activo WHERE id = @id", new { id, activo }, cancellationToken: ct)) == 0)
            throw new ReglaContableException("El modelo no existe.");
    }

    // ---------------------------------------------------------------- formas de pago de la empresa
    private static readonly string[] TiposPago = ["EFECTIVO", "TRANSFERENCIA", "RECIBO", "TARJETA", "PAGARE", "CHEQUE", "OTRO"];

    public async Task<IReadOnlyList<FormaPagoDto>> FormasPagoAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<FormaPagoDto>(new CommandDefinition("""
            SELECT id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos FROM core.forma_pago WHERE empresa_id = @empresaId ORDER BY codigo
            """, new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<long> GuardarFormaPagoAsync(long empresaId, FormaPagoDto f, CancellationToken ct)
    {
        var codigo = Texto(f.Codigo, "El código", 10).ToUpperInvariant();
        var nombre = Texto(f.Nombre, "El nombre", 100);
        var tipo = (f.Tipo ?? "").Trim().ToUpperInvariant();
        if (!TiposPago.Contains(tipo)) throw new ReglaContableException($"Tipo no válido: {string.Join(", ", TiposPago)}.");
        if (f.NumPlazos is < 1 or > 60 || f.DiasPrimerPlazo is < 0 or > 730 || f.DiasEntrePlazos is < 0 or > 365)
            throw new ReglaContableException("Plazos de 1 a 60, días hasta el primero de 0 a 730 y entre plazos de 0 a 365.");
        if (f.NumPlazos > 1 && f.DiasEntrePlazos == 0) throw new ReglaContableException("Con varios plazos indica los días entre plazos.");
        await using var cn = await conexiones.AbrirAsync(ct);
        return await UnicoAsync(async () => f.Id == 0
            ? await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.forma_pago (empresa_id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos)
                VALUES (@empresaId, @codigo, @nombre, @tipo, @n, @d1, @dn) RETURNING id
                """, new { empresaId, codigo, nombre, tipo, n = f.NumPlazos, d1 = f.DiasPrimerPlazo, dn = f.DiasEntrePlazos }, cancellationToken: ct))
            : await cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
                UPDATE core.forma_pago SET codigo = @codigo, nombre = @nombre, tipo = @tipo, num_plazos = @n, dias_primer_plazo = @d1, dias_entre_plazos = @dn
                WHERE id = @id AND empresa_id = @empresaId RETURNING id
                """, new { empresaId, codigo, nombre, tipo, n = f.NumPlazos, d1 = f.DiasPrimerPlazo, dn = f.DiasEntrePlazos, id = f.Id }, cancellationToken: ct))
              ?? throw new ReglaContableException("La forma de pago no existe en la empresa."),
            $"Ya existe la forma de pago {codigo}.");
    }

    public async Task EliminarFormaPagoAsync(long empresaId, long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        try
        {
            if (await cn.ExecuteAsync(new CommandDefinition("DELETE FROM core.forma_pago WHERE id = @id AND empresa_id = @empresaId",
                    new { id, empresaId }, cancellationToken: ct)) == 0)
                throw new ReglaContableException("La forma de pago no existe en la empresa.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new ReglaContableException("La forma de pago está en uso (terceros o vencimientos): no se puede borrar.");
        }
    }
}
