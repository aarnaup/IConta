using Contabilidad.Domain;
using Contabilidad.Domain.Nominas;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface INominaRepositorio
{
    Task<IReadOnlyList<NominaDto>> ListarAsync(long empresaId, long ejercicioId, CancellationToken ct);
    Task<NominaDto> CrearAsync(long empresaId, long ejercicioId, long usuarioId, CrearNominaCmd cmd, CancellationToken ct);
    Task AnularAsync(long empresaId, long ejercicioId, long nominaId, CancellationToken ct);
    Task<IReadOnlyList<EmpleadoDto>> EmpleadosAsync(long empresaId, CancellationToken ct);
    Task GuardarEmpleadoAsync(long empresaId, EmpleadoDto e, CancellationToken ct);
}

/// <summary>
/// Nóminas: cada una genera su asiento y sus importes alimentan el 111 (rendimientos del trabajo, casillas 01 a 06) y el 190 (clave A).
/// El pago del líquido (465 contra bancos) y el de los seguros sociales (476) se contabilizan desde tesorería o con un asiento.
/// </summary>
public sealed class NominaRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : INominaRepositorio
{
    private sealed class EjercicioFila
    {
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
        public string Estado { get; set; } = "";
    }

    private const string SqlLista = """
        SELECT n.id, n.tercero_id, t.nombre AS empleado, ni.numero AS nif, n.fecha, n.periodo, n.bruto, n.especie, n.ingreso_a_cuenta,
               n.ingreso_repercutido, n.retencion, CASE WHEN n.bruto = 0 THEN 0 ELSE round(n.retencion * 100 / n.bruto, 2) END AS porcentaje_retencion,
               n.ss_trabajador, n.ss_empresa, n.anticipos, n.liquido, n.estado, n.asiento_id, a.numero AS asiento_numero
        FROM conta.nomina n
        JOIN conta.tercero t ON t.id = n.tercero_id
        JOIN core.nif ni ON ni.id = t.nif_id
        LEFT JOIN conta.asiento a ON a.id = n.asiento_id
        """;

