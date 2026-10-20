using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IRemesasRepositorio
{
    Task<IReadOnlyList<CuentaTerceroDto>> ListarCuentasTerceroAsync(long empresaId, long terceroId, CancellationToken ct);
    Task<CuentaTerceroDto> CrearCuentaTerceroAsync(long empresaId, long terceroId, CrearCuentaTerceroCmd cmd, CancellationToken ct);

    Task<IReadOnlyList<MandatoDto>> ListarMandatosAsync(long empresaId, long? terceroId, CancellationToken ct);
    Task<MandatoDto> CrearMandatoAsync(long empresaId, CrearMandatoCmd cmd, CancellationToken ct);
    Task DesactivarMandatoAsync(long empresaId, long mandatoId, CancellationToken ct);

    Task<IReadOnlyList<CandidatoRemesaDto>> CandidatosAsync(long empresaId, string tipo, DateOnly? hasta, CancellationToken ct);
    Task<RemesaDetalleDto> CrearAsync(long empresaId, long usuarioId, CrearRemesaCmd cmd, CancellationToken ct);
    Task<IReadOnlyList<RemesaDto>> ListarAsync(long empresaId, CancellationToken ct);
    Task<RemesaDetalleDto> DetalleAsync(long empresaId, long remesaId, CancellationToken ct);
    Task<(string Nombre, string Xml)> FicheroAsync(long empresaId, long remesaId, CancellationToken ct);
    Task AnularAsync(long empresaId, long remesaId, CancellationToken ct);
    Task<AsientoHechoDto> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, long remesaId, ContabilizarRemesaCmd cmd, CancellationToken ct);
    Task<AsientoHechoDto> DevolverAsync(long empresaId, long ejercicioId, long usuarioId, long lineaId, DevolucionReciboCmd cmd, CancellationToken ct);
}

/// <summary>
/// Remesas SEPA. Cobros: recibos domiciliados con mandato (adeudos, pain.008). Pagos: transferencias a proveedores (pain.001).
/// Al generar la remesa los vencimientos quedan REMESADOS; al contabilizarla (cuando el banco abona o carga) se liquidan en un único
/// asiento contra el banco, y un recibo devuelto vuelve a quedar pendiente con su asiento de devolución.
/// </summary>
public sealed class RemesasRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : IRemesasRepositorio
{
    private static readonly string[] Esquemas = ["CORE", "B2B"];

    private sealed class MandatoFila { public long Id { get; set; } public bool Usado { get; set; } public bool Recurrente { get; set; } public DateOnly FechaFirma { get; set; } }
    private sealed class EmpresaFila { public string Nombre { get; set; } = ""; public string Nif { get; set; } = ""; public string SepaSufijo { get; set; } = "000"; }
    private sealed class BancoFila { public long Id { get; set; } public string Iban { get; set; } = ""; public string? Bic { get; set; } public long? CuentaContableId { get; set; } public bool Activa { get; set; } }
    private sealed class RemesaFila
    {
        public long Id { get; set; }
        public string Tipo { get; set; } = "";
        public string Referencia { get; set; } = "";
        public long CuentaBancariaId { get; set; }
        public DateOnly FechaCargo { get; set; }
        public decimal Importe { get; set; }
        public string Estado { get; set; } = "";
        public string Fichero { get; set; } = "";
    }
    private sealed class LineaFila
    {
        public long Id { get; set; }
        public long RemesaId { get; set; }
        public long VencimientoId { get; set; }
        public decimal Importe { get; set; }
        public string Estado { get; set; } = "";
        public string? Secuencia { get; set; }
        public long? MandatoId { get; set; }
        public long TerceroId { get; set; }
        public string TerceroNombre { get; set; } = "";
        public long? CuentaTercero { get; set; }
        public long? CanalFactura { get; set; }
        public string? Factura { get; set; }
        public decimal VencImporte { get; set; }
        public decimal VencPendiente { get; set; }
    }

