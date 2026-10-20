using Contabilidad.Domain;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IEmpresaAdminRepositorio
{
    Task<IReadOnlyList<PlanModeloDto>> ListarPlanesAsync(CancellationToken ct);
    Task<IReadOnlyList<EmpresaAdminDto>> ListarAsync(CancellationToken ct);
    Task<ResultadoAltaEmpresa> CrearAsync(CrearEmpresaCmd cmd, CancellationToken ct);
    Task<long> CrearEjercicioAsync(long empresaId, CrearEjercicioCmd cmd, CancellationToken ct);
}

public sealed class EmpresaAdminRepositorio(IConexionFactory conexiones) : IEmpresaAdminRepositorio
{
    private static readonly string[] Formas = ["AUTONOMO", "SL", "SA", "SLU", "COMUNIDAD_BIENES", "SOCIEDAD_CIVIL", "COOPERATIVA", "ASOCIACION", "OTRA"];
    private static readonly string[] Impuestos = ["IVA", "IGIC", "IPSI", "EXENTO"];
    private static readonly string[] Documentos = ["NIF", "NIE", "CIF", "OTRO"];

    public async Task<IReadOnlyList<PlanModeloDto>> ListarPlanesAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<PlanModeloDto>(new CommandDefinition(
            "SELECT id, codigo, nombre, tipo FROM cat.plan_modelo ORDER BY nombre", cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<EmpresaAdminDto>> ListarAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT e.id, e.codigo, e.nombre, n.numero AS nif, e.forma_juridica, p.nombre AS plan, e.impuesto_indirecto, e.activa
            FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id JOIN cat.plan_modelo p ON p.id = e.plan_modelo_id
            ORDER BY e.nombre
            """;
        return (await cn.QueryAsync<EmpresaAdminDto>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
    }

    // ------------------------------------------------------------------ alta de empresa
    public async Task<ResultadoAltaEmpresa> CrearAsync(CrearEmpresaCmd cmd, CancellationToken ct)
    {
        var nif = (cmd.Nif ?? "").Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
        var nombre = (cmd.Nombre ?? "").Trim();
        var codigo = (cmd.Codigo ?? "").Trim().ToUpperInvariant();
        if (nif.Length == 0 || nombre.Length == 0 || codigo.Length == 0)
            throw new ReglaContableException("NIF, nombre y código son obligatorios.");
        if (codigo.Length > 10) throw new ReglaContableException("El código admite como máximo 10 caracteres.");
        if (!Formas.Contains(cmd.FormaJuridica)) throw new ReglaContableException("Forma jurídica no válida.");
        if (!Impuestos.Contains(cmd.ImpuestoIndirecto)) throw new ReglaContableException("Impuesto indirecto no válido.");
        if (!Documentos.Contains(cmd.TipoDocumento)) throw new ReglaContableException("Tipo de documento no válido.");
        if (cmd.DigitosCuenta is < 6 or > 12) throw new ReglaContableException("Los dígitos de cuenta deben estar entre 6 y 12.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var avisos = new List<string>();

        try
        {
            var planOk = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM cat.plan_modelo WHERE id = @id)", new { id = cmd.PlanModeloId }, tx, cancellationToken: ct));
            if (!planOk) throw new ReglaContableException("El plan contable modelo no existe.");

            var nifId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.nif (pais_id, tipo_documento, numero, razon_social, es_persona_fisica)
                SELECT id, @tipo, @nif, @nombre, @fisica FROM cat.pais WHERE iso2 = 'ES'
                ON CONFLICT (pais_id, numero) DO UPDATE SET razon_social = EXCLUDED.razon_social
                RETURNING id
                """, new { tipo = cmd.TipoDocumento, nif, nombre, fisica = cmd.FormaJuridica == "AUTONOMO" }, tx, cancellationToken: ct));