    public async Task<IReadOnlyList<NominaDto>> ListarAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<NominaDto>(new CommandDefinition(SqlLista +
            " WHERE n.empresa_id = @empresaId AND n.ejercicio_id = @ejercicioId ORDER BY n.fecha DESC, t.nombre, n.id DESC",
            new { empresaId, ejercicioId }, cancellationToken: ct))).AsList();
    }

    public async Task<NominaDto> CrearAsync(long empresaId, long ejercicioId, long usuarioId, CrearNominaCmd cmd, CancellationToken ct)
    {
        var imp = new ImportesNomina(cmd.Bruto, cmd.Especie, cmd.IngresoACuenta, cmd.IngresoRepercutido, cmd.Retencion, cmd.SsTrabajador,
            cmd.SsEmpresa, cmd.Anticipos);
        imp.Validar();
        var periodo = string.IsNullOrWhiteSpace(cmd.Periodo) ? null : cmd.Periodo.Trim();
        if (periodo is { Length: > 60 }) throw new ReglaContableException("El periodo admite como mucho 60 caracteres.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var ej = await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
            "SELECT fecha_inicio, fecha_fin, estado FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
            new { ejercicioId, empresaId }, tx, cancellationToken: ct)) ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        if (cmd.Fecha < ej.FechaInicio || cmd.Fecha > ej.FechaFin)
            throw new ReglaContableException($"La fecha debe estar dentro del ejercicio ({ej.FechaInicio:dd/MM/yyyy} – {ej.FechaFin:dd/MM/yyyy}).");
        var empleado = await cn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT nombre FROM conta.tercero WHERE id = @id AND empresa_id = @empresaId AND activo", new { id = cmd.TerceroId, empresaId }, tx,
            cancellationToken: ct)) ?? throw new ReglaContableException("El trabajador no existe en la empresa (dalo de alta como tercero con su NIF).");
        var canalOk = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM canal.canal WHERE id = @id AND empresa_id = @empresaId)", new { id = cmd.CanalId, empresaId }, tx,
            cancellationToken: ct));
        if (!canalOk) throw new ReglaContableException("El canal no existe en la empresa.");

        async Task<long> Cuenta(string codigo) => await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT inmov.asegurar_cuenta(@empresaId, @codigo)", new { empresaId, codigo }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException($"No se encuentra la cuenta {codigo} en el plan de la empresa.");
        var cuentas = new CuentasNomina(await Cuenta("6400"), await Cuenta("6420"), await Cuenta("6490"), await Cuenta("4751"),
            await Cuenta("4760"), await Cuenta("4600"), await Cuenta("4650"));
        var apuntes = GeneradorAsientoNomina.Generar(imp, cuentas, cmd.CanalId);
        var diario = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'NOMINAS' ORDER BY id LIMIT 1", new { empresaId }, tx,
            cancellationToken: ct)) ?? await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct);
        var concepto = $"Nómina {periodo ?? cmd.Fecha.ToString("MM/yyyy")} · {empleado}";
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
            new CrearAsientoCmd { Fecha = cmd.Fecha, DiarioId = diario, Concepto = concepto, Apuntes = ConsultasContables.AApuntes(apuntes, concepto) },
            "NOMINA", null, $"NOM {cmd.Fecha:yyyy-MM}", ct);

        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO conta.nomina (empresa_id, ejercicio_id, tercero_id, canal_id, fecha, periodo, bruto, especie, ingreso_a_cuenta, ingreso_repercutido,
                                      retencion, ss_trabajador, ss_empresa, anticipos, liquido, asiento_id)
            VALUES (@empresaId, @ejercicioId, @tercero, @canal, @fecha, @periodo, @bruto, @especie, @ic, @rep, @ret, @sst, @sse, @ant, @liq, @asiento)
            RETURNING id
            """, new
        {
            empresaId, ejercicioId, tercero = cmd.TerceroId, canal = cmd.CanalId, fecha = cmd.Fecha, periodo, bruto = imp.Bruto, especie = imp.Especie,
            ic = imp.IngresoACuenta, rep = imp.IngresoRepercutido, ret = imp.Retencion, sst = imp.SsTrabajador, sse = imp.SsEmpresa,
            ant = imp.Anticipos, liq = imp.Liquido, asiento = a.Id
        }, tx, cancellationToken: ct));
        // El trabajador queda registrado para el 190 (con los datos por defecto hasta que se completen)
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO conta.empleado (tercero_id, empresa_id) VALUES (@tercero, @empresaId) ON CONFLICT (tercero_id) DO NOTHING
            """, new { tercero = cmd.TerceroId, empresaId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return await cn.QuerySingleAsync<NominaDto>(new CommandDefinition(SqlLista + " WHERE n.id = @id", new { id }, cancellationToken: ct));
    }

    public async Task AnularAsync(long empresaId, long ejercicioId, long nominaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var n = await cn.QuerySingleOrDefaultAsync<(long? AsientoId, string Estado, string EstadoEjercicio)?>(new CommandDefinition("""
            SELECT n.asiento_id, n.estado, e.estado FROM conta.nomina n JOIN core.ejercicio e ON e.id = n.ejercicio_id
            WHERE n.id = @nominaId AND n.empresa_id = @empresaId AND n.ejercicio_id = @ejercicioId FOR UPDATE OF n
            """, new { nominaId, empresaId, ejercicioId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La nómina no existe en este ejercicio.");
        if (n.Estado == "ANULADA") throw new ReglaContableException("La nómina ya está anulada.");
        if (n.EstadoEjercicio != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.nomina SET estado = 'ANULADA' WHERE id = @nominaId", new { nominaId }, tx, cancellationToken: ct));
        if (n.AsientoId is { } a)
            await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'ANULADO' WHERE id = @a", new { a }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<EmpleadoDto>> EmpleadosAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<EmpleadoDto>(new CommandDefinition("""
            SELECT e.tercero_id, t.nombre, n.numero AS nif, e.anio_nacimiento, e.situacion_familiar, e.nif_conyuge, e.discapacidad, e.contrato,
                   e.movilidad_geografica
            FROM conta.empleado e JOIN conta.tercero t ON t.id = e.tercero_id JOIN core.nif n ON n.id = t.nif_id
            WHERE e.empresa_id = @empresaId ORDER BY t.nombre
            """, new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task GuardarEmpleadoAsync(long empresaId, EmpleadoDto e, CancellationToken ct)
    {
        if (e.AnioNacimiento is < 1900 or > 2100) throw new ReglaContableException("El año de nacimiento no es válido.");
        if (e.SituacionFamiliar is < 1 or > 3) throw new ReglaContableException("La situación familiar es 1, 2 o 3.");
        if (e.Discapacidad is < 0 or > 3) throw new ReglaContableException("La discapacidad va de 0 a 3.");
        if (e.Contrato is < 1 or > 4) throw new ReglaContableException("El tipo de contrato o relación va de 1 a 4.");
        var conyuge = string.IsNullOrWhiteSpace(e.NifConyuge) ? null : e.NifConyuge.Trim().ToUpperInvariant();
        if (conyuge is { Length: > 9 }) throw new ReglaContableException("El NIF del cónyuge tiene 9 caracteres.");
        if (e.SituacionFamiliar == 2 && conyuge is null) throw new ReglaContableException("Con la situación familiar 2 indica el NIF del cónyuge.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM conta.tercero WHERE id = @id AND empresa_id = @empresaId)", new { id = e.TerceroId, empresaId }, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("El trabajador no existe en la empresa.");
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO conta.empleado (tercero_id, empresa_id, anio_nacimiento, situacion_familiar, nif_conyuge, discapacidad, contrato, movilidad_geografica)
            VALUES (@id, @empresaId, @anio, @sit, @conyuge, @disc, @contrato, @mov)
            ON CONFLICT (tercero_id) DO UPDATE SET anio_nacimiento = EXCLUDED.anio_nacimiento, situacion_familiar = EXCLUDED.situacion_familiar,
              nif_conyuge = EXCLUDED.nif_conyuge, discapacidad = EXCLUDED.discapacidad, contrato = EXCLUDED.contrato,
              movilidad_geografica = EXCLUDED.movilidad_geografica
            """, new { id = e.TerceroId, empresaId, anio = e.AnioNacimiento, sit = e.SituacionFamiliar, conyuge, disc = e.Discapacidad,
                       contrato = e.Contrato, mov = e.MovilidadGeografica }, cancellationToken: ct));
    }
}