    // =====================================================================
    // Cuentas bancarias de terceros
    // =====================================================================
    public async Task<IReadOnlyList<CuentaTerceroDto>> ListarCuentasTerceroAsync(long empresaId, long terceroId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<CuentaTerceroDto>(new CommandDefinition("""
            SELECT id, tercero_id, alias, iban, bic, activa FROM tes.cuenta_bancaria
            WHERE empresa_id = @empresaId AND tercero_id = @terceroId ORDER BY activa DESC, id DESC
            """, new { empresaId, terceroId }, cancellationToken: ct))).AsList();
    }

    public async Task<CuentaTerceroDto> CrearCuentaTerceroAsync(long empresaId, long terceroId, CrearCuentaTerceroCmd cmd, CancellationToken ct)
    {
        var iban = Iban.Normalizar(cmd.Iban);
        if (!Iban.EsValido(iban)) throw new ReglaContableException("El IBAN no es válido (revisa los dígitos de control).");
        var bic = string.IsNullOrWhiteSpace(cmd.Bic) ? null : cmd.Bic.Trim().ToUpperInvariant();
        if (bic is not null && bic.Length is not (8 or 11)) throw new ReglaContableException("El BIC debe tener 8 u 11 caracteres.");
        await using var cn = await conexiones.AbrirAsync(ct);
        if (!await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM conta.tercero WHERE id = @terceroId AND empresa_id = @empresaId)", new { terceroId, empresaId }, cancellationToken: ct)))
            throw new ReglaContableException("El tercero no pertenece a la empresa.");
        try
        {
            return await cn.QuerySingleAsync<CuentaTerceroDto>(new CommandDefinition("""
                INSERT INTO tes.cuenta_bancaria (empresa_id, tercero_id, alias, iban, bic) VALUES (@empresaId, @terceroId, @alias, @iban, @bic)
                RETURNING id, tercero_id, alias, iban, bic, activa
                """, new { empresaId, terceroId, alias = string.IsNullOrWhiteSpace(cmd.Alias) ? null : cmd.Alias.Trim(), iban, bic }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Ese IBAN ya está dado de alta en la empresa.");
        }
    }

    // =====================================================================
    // Mandatos
    // =====================================================================
    private const string SqlMandatos = """
        SELECT m.id, m.tercero_id, t.nombre AS tercero, m.cuenta_bancaria_id, b.iban, m.referencia, m.fecha_firma, m.esquema, m.recurrente, m.usado, m.activo
        FROM tes.mandato m JOIN conta.tercero t ON t.id = m.tercero_id JOIN tes.cuenta_bancaria b ON b.id = m.cuenta_bancaria_id
        """;

    public async Task<IReadOnlyList<MandatoDto>> ListarMandatosAsync(long empresaId, long? terceroId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<MandatoDto>(new CommandDefinition(SqlMandatos + """
             WHERE m.empresa_id = @empresaId AND (@terceroId::bigint IS NULL OR m.tercero_id = @terceroId) ORDER BY t.nombre, m.activo DESC, m.fecha_firma DESC
            """, new { empresaId, terceroId }, cancellationToken: ct))).AsList();
    }

    public async Task<MandatoDto> CrearMandatoAsync(long empresaId, CrearMandatoCmd cmd, CancellationToken ct)
    {
        if (!Esquemas.Contains(cmd.Esquema)) throw new ReglaContableException("Esquema no válido: CORE o B2B.");
        if (cmd.FechaFirma > DateOnly.FromDateTime(DateTime.Today)) throw new ReglaContableException("La fecha de firma no puede ser futura.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var cuentaOk = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM tes.cuenta_bancaria WHERE id = @c AND empresa_id = @empresaId AND tercero_id = @t AND activa)
            """, new { c = cmd.CuentaBancariaId, empresaId, t = cmd.TerceroId }, cancellationToken: ct));
        if (!cuentaOk) throw new ReglaContableException("La cuenta bancaria no es de ese cliente o está desactivada.");

        var referencia = Sepa.Texto(cmd.Referencia, 35);
        if (referencia.Length == 0)
        {
            var n = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) + 1 FROM tes.mandato WHERE empresa_id = @empresaId AND tercero_id = @t", new { empresaId, t = cmd.TerceroId }, cancellationToken: ct));
            referencia = $"MND-{cmd.TerceroId:000000}-{n:00}";
        }
        try
        {
            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO tes.mandato (empresa_id, tercero_id, cuenta_bancaria_id, referencia, fecha_firma, esquema, recurrente)
                VALUES (@empresaId, @t, @c, @referencia, @firma, @esquema, @recurrente) RETURNING id
                """, new { empresaId, t = cmd.TerceroId, c = cmd.CuentaBancariaId, referencia, firma = cmd.FechaFirma, esquema = cmd.Esquema,
                           recurrente = cmd.Recurrente }, cancellationToken: ct));
            return await cn.QuerySingleAsync<MandatoDto>(new CommandDefinition(SqlMandatos + " WHERE m.id = @id", new { id }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe un mandato con la referencia {referencia}.");
        }
    }

    public async Task DesactivarMandatoAsync(long empresaId, long mandatoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.mandato SET activo = false WHERE id = @mandatoId AND empresa_id = @empresaId AND activo",
            new { mandatoId, empresaId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("El mandato no existe o ya está desactivado.");
    }

    // =====================================================================
    // Remesas
    // =====================================================================
    private static string TipoValido(string? tipo)
        => (tipo ?? "").Trim().ToUpperInvariant() is var t && t is "COBRO" or "PAGO" ? t : throw new ReglaContableException("Tipo no válido: COBRO o PAGO.");

    public async Task<IReadOnlyList<CandidatoRemesaDto>> CandidatosAsync(long empresaId, string tipo, DateOnly? hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await CandidatosAsync(cn, null, empresaId, TipoValido(tipo), hasta, null, ct);
    }

    /// <summary>Cobros: el mandato activo más reciente del cliente da el IBAN. Pagos: la cuenta activa más reciente del proveedor.</summary>
    private static async Task<List<CandidatoRemesaDto>> CandidatosAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, string tipo, DateOnly? hasta,
        long[]? ids, CancellationToken ct)
    {
        var filas = (await cn.QueryAsync<CandidatoRemesaDto>(new CommandDefinition($"""
            SELECT v.id AS vencimiento_id, v.tercero_id, t.nombre AS tercero, COALESCE(f.serie, '') || f.numero AS factura, v.fecha_vencimiento,
                   v.importe_pendiente, fp.nombre AS forma_pago,
                   {(tipo == "COBRO"
                       ? "m.iban, m.id AS mandato_id, m.referencia AS mandato, m.esquema"
                       : "(SELECT b.iban FROM tes.cuenta_bancaria b WHERE b.tercero_id = v.tercero_id AND b.activa ORDER BY b.id DESC LIMIT 1) AS iban")}
            FROM tes.vencimiento v
            JOIN conta.tercero t ON t.id = v.tercero_id
            LEFT JOIN fiscal.factura f ON f.id = v.factura_id
            LEFT JOIN core.forma_pago fp ON fp.id = v.forma_pago_id
            {(tipo == "COBRO" ? """
            LEFT JOIN LATERAL (SELECT mm.id, mm.referencia, mm.esquema, b.iban FROM tes.mandato mm JOIN tes.cuenta_bancaria b ON b.id = mm.cuenta_bancaria_id
                               WHERE mm.tercero_id = v.tercero_id AND mm.activo AND b.activa ORDER BY mm.fecha_firma DESC, mm.id DESC LIMIT 1) m ON true
            """ : "")}
            WHERE v.empresa_id = @empresaId AND v.tipo = @tipo AND v.estado IN ('PENDIENTE','PARCIAL') AND v.importe_pendiente > 0
              AND (@hasta::date IS NULL OR v.fecha_vencimiento <= @hasta) AND (@ids::bigint[] IS NULL OR v.id = ANY(@ids))
            ORDER BY v.fecha_vencimiento, t.nombre, v.id
            {(tx is null ? "" : "FOR UPDATE OF v")}
            """, new { empresaId, tipo, hasta, ids }, tx, cancellationToken: ct))).AsList();
        foreach (var c in filas)
            c.Problema = c.Iban is null ? (tipo == "COBRO" ? "El cliente no tiene mandato de domiciliación activo." : "El proveedor no tiene cuenta bancaria.") : null;
        return filas;
    }

    public async Task<RemesaDetalleDto> CrearAsync(long empresaId, long usuarioId, CrearRemesaCmd cmd, CancellationToken ct)
    {
        var tipo = TipoValido(cmd.Tipo);
        var ids = cmd.VencimientoIds.Distinct().ToArray();
        if (ids.Length == 0) throw new ReglaContableException("Elige los vencimientos de la remesa.");
        if (cmd.FechaCargo < DateOnly.FromDateTime(DateTime.Today)) throw new ReglaContableException("La fecha de cargo no puede ser anterior a hoy.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))", new { clave = $"remesa|{empresaId}" }, tx, cancellationToken: ct));

        var empresa = await cn.QuerySingleAsync<EmpresaFila>(new CommandDefinition("""
            SELECT e.nombre, n.numero AS nif, e.sepa_sufijo FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id WHERE e.id = @empresaId
            """, new { empresaId }, tx, cancellationToken: ct));
        var banco = await cn.QuerySingleOrDefaultAsync<BancoFila>(new CommandDefinition(
            "SELECT id, iban, bic, cuenta_contable_id, activa FROM tes.cuenta_bancaria WHERE id = @id AND empresa_id = @empresaId AND tercero_id IS NULL",
            new { id = cmd.CuentaBancariaId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La cuenta bancaria no pertenece a la empresa.");
        if (!banco.Activa) throw new ReglaContableException("La cuenta bancaria está desactivada.");

        var candidatos = await CandidatosAsync(cn, tx, empresaId, tipo, null, ids, ct);
        if (candidatos.Count != ids.Length) throw new ReglaContableException("Algún vencimiento no existe, no es de este tipo o ya no está pendiente.");
        var conProblema = candidatos.Where(c => c.Problema is not null).ToList();
        if (conProblema.Count > 0) throw new ReglaContableException($"{conProblema[0].Tercero}: {conProblema[0].Problema}");
        var esquemas = candidatos.Select(c => c.Esquema).Distinct().ToList();
        if (tipo == "COBRO" && esquemas.Count > 1) throw new ReglaContableException("No se pueden mezclar mandatos CORE y B2B en la misma remesa.");

        var hoy = DateTime.Now;
        var n = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) + 1 FROM tes.remesa WHERE empresa_id = @empresaId AND creado_en::date = current_date", new { empresaId }, tx, cancellationToken: ct));
        var referencia = $"{(tipo == "COBRO" ? "REC" : "TRF")}-{hoy:yyyyMMdd}-{n:000}";

        var mandatos = tipo == "COBRO"
            ? (await cn.QueryAsync<MandatoFila>(new CommandDefinition(
                "SELECT id, usado, recurrente, fecha_firma FROM tes.mandato WHERE id = ANY(@ids) FOR UPDATE",
                new { ids = candidatos.Select(c => c.MandatoId!.Value).Distinct().ToArray() }, tx, cancellationToken: ct))).ToDictionary(m => m.Id)
            : [];
        var bics = (await cn.QueryAsync<(string Iban, string? Bic)>(new CommandDefinition(
            "SELECT iban, bic FROM tes.cuenta_bancaria WHERE empresa_id = @empresaId AND iban = ANY(@ibanes)",
            new { empresaId, ibanes = candidatos.Select(c => c.Iban!).Distinct().ToArray() }, tx, cancellationToken: ct))).ToDictionary(x => x.Iban, x => x.Bic);

        // Secuencia del adeudo: único (OOFF), primero (FRST) o recurrente (RCUR). Varios recibos del mismo mandato nuevo: el primero es FRST
        var primeros = new HashSet<long>();
        var lineas = candidatos.Select((c, i) =>
        {
            string secuencia = "OOFF";
            if (tipo == "COBRO")
            {
                var m = mandatos[c.MandatoId!.Value];
                secuencia = !m.Recurrente ? "OOFF" : !m.Usado && primeros.Add(m.Id) ? "FRST" : "RCUR";
            }
            var concepto = c.Factura is { Length: > 0 } ? $"Factura {c.Factura}" : $"Vencimiento {c.FechaVencimiento:dd/MM/yyyy}";
            return (Candidato: c, Op: new OperacionSepa($"{referencia}-{i + 1:0000}", c.ImportePendiente, c.Tercero, c.Iban!, bics.GetValueOrDefault(c.Iban!),
                concepto, c.Mandato, tipo == "COBRO" ? mandatos[c.MandatoId!.Value].FechaFirma : null, secuencia));
        }).ToList();

        var ordenante = new OrdenanteSepa(empresa.Nombre, empresa.Nif, empresa.SepaSufijo, banco.Iban, banco.Bic);
        var ops = lineas.Select(l => l.Op).ToList();
        var xml = tipo == "COBRO"
            ? Sepa.Adeudos(ordenante, referencia, hoy, cmd.FechaCargo, esquemas[0] ?? "CORE", ops)
            : Sepa.Transferencias(ordenante, referencia, hoy, cmd.FechaCargo, ops);

        var remesaId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO tes.remesa (empresa_id, tipo, cuenta_bancaria_id, referencia, fecha_cargo, importe, num_operaciones, fichero, creado_por)
            VALUES (@empresaId, @tipo, @banco, @referencia, @fecha, @importe, @n, @xml, @usuarioId) RETURNING id
            """, new { empresaId, tipo, banco = banco.Id, referencia, fecha = cmd.FechaCargo, importe = ops.Sum(o => o.Importe), n = ops.Count, xml, usuarioId },
            tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tes.remesa_vencimiento (remesa_id, vencimiento_id, importe, mandato_id, secuencia, iban, end_to_end_id)
            VALUES (@remesaId, @vencimientoId, @importe, @mandatoId, @secuencia, @iban, @e2e)
            """, lineas.Select(l => new { remesaId, vencimientoId = l.Candidato.VencimientoId, importe = l.Op.Importe, mandatoId = l.Candidato.MandatoId,
                                           secuencia = tipo == "COBRO" ? l.Op.Secuencia : null, iban = l.Op.Iban, e2e = l.Op.EndToEndId }), tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE tes.vencimiento SET estado = 'REMESADO', cuenta_bancaria_id = @banco WHERE id = ANY(@ids)", new { banco = banco.Id, ids }, tx, cancellationToken: ct));
        if (primeros.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.mandato SET usado = true WHERE id = ANY(@ids)", new { ids = primeros.ToArray() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return await DetalleAsync(empresaId, remesaId, ct);
    }

    private const string SqlRemesas = """
        SELECT r.id, r.tipo, r.referencia, r.cuenta_bancaria_id, COALESCE(NULLIF(b.alias, ''), b.iban) AS cuenta_bancaria, r.fecha_cargo, r.importe,
               r.num_operaciones, r.estado, s.numero AS asiento_numero, r.creado_en,
               (SELECT count(*) FROM tes.remesa_vencimiento rv WHERE rv.remesa_id = r.id AND rv.estado = 'DEVUELTO')::int AS devueltos
        FROM tes.remesa r JOIN tes.cuenta_bancaria b ON b.id = r.cuenta_bancaria_id LEFT JOIN conta.asiento s ON s.id = r.asiento_id
        """;

    public async Task<IReadOnlyList<RemesaDto>> ListarAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<RemesaDto>(new CommandDefinition(SqlRemesas + " WHERE r.empresa_id = @empresaId ORDER BY r.creado_en DESC, r.id DESC LIMIT 100",
            new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<RemesaDetalleDto> DetalleAsync(long empresaId, long remesaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var r = await cn.QuerySingleOrDefaultAsync<RemesaDto>(new CommandDefinition(SqlRemesas + " WHERE r.id = @remesaId AND r.empresa_id = @empresaId",
            new { remesaId, empresaId }, cancellationToken: ct)) ?? throw new ReglaContableException("La remesa no existe.");
        var lineas = (await cn.QueryAsync<RemesaLineaDto>(new CommandDefinition("""
            SELECT rv.id, rv.vencimiento_id, t.nombre AS tercero, COALESCE(f.serie, '') || f.numero AS factura, rv.importe, rv.iban, rv.secuencia,
                   m.referencia AS mandato, rv.end_to_end_id, rv.estado, rv.fecha_devolucion, rv.motivo_devolucion
            FROM tes.remesa_vencimiento rv
            JOIN tes.vencimiento v ON v.id = rv.vencimiento_id
            JOIN conta.tercero t ON t.id = v.tercero_id
            LEFT JOIN fiscal.factura f ON f.id = v.factura_id
            LEFT JOIN tes.mandato m ON m.id = rv.mandato_id
            WHERE rv.remesa_id = @remesaId ORDER BY rv.id
            """, new { remesaId }, cancellationToken: ct))).AsList();
        return new RemesaDetalleDto(r, lineas);
    }

    public async Task<(string Nombre, string Xml)> FicheroAsync(long empresaId, long remesaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var r = await cn.QuerySingleOrDefaultAsync<RemesaFila>(new CommandDefinition(
            "SELECT id, tipo, referencia, estado, fichero FROM tes.remesa WHERE id = @remesaId AND empresa_id = @empresaId", new { remesaId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("La remesa no existe.");
        if (r.Estado == "ANULADA") throw new ReglaContableException("La remesa está anulada.");
        return ($"{r.Referencia}.xml", r.Fichero);
    }

    public async Task AnularAsync(long empresaId, long remesaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var r = await RemesaAsync(cn, tx, empresaId, remesaId, ct);
        if (r.Estado != "GENERADA") throw new ReglaContableException("Solo se anulan remesas generadas y sin contabilizar.");
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE tes.vencimiento v SET estado = CASE WHEN v.importe_pendiente < v.importe THEN 'PARCIAL' ELSE 'PENDIENTE' END
            FROM tes.remesa_vencimiento rv WHERE rv.remesa_id = @remesaId AND v.id = rv.vencimiento_id AND v.estado = 'REMESADO'
            """, new { remesaId }, tx, cancellationToken: ct));
        // Si esta remesa llevaba el primer adeudo de un mandato, el mandato vuelve a estar sin usar
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE tes.mandato SET usado = false WHERE id IN (SELECT mandato_id FROM tes.remesa_vencimiento WHERE remesa_id = @remesaId AND secuencia = 'FRST')
            """, new { remesaId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.remesa SET estado = 'ANULADA' WHERE id = @remesaId", new { remesaId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // =====================================================================
    // Contabilización y devoluciones
    // =====================================================================
    private static readonly string SqlLineas = """
        SELECT rv.id, rv.remesa_id, rv.vencimiento_id, rv.importe, rv.estado, rv.secuencia, rv.mandato_id, v.tercero_id, t.nombre AS tercero_nombre,
               CASE v.tipo WHEN 'COBRO' THEN t.cuenta_cliente_id ELSE t.cuenta_proveedor_id END AS cuenta_tercero,
               f.canal_id AS canal_factura, COALESCE(f.serie, '') || f.numero AS factura, v.importe AS venc_importe, v.importe_pendiente AS venc_pendiente
        FROM tes.remesa_vencimiento rv
        JOIN tes.vencimiento v ON v.id = rv.vencimiento_id
        JOIN conta.tercero t ON t.id = v.tercero_id
        LEFT JOIN fiscal.factura f ON f.id = v.factura_id
        """;

    public async Task<AsientoHechoDto> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, long remesaId, ContabilizarRemesaCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var r = await RemesaAsync(cn, tx, empresaId, remesaId, ct);
        if (r.Estado != "GENERADA") throw new ReglaContableException(r.Estado == "CONTABILIZADA" ? "La remesa ya está contabilizada." : "La remesa está anulada.");
        var banco = await cn.QuerySingleAsync<BancoFila>(new CommandDefinition(
            "SELECT id, iban, bic, cuenta_contable_id, activa FROM tes.cuenta_bancaria WHERE id = @id", new { id = r.CuentaBancariaId }, tx, cancellationToken: ct));
        if (banco.CuentaContableId is null) throw new ReglaContableException("La cuenta bancaria no tiene cuenta contable (572…).");

        var lineas = (await cn.QueryAsync<LineaFila>(new CommandDefinition(SqlLineas + " WHERE rv.remesa_id = @remesaId ORDER BY rv.id FOR UPDATE OF v",
            new { remesaId }, tx, cancellationToken: ct))).ToList();
        var sinCuenta = lineas.FirstOrDefault(l => l.CuentaTercero is null);
        if (sinCuenta is not null) throw new ReglaContableException($"{sinCuenta.TerceroNombre} no tiene subcuenta de {(r.Tipo == "COBRO" ? "cliente" : "proveedor")}.");
        var excede = lineas.FirstOrDefault(l => l.Importe > l.VencPendiente);
        if (excede is not null) throw new ReglaContableException($"El vencimiento de {excede.TerceroNombre} ya no tiene pendiente el importe remesado.");
        long Canal(LineaFila l) => l.CanalFactura ?? cmd.CanalId
            ?? throw new ReglaContableException($"Indica el canal: el vencimiento de {l.TerceroNombre} no viene de una factura con canal.");

        // Cobros: banco (D) por canal a clientes (H). Pagos: proveedores (D) a banco (H) por canal
        var cobro = r.Tipo == "COBRO";
        var concepto = $"Remesa de {(cobro ? "recibos" : "transferencias")} {r.Referencia} ({lineas.Count})";
        var apuntes = lineas.Select(l => new ApunteCmd
        {
            CuentaId = l.CuentaTercero!.Value, TerceroId = l.TerceroId, CanalId = Canal(l),
            Debe = cobro ? 0 : l.Importe, Haber = cobro ? l.Importe : 0, Concepto = $"{concepto} · {l.TerceroNombre} {l.Factura}".Trim()
        }).ToList();
        apuntes.AddRange(lineas.GroupBy(Canal).Select(g => new ApunteCmd
        {
            CuentaId = banco.CuentaContableId.Value, CanalId = g.Key, Debe = cobro ? g.Sum(l => l.Importe) : 0, Haber = cobro ? 0 : g.Sum(l => l.Importe), Concepto = concepto
        }));
        var diarioId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'BANCOS' ORDER BY id LIMIT 1", new { empresaId }, tx, cancellationToken: ct))
            ?? await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct);
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
            new CrearAsientoCmd { Fecha = cmd.Fecha, DiarioId = diarioId, Concepto = concepto, Apuntes = apuntes }, "TESORERIA", null, r.Referencia, ct);

        var movimientoId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO tes.movimiento_bancario (empresa_id, cuenta_bancaria_id, fecha, importe, concepto, referencia, origen, asiento_id)
            VALUES (@empresaId, @banco, @fecha, @importe, @concepto, @referencia, 'MANUAL', @asientoId) RETURNING id
            """, new { empresaId, banco = banco.Id, fecha = cmd.Fecha, importe = cobro ? r.Importe : -r.Importe, concepto, referencia = r.Referencia, asientoId = a.Id },
            tx, cancellationToken: ct));
        foreach (var l in lineas)
        {
            var cobroPagoId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO tes.cobro_pago (empresa_id, vencimiento_id, movimiento_id, fecha, importe, asiento_id, creado_por)
                VALUES (@empresaId, @v, @movimientoId, @fecha, @importe, @asientoId, @usuarioId) RETURNING id
                """, new { empresaId, v = l.VencimientoId, movimientoId, fecha = cmd.Fecha, importe = l.Importe, asientoId = a.Id, usuarioId }, tx, cancellationToken: ct));
            var pendiente = l.VencPendiente - l.Importe;
            await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.vencimiento SET importe_pendiente = @pendiente, estado = @estado WHERE id = @v",
                new { pendiente, estado = CalculadorVencimientos.EstadoTras(l.VencImporte, pendiente), v = l.VencimientoId }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.remesa_vencimiento SET cobro_pago_id = @cobroPagoId WHERE id = @id",
                new { cobroPagoId, id = l.Id }, tx, cancellationToken: ct));
        }
        await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.remesa SET estado = 'CONTABILIZADA', asiento_id = @asientoId WHERE id = @remesaId",
            new { asientoId = a.Id, remesaId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new AsientoHechoDto(a.Id, a.Numero);
    }

    public async Task<AsientoHechoDto> DevolverAsync(long empresaId, long ejercicioId, long usuarioId, long lineaId, DevolucionReciboCmd cmd, CancellationToken ct)
    {
        if (cmd.Gastos < 0) throw new ReglaContableException("Los gastos no pueden ser negativos.");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var l = await cn.QuerySingleOrDefaultAsync<LineaFila>(new CommandDefinition(SqlLineas + """
             JOIN tes.remesa r ON r.id = rv.remesa_id WHERE rv.id = @lineaId AND r.empresa_id = @empresaId FOR UPDATE OF rv, v
            """, new { lineaId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El recibo no existe.");
        var r = await RemesaAsync(cn, tx, empresaId, l.RemesaId, ct);
        if (r.Tipo != "COBRO") throw new ReglaContableException("Solo se devuelven recibos de remesas de cobro.");
        if (r.Estado != "CONTABILIZADA") throw new ReglaContableException("Contabiliza antes la remesa.");
        if (l.Estado == "DEVUELTO") throw new ReglaContableException("El recibo ya está devuelto.");
        var banco = await cn.QuerySingleAsync<BancoFila>(new CommandDefinition(
            "SELECT id, iban, bic, cuenta_contable_id, activa FROM tes.cuenta_bancaria WHERE id = @id", new { id = r.CuentaBancariaId }, tx, cancellationToken: ct));
        var canal = l.CanalFactura ?? cmd.CanalId ?? throw new ReglaContableException("Indica el canal: el recibo no viene de una factura con canal.");

        // Cliente (D) a banco (H) por el recibo; gastos de devolución (626) a banco
        var motivo = string.IsNullOrWhiteSpace(cmd.Motivo) ? null : cmd.Motivo.Trim()[..Math.Min(cmd.Motivo.Trim().Length, 100)];
        var concepto = $"Devolución recibo {l.Factura} {l.TerceroNombre}{(motivo is null ? "" : " · " + motivo)}".Trim();
        if (concepto.Length > 250) concepto = concepto[..250];
        var apuntes = new List<ApunteCmd>
        {
            new() { CuentaId = l.CuentaTercero!.Value, TerceroId = l.TerceroId, CanalId = canal, Debe = l.Importe, Concepto = concepto },
            new() { CuentaId = banco.CuentaContableId!.Value, CanalId = canal, Haber = l.Importe + cmd.Gastos, Concepto = concepto }
        };
        if (cmd.Gastos > 0)
        {
            var cuentaGastos = cmd.CuentaGastosId ?? await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "GASTOS_BANCARIOS", "626", ct)
                ?? throw new ReglaContableException("Indica la cuenta de los gastos de devolución (626…).");
            apuntes.Insert(1, new ApunteCmd { CuentaId = cuentaGastos, CanalId = canal, Debe = cmd.Gastos, Concepto = concepto });
        }
        var diarioId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'BANCOS' ORDER BY id LIMIT 1", new { empresaId }, tx, cancellationToken: ct))
            ?? await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct);
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
            new CrearAsientoCmd { Fecha = cmd.Fecha, DiarioId = diarioId, Concepto = concepto, Apuntes = apuntes }, "TESORERIA", null, r.Referencia, ct);

        var pendiente = l.VencPendiente + l.Importe;
        await cn.ExecuteAsync(new CommandDefinition("UPDATE tes.vencimiento SET importe_pendiente = @pendiente, estado = @estado WHERE id = @v",
            new { pendiente, estado = CalculadorVencimientos.EstadoTras(l.VencImporte, pendiente), v = l.VencimientoId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE tes.remesa_vencimiento SET estado = 'DEVUELTO', fecha_devolucion = @fecha, motivo_devolucion = @motivo, asiento_devolucion_id = @asientoId WHERE id = @id
            """, new { fecha = cmd.Fecha, motivo, asientoId = a.Id, id = l.Id }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tes.movimiento_bancario (empresa_id, cuenta_bancaria_id, fecha, importe, concepto, referencia, origen, asiento_id)
            VALUES (@empresaId, @banco, @fecha, @importe, @concepto, @referencia, 'MANUAL', @asientoId)
            """, new { empresaId, banco = banco.Id, fecha = cmd.Fecha, importe = -(l.Importe + cmd.Gastos), concepto, referencia = r.Referencia, asientoId = a.Id },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new AsientoHechoDto(a.Id, a.Numero);
    }

    private static async Task<RemesaFila> RemesaAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long remesaId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<RemesaFila>(new CommandDefinition("""
               SELECT id, tipo, referencia, cuenta_bancaria_id, fecha_cargo, importe, estado, fichero FROM tes.remesa
               WHERE id = @remesaId AND empresa_id = @empresaId FOR UPDATE
               """, new { remesaId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("La remesa no existe.");
}
