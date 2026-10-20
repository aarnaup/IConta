using Contabilidad.Domain;
using Contabilidad.Domain.Canales;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IAsientoRepositorio
{
    Task<AsientoCreado> CrearAsync(long empresaId, long ejercicioId, long usuarioId, CrearAsientoCmd cmd, CancellationToken ct);

    /// <summary>
    /// Crea el asiento dentro de una transacción existente (p. ej. al contabilizar una factura).
    /// <paramref name="tipoPeriodo"/>: NORMAL para el trabajo diario; APERTURA, REGULARIZACION o CIERRE para los asientos de fin de ejercicio.
    /// </summary>
    Task<AsientoCreado> CrearEnTransaccionAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long ejercicioId,
        long? usuarioId, CrearAsientoCmd cmd, string origen, long? plantillaId, string? documento, CancellationToken ct,
        string tipoPeriodo = "NORMAL", long? asientoOrigenId = null);

    Task<IReadOnlyList<FilaAsientoDto>> ListarAsync(long empresaId, long ejercicioId, int limite, CancellationToken ct);
    Task<IReadOnlyList<FilaMayorDto>> MayorAsync(long empresaId, long ejercicioId, string cuenta, long? canalBaseId, CancellationToken ct);

    Task<IReadOnlyList<PendienteDto>> ListarPendientesAsync(long empresaId, long ejercicioId, CancellationToken ct);
    Task AprobarAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct);
    Task RechazarAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct);
}