            var empresaId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.empresa (nif_id, codigo, nombre, forma_juridica, plan_modelo_id, digitos_cuenta, impuesto_indirecto, tributa_is)
                VALUES (@nifId, @codigo, @nombre, @forma, @plan, @digitos, @impuesto, @tributaIs) RETURNING id
                """, new
            {
                nifId, codigo, nombre, forma = cmd.FormaJuridica, plan = cmd.PlanModeloId, digitos = cmd.DigitosCuenta,
                impuesto = cmd.ImpuestoIndirecto, tributaIs = cmd.TributaIs
            }, tx, cancellationToken: ct));

            await CrearDiariosYCanalesAsync(cn, tx, empresaId, ct);
            await CopiarPlanAsync(cn, tx, empresaId, cmd.PlanModeloId, cmd.DigitosCuenta, avisos, ct);
            await cn.ExecuteAsync(new CommandDefinition("SELECT inmov.crear_categorias_defecto(@empresaId)", new { empresaId }, tx, cancellationToken: ct));

            if (cmd.PrimerEjercicio is { } ej) await CrearEjercicioEnTransaccionAsync(cn, tx, empresaId, ej, ct);
            else avisos.Add("La empresa no tiene ejercicio: crea uno antes de empezar a trabajar.");

            await tx.CommitAsync(ct);
            return new ResultadoAltaEmpresa(empresaId, avisos);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Ya existe una empresa con ese NIF o con ese código.");
        }
    }

    private static Task CrearDiariosYCanalesAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO conta.diario (empresa_id, codigo, nombre, tipo) VALUES
              (@e, 'GEN',  'Diario general', 'GENERAL'), (@e, 'COMP', 'Compras', 'COMPRAS'),
              (@e, 'VENT', 'Ventas', 'VENTAS'),          (@e, 'BANC', 'Bancos', 'BANCOS'),
              (@e, 'AMOR', 'Amortizaciones', 'AMORTIZACION');

            INSERT INTO core.forma_pago (empresa_id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos) VALUES
              (@e, 'CONT',  'Contado',                 'EFECTIVO',      1,  0,  0),
              (@e, 'TRF',   'Transferencia',           'TRANSFERENCIA', 1,  0,  0),
              (@e, 'TRF30', 'Transferencia a 30 días', 'TRANSFERENCIA', 1, 30,  0),
              (@e, 'REC30', 'Recibo a 30 días',        'RECIBO',        1, 30,  0),
              (@e, 'REC3060','Recibo 30/60 días',      'RECIBO',        2, 30, 30);

            INSERT INTO canal.canal (empresa_id, codigo, nombre, tipo) VALUES
              (@e, '1', 'Canal 1', 'BASE'), (@e, '2', 'Canal 2', 'BASE'), (@e, '3', 'Canal 3', 'BASE'),
              (@e, 'A', 'A = 1+2+3', 'DERIVADO'), (@e, 'B', 'B = 1+2', 'DERIVADO'),
              (@e, 'C', 'C = 1+3', 'DERIVADO'),   (@e, 'D', 'D = 2+3', 'DERIVADO');

            INSERT INTO canal.canal_version (canal_id, vigente_desde, descripcion)
            SELECT id, date '2000-01-01', 'Versión inicial' FROM canal.canal WHERE empresa_id = @e AND tipo = 'DERIVADO';

            INSERT INTO canal.canal_version_linea (canal_version_id, orden, canal_base_id, modo, porcentaje)
            SELECT v.id, (row_number() OVER (PARTITION BY d.codigo ORDER BY b.codigo))::smallint, b.id, 'PORCENTAJE', 100
            FROM (VALUES ('A','1'),('A','2'),('A','3'),('B','1'),('B','2'),('C','1'),('C','3'),('D','2'),('D','3')) m(der, bas)
            JOIN canal.canal d ON d.empresa_id = @e AND d.codigo = m.der
            JOIN canal.canal_version v ON v.canal_id = d.id
            JOIN canal.canal b ON b.empresa_id = @e AND b.codigo = m.bas;
            """, new { e = empresaId }, tx, cancellationToken: ct));

    /// <summary>Copia el plan modelo: subcuentas (nivel 4) rellenadas con ceros hasta los dígitos de la empresa; el resto, tal cual.</summary>
    private static async Task CopiarPlanAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long planId, int digitos,
        List<string> avisos, CancellationToken ct)
    {
        var copiadas = await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, naturaleza, es_imputable, cuenta_modelo_id)
            SELECT @empresaId, CASE WHEN nivel >= 4 THEN rpad(codigo, @digitos, '0') ELSE codigo END,
                   nombre, nivel, naturaleza, nivel >= 4, id
            FROM cat.cuenta_modelo WHERE plan_modelo_id = @planId
            """, new { empresaId, planId, digitos }, tx, cancellationToken: ct));

        if (copiadas == 0)
        {
            avisos.Add("El plan modelo elegido no tiene cuentas cargadas (cat.cuenta_modelo): la empresa se creó sin plan contable.");
            return;
        }

        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE conta.cuenta c SET padre_id = p.id
            FROM cat.cuenta_modelo m
            JOIN conta.cuenta p ON p.empresa_id = @empresaId AND p.codigo = m.padre_codigo
            WHERE c.empresa_id = @empresaId AND c.cuenta_modelo_id = m.id AND m.padre_codigo IS NOT NULL
            """, new { empresaId }, tx, cancellationToken: ct));

        var asignadas = (await cn.QueryAsync<string>(new CommandDefinition("""
            INSERT INTO conta.cuenta_automatica (empresa_id, rol, cuenta_id)
            SELECT @empresaId, m.rol, c.id
            FROM (VALUES ('IVA_SOPORTADO','4720'), ('IVA_REPERCUTIDO','4770'), ('RETENCION_SOPORTADA','4730'),
                         ('RETENCION_PRACTICADA','4751'), ('COMPRAS','6000'), ('VENTAS','7000'),
                         ('HP_ACREEDORA_IVA','4750'), ('HP_DEUDORA_IVA','4700'), ('RESULTADO_EJERCICIO','1290')) m(rol, cod)
            JOIN conta.cuenta c ON c.empresa_id = @empresaId AND c.codigo = rpad(m.cod, @digitos, '0')
            RETURNING rol
            """, new { empresaId, digitos }, tx, cancellationToken: ct))).ToHashSet();

        foreach (var rol in new[] { "IVA_SOPORTADO", "IVA_REPERCUTIDO", "RETENCION_SOPORTADA", "RETENCION_PRACTICADA", "COMPRAS", "VENTAS",
                                    "HP_ACREEDORA_IVA", "HP_DEUDORA_IVA", "RESULTADO_EJERCICIO" })
            if (!asignadas.Contains(rol))
                avisos.Add($"No se encontró la cuenta para el rol {rol}: configúrala en cuentas automáticas.");
    }

    // ------------------------------------------------------------------ ejercicios
    public async Task<long> CrearEjercicioAsync(long empresaId, CrearEjercicioCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM core.empresa WHERE id = @empresaId)", new { empresaId }, tx, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("La empresa no existe.");

        var id = await CrearEjercicioEnTransaccionAsync(cn, tx, empresaId, cmd, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Un ejercicio va de un día 1 al último día de un mes y dura como máximo 12 meses (admite ejercicio partido).</summary>
    private static async Task<long> CrearEjercicioEnTransaccionAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId,
        CrearEjercicioCmd cmd, CancellationToken ct)
    {
        var codigo = (cmd.Codigo ?? "").Trim();
        if (codigo.Length == 0 || codigo.Length > 10) throw new ReglaContableException("El código del ejercicio es obligatorio (máximo 10 caracteres).");
        if (cmd.FechaFin <= cmd.FechaInicio) throw new ReglaContableException("La fecha de fin debe ser posterior a la de inicio.");
        if (cmd.FechaInicio.Day != 1) throw new ReglaContableException("El ejercicio debe empezar el día 1 de un mes.");
        if (cmd.FechaFin.Day != DateTime.DaysInMonth(cmd.FechaFin.Year, cmd.FechaFin.Month))
            throw new ReglaContableException("El ejercicio debe terminar el último día de un mes.");

        var periodos = new List<(short Numero, DateOnly Ini, DateOnly Fin)>();
        var ini = cmd.FechaInicio;
        short n = 1;
        while (ini <= cmd.FechaFin)
        {
            var finMes = new DateOnly(ini.Year, ini.Month, DateTime.DaysInMonth(ini.Year, ini.Month));
            var fin = finMes < cmd.FechaFin ? finMes : cmd.FechaFin;
            periodos.Add((n++, ini, fin));
            ini = fin.AddDays(1);
        }
        if (periodos.Count > 12) throw new ReglaContableException("Un ejercicio no puede durar más de 12 meses.");

        try
        {
            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.ejercicio (empresa_id, codigo, fecha_inicio, fecha_fin) VALUES (@empresaId, @codigo, @inicio, @fin) RETURNING id
                """, new { empresaId, codigo, inicio = cmd.FechaInicio, fin = cmd.FechaFin }, tx, cancellationToken: ct));

            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO core.periodo (ejercicio_id, numero, tipo, fecha_inicio, fecha_fin) VALUES (@id, @numero, 'NORMAL', @ini, @fin)
                """, periodos.Select(p => new { id, numero = p.Numero, ini = p.Ini, fin = p.Fin }), tx, cancellationToken: ct));
            return id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            throw new ReglaContableException("Las fechas se solapan con otro ejercicio de la empresa.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Ya existe un ejercicio con ese código en la empresa.");
        }
    }
}