public sealed class AsientoRepositorio(
    IConexionFactory conexiones,
    ICanalRepositorio canales,
    RepartidorCanales repartidor) : IAsientoRepositorio
{
    private sealed class Contexto { public long EjercicioId { get; set; } public long PeriodoId { get; set; } }
    private sealed class Descuadre { public long CanalBaseId { get; set; } public decimal Debe { get; set; } public decimal Haber { get; set; } }
    private sealed class AsientoFila { public string Estado { get; set; } = ""; public DateOnly Fecha { get; set; } public int Apuntes { get; set; } }

    public async Task<AsientoCreado> CrearAsync(long empresaId, long ejercicioId, long usuarioId, CrearAsientoCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var r = await CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, cmd, "MANUAL", null, null, ct);
        await tx.CommitAsync(ct);
        return r;
    }

    public async Task<AsientoCreado> CrearEnTransaccionAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long ejercicioId,
        long? usuarioId, CrearAsientoCmd cmd, string origen, long? plantillaId, string? documento, CancellationToken ct,
        string tipoPeriodo = "NORMAL", long? asientoOrigenId = null)
    {
        if (cmd.Apuntes.Count < 2 && !cmd.Borrador)
            throw new ReglaContableException("Un asiento necesita al menos dos apuntes.");
        if (cmd.Apuntes.Count == 0)
            throw new ReglaContableException("El asiento no tiene apuntes.");
        if (string.IsNullOrWhiteSpace(cmd.Concepto))
            throw new ReglaContableException("El concepto es obligatorio.");

        // 1) Reparto por canal (lectura con conexión propia: los canales ya están confirmados).
        //    ObtenerConfiguracionAsync comprueba que el canal es de la empresa.
        var configs = new Dictionary<long, ConfiguracionCanal>();
        foreach (var canalId in cmd.Apuntes.Select(a => a.CanalId).Distinct())
            configs[canalId] = await canales.ObtenerConfiguracionAsync(empresaId, canalId, cmd.Fecha, ct);
        var repartos = cmd.Apuntes.Select(a => repartidor.Repartir(configs[a.CanalId], a.Debe, a.Haber)).ToList();

        // 2) El ejercicio ELEGIDO debe contener la fecha y estar abierto, igual que el periodo.
        //    FOR SHARE: un cierre simultáneo espera a este asiento, y si el cierre gana, aquí ya se ve el ejercicio cerrado.
        var ctx = await cn.QuerySingleOrDefaultAsync<Contexto>(new CommandDefinition("""
            SELECT e.id AS ejercicio_id, p.id AS periodo_id
            FROM core.ejercicio e
            JOIN core.periodo p ON p.ejercicio_id = e.id AND p.tipo = @tipoPeriodo
                               AND @fecha BETWEEN p.fecha_inicio AND p.fecha_fin AND p.estado = 'ABIERTO'
            WHERE e.id = @ejercicioId AND e.empresa_id = @empresaId AND e.estado = 'ABIERTO'
              AND @fecha BETWEEN e.fecha_inicio AND e.fecha_fin
            FOR SHARE OF e
            """, new { empresaId, ejercicioId, fecha = cmd.Fecha, tipoPeriodo }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException(
                $"La fecha {cmd.Fecha:dd/MM/yyyy} no está dentro del ejercicio seleccionado, o el ejercicio o el periodo están cerrados.");

        // 3) Integridad multiempresa: diario, cuentas, terceros y actividades deben ser de ESTA empresa
        var diarioOk = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM conta.diario WHERE id = @diarioId AND empresa_id = @empresaId AND activo)",
            new { empresaId, diarioId = cmd.DiarioId }, tx, cancellationToken: ct));
        if (!diarioOk) throw new ReglaContableException("El diario no existe en la empresa o está inactivo.");

        var cuentaIds = cmd.Apuntes.Select(a => a.CuentaId).Distinct().ToArray();
        var okCuentas = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM conta.cuenta WHERE empresa_id = @empresaId AND es_imputable AND activa AND id = ANY(@cuentaIds)",
            new { empresaId, cuentaIds }, tx, cancellationToken: ct));
        if (okCuentas != cuentaIds.Length)
            throw new ReglaContableException("Alguna cuenta no existe, está inactiva o no admite apuntes.");

        var terceroIds = cmd.Apuntes.Where(a => a.TerceroId is not null).Select(a => a.TerceroId!.Value).Distinct().ToArray();
        if (terceroIds.Length > 0)
        {
            var okTerceros = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM conta.tercero WHERE empresa_id = @empresaId AND id = ANY(@terceroIds)",
                new { empresaId, terceroIds }, tx, cancellationToken: ct));
            if (okTerceros != terceroIds.Length) throw new ReglaContableException("Algún tercero no pertenece a la empresa.");
        }

        var actividadIds = cmd.Apuntes.Where(a => a.ActividadId is not null).Select(a => a.ActividadId!.Value).Distinct().ToArray();
        if (actividadIds.Length > 0)
        {
            var okAct = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM core.empresa_actividad WHERE empresa_id = @empresaId AND id = ANY(@actividadIds)",
                new { empresaId, actividadIds }, tx, cancellationToken: ct));
            if (okAct != actividadIds.Length) throw new ReglaContableException("Alguna actividad no pertenece a la empresa.");
        }

        // 4) Número correlativo por empresa / ejercicio / diario
        var numero = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO conta.contador_asiento (empresa_id, ejercicio_id, diario_id, ultimo_numero)
            VALUES (@empresaId, @ejercicioId, @diarioId, 1)
            ON CONFLICT (empresa_id, ejercicio_id, diario_id)
            DO UPDATE SET ultimo_numero = conta.contador_asiento.ultimo_numero + 1
            RETURNING ultimo_numero
            """, new { empresaId, ejercicioId = ctx.EjercicioId, diarioId = cmd.DiarioId }, tx, cancellationToken: ct));

        var estado = cmd.Borrador ? "BORRADOR" : "CONTABILIZADO";
        var asientoId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO conta.asiento (empresa_id, ejercicio_id, periodo_id, diario_id, numero, fecha, concepto, estado, origen,
                                       plantilla_id, documento, asiento_origen_id, creado_por)
            VALUES (@empresaId, @ejercicioId, @periodoId, @diarioId, @numero, @fecha, @concepto, @estado, @origen,
                    @plantillaId, @documento, @asientoOrigenId, @usuarioId)
            RETURNING id
            """, new
        {
            empresaId, ejercicioId = ctx.EjercicioId, periodoId = ctx.PeriodoId, diarioId = cmd.DiarioId,
            numero, fecha = cmd.Fecha, concepto = cmd.Concepto.Trim(), estado, origen, plantillaId, documento, asientoOrigenId, usuarioId
        }, tx, cancellationToken: ct));

        // 5) Apuntes + reparto materializado por canal base
        short linea = 0;
        for (var i = 0; i < cmd.Apuntes.Count; i++)
        {
            var a = cmd.Apuntes[i];
            linea++;
            var apunteId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO conta.apunte (empresa_id, asiento_id, linea, cuenta_id, tercero_id, actividad_id, canal_id, debe, haber, concepto)
                VALUES (@empresaId, @asientoId, @linea, @cuentaId, @terceroId, @actividadId, @canalId, @debe, @haber, @concepto)
                RETURNING id
                """, new
            {
                empresaId, asientoId, linea, cuentaId = a.CuentaId, terceroId = a.TerceroId, actividadId = a.ActividadId,
                canalId = a.CanalId, debe = a.Debe, haber = a.Haber, concepto = a.Concepto
            }, tx, cancellationToken: ct));

            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO conta.apunte_canal (apunte_id, canal_base_id, canal_version_id, debe, haber)
                VALUES (@apunteId, @canalBaseId, @versionId, @debe, @haber)
                """, repartos[i].Select(r => new
            {
                apunteId, canalBaseId = r.CanalBaseId, versionId = r.CanalVersionId, debe = r.Debe, haber = r.Haber
            }), tx, cancellationToken: ct));
        }

        // 6) Un asiento contabilizado debe cuadrar en CADA canal base
        if (!cmd.Borrador) await ValidarCuadreAsync(cn, tx, asientoId, ct);

        return new AsientoCreado(asientoId, numero, estado);
    }

    private static async Task ValidarCuadreAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long asientoId, CancellationToken ct)
    {
        var descuadres = (await cn.QueryAsync<Descuadre>(new CommandDefinition(
            "SELECT canal_base_id, debe, haber FROM conta.validar_asiento(@asientoId)",
            new { asientoId }, tx, cancellationToken: ct))).AsList();
        if (descuadres.Count > 0)
            throw new ReglaContableException("El asiento no cuadra en todos los canales.",
                string.Join("; ", descuadres.Select(d => $"canal {d.CanalBaseId}: debe {d.Debe:N2} / haber {d.Haber:N2}")));
    }

    // ---------------------------------------------------------------- revisión de borradores
    public async Task<IReadOnlyList<PendienteDto>> ListarPendientesAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT 'ASIENTO' AS tipo, s.id, d.codigo || ' nº ' || s.numero AS referencia, s.fecha, s.concepto AS descripcion,
                   COALESCE(SUM(a.debe), 0) AS importe, COALESCE(u.nombre, '') AS creado_por
            FROM conta.asiento s
            JOIN conta.diario d ON d.id = s.diario_id
            LEFT JOIN core.usuario u ON u.id = s.creado_por
            LEFT JOIN conta.apunte a ON a.asiento_id = s.id
            WHERE s.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId AND s.estado = 'BORRADOR'
            GROUP BY s.id, d.codigo, u.nombre
            ORDER BY s.fecha, s.id
            """;
        return (await cn.QueryAsync<PendienteDto>(new CommandDefinition(sql, new { empresaId, ejercicioId }, cancellationToken: ct))).AsList();
    }

    public async Task AprobarAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var a = await cn.QuerySingleOrDefaultAsync<AsientoFila>(new CommandDefinition("""
            SELECT s.estado, s.fecha, (SELECT count(*) FROM conta.apunte WHERE asiento_id = s.id)::int AS apuntes
            FROM conta.asiento s
            WHERE s.id = @asientoId AND s.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId
            FOR UPDATE
            """, new { asientoId, empresaId, ejercicioId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El asiento no existe en este ejercicio.");

        if (a.Estado != "BORRADOR") throw new ReglaContableException("Solo se pueden aprobar asientos en borrador.");
        if (a.Apuntes < 2) throw new ReglaContableException("Un asiento necesita al menos dos apuntes.");

        var abierto = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
              SELECT 1 FROM core.ejercicio e JOIN core.periodo p ON p.ejercicio_id = e.id AND p.tipo = 'NORMAL'
               AND @fecha BETWEEN p.fecha_inicio AND p.fecha_fin AND p.estado = 'ABIERTO'
              WHERE e.id = @ejercicioId AND e.estado = 'ABIERTO')
            """, new { ejercicioId, fecha = a.Fecha }, tx, cancellationToken: ct));
        if (!abierto) throw new ReglaContableException("El ejercicio o el periodo de ese asiento están cerrados.");

        await ValidarCuadreAsync(cn, tx, asientoId, ct);
        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'CONTABILIZADO' WHERE id = @asientoId",
            new { asientoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task RechazarAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE conta.asiento SET estado = 'ANULADO'
            WHERE id = @asientoId AND empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND estado = 'BORRADOR'
            """, new { asientoId, empresaId, ejercicioId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("No hay un asiento en borrador con ese identificador en este ejercicio.");
    }

    // ---------------------------------------------------------------- consultas
    public async Task<IReadOnlyList<FilaAsientoDto>> ListarAsync(long empresaId, long ejercicioId, int limite, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT s.id AS asiento_id, s.numero, s.fecha, s.concepto, s.estado, d.codigo AS diario,
                   a.linea, c.codigo AS cuenta, c.nombre AS cuenta_nombre, k.codigo AS canal, a.debe, a.haber
            FROM (SELECT * FROM conta.asiento
                  WHERE empresa_id = @empresaId AND ejercicio_id = @ejercicioId
                  ORDER BY fecha DESC, id DESC LIMIT @limite) s
            JOIN conta.diario d  ON d.id = s.diario_id
            JOIN conta.apunte a  ON a.asiento_id = s.id
            JOIN conta.cuenta c  ON c.id = a.cuenta_id
            JOIN canal.canal k   ON k.id = a.canal_id
            ORDER BY s.fecha DESC, s.id DESC, a.linea
            """;
        return (await cn.QueryAsync<FilaAsientoDto>(new CommandDefinition(sql, new { empresaId, ejercicioId, limite }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<FilaMayorDto>> MayorAsync(long empresaId, long ejercicioId, string cuenta, long? canalBaseId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT m.fecha, m.numero, m.concepto, k.codigo AS canal, m.debe, m.haber,
                   SUM(m.debe - m.haber) OVER (PARTITION BY m.canal_base_id ORDER BY m.fecha, m.asiento_id, m.apunte_id) AS saldo
            FROM conta.v_mayor m
            JOIN conta.cuenta c ON c.id = m.cuenta_id
            JOIN canal.canal k  ON k.id = m.canal_base_id
            WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND c.codigo = @cuenta
              AND (@canalBaseId::bigint IS NULL OR m.canal_base_id = @canalBaseId)
            ORDER BY m.canal_base_id, m.fecha, m.asiento_id, m.apunte_id
            """;
        return (await cn.QueryAsync<FilaMayorDto>(new CommandDefinition(sql, new { empresaId, ejercicioId, cuenta, canalBaseId }, cancellationToken: ct))).AsList();
    }
}
