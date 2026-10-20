using System.Globalization;
using Contabilidad.Domain;
using Contabilidad.Domain.Analitica;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IPresupuestoRepositorio
{
    Task<IReadOnlyList<PresupuestoDto>> ListarAsync(long empresaId, long ejercicioId, CancellationToken ct);
    Task<PresupuestoDto> ObtenerAsync(long empresaId, long presupuestoId, CancellationToken ct);
    Task<long> CrearAsync(long empresaId, long ejercicioId, CrearPresupuestoCmd cmd, CancellationToken ct);
    Task GuardarLineasAsync(long empresaId, long presupuestoId, GuardarLineasPresupuestoCmd cmd, CancellationToken ct);
    Task GenerarDesdeRealAsync(long empresaId, long presupuestoId, GenerarDesdeRealCmd cmd, CancellationToken ct);
    Task CambiarEstadoAsync(long empresaId, long presupuestoId, string estado, CancellationToken ct);
    Task<long> NuevaVersionAsync(long empresaId, long presupuestoId, CancellationToken ct);
    Task EliminarAsync(long empresaId, long presupuestoId, CancellationToken ct);
    Task<ComparativaPresupuestoDto> ComparativaAsync(long empresaId, long presupuestoId, int? hastaPeriodo, CancellationToken ct);
}

/// <summary>
/// Presupuestos por ejercicio y canal base: líneas por cuenta (o prefijo, «62» agrupa todas las 62x), con elemento analítico
/// opcional, e importe por periodo. Versiones (cada una con su estado BORRADOR → APROBADO → CERRADO) y comparación con lo real.
/// </summary>
public sealed class PresupuestoRepositorio(IConexionFactory conexiones) : IPresupuestoRepositorio
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private sealed class CabFila
    {
        public long Id { get; set; }
        public long EjercicioId { get; set; }
        public string Codigo { get; set; } = "";
        public string Nombre { get; set; } = "";
        public int Version { get; set; }
        public long? CanalBaseId { get; set; }
        public string? Canal { get; set; }
        public string Estado { get; set; } = "";
    }

    private sealed class LineaFila
    {
        public string CuentaPatron { get; set; } = "";
        public string? CuentaNombre { get; set; }
        public long? ElementoId { get; set; }
        public string? Elemento { get; set; }
        public int PeriodoNumero { get; set; }
        public decimal Importe { get; set; }
    }

    private sealed class PeriodoFila { public int Numero { get; set; } public DateOnly FechaInicio { get; set; } }

    private sealed class RealFila
    {
        public string CuentaPatron { get; set; } = "";
        public long? ElementoId { get; set; }
        public int Periodo { get; set; }
        public decimal Debe { get; set; }
        public decimal Haber { get; set; }
    }

    private sealed class OrigenFila
    {
        public string Cuenta { get; set; } = "";
        public long? ElementoId { get; set; }
        public int Periodo { get; set; }
        public decimal Debe { get; set; }
        public decimal Haber { get; set; }
    }

    private const string SelectCab = """
        SELECT p.id, p.ejercicio_id, p.codigo, p.nombre, p.version, p.canal_base_id, k.codigo AS canal, p.estado
        FROM gest.presupuesto p LEFT JOIN canal.canal k ON k.id = p.canal_base_id
        """;

    private static async Task<List<PeriodoFila>> PeriodosAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long ejercicioId, CancellationToken ct)
        => (await cn.QueryAsync<PeriodoFila>(new CommandDefinition(
            "SELECT numero, fecha_inicio FROM core.periodo WHERE ejercicio_id = @ejercicioId AND tipo = 'NORMAL' ORDER BY numero",
            new { ejercicioId }, tx, cancellationToken: ct))).AsList();

    private static async Task<CabFila> CabeceraAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long presupuestoId, CancellationToken ct,
        bool bloquear = false)
        => await cn.QuerySingleOrDefaultAsync<CabFila>(new CommandDefinition(
               SelectCab + " WHERE p.id = @presupuestoId AND p.empresa_id = @empresaId" + (bloquear ? " FOR UPDATE OF p" : ""),
               new { presupuestoId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El presupuesto no existe en la empresa.");

    private static PresupuestoDto Dto(CabFila c) => new()
    {
        Id = c.Id, EjercicioId = c.EjercicioId, Codigo = c.Codigo, Nombre = c.Nombre, Version = c.Version, CanalBaseId = c.CanalBaseId,
        Canal = c.Canal, Estado = c.Estado
    };

    public async Task<IReadOnlyList<PresupuestoDto>> ListarAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var cab = (await cn.QueryAsync<CabFila>(new CommandDefinition(
            SelectCab + " WHERE p.empresa_id = @empresaId AND p.ejercicio_id = @ejercicioId ORDER BY p.codigo, p.version",
            new { empresaId, ejercicioId }, cancellationToken: ct))).AsList();
        var tot = (await cn.QueryAsync<(long Id, decimal Ingresos, decimal Gastos)>(new CommandDefinition("""
            SELECT l.presupuesto_id, COALESCE(SUM(l.importe) FILTER (WHERE l.cuenta_patron LIKE '7%'), 0),
                   COALESCE(SUM(l.importe) FILTER (WHERE l.cuenta_patron LIKE '6%'), 0)
            FROM gest.presupuesto_linea l JOIN gest.presupuesto p ON p.id = l.presupuesto_id
            WHERE p.empresa_id = @empresaId AND p.ejercicio_id = @ejercicioId GROUP BY l.presupuesto_id
            """, new { empresaId, ejercicioId }, cancellationToken: ct))).ToDictionary(x => x.Id);
        return cab.Select(c =>
        {
            var d = Dto(c);
            if (tot.TryGetValue(c.Id, out var t)) { d.Ingresos = t.Ingresos; d.Gastos = t.Gastos; }
            return d;
        }).ToList();
    }

    public async Task<PresupuestoDto> ObtenerAsync(long empresaId, long presupuestoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await CompletoAsync(cn, empresaId, presupuestoId, ct);
    }

    private static async Task<PresupuestoDto> CompletoAsync(NpgsqlConnection cn, long empresaId, long presupuestoId, CancellationToken ct)
    {
        var c = await CabeceraAsync(cn, null, empresaId, presupuestoId, ct);
        var periodos = await PeriodosAsync(cn, null, c.EjercicioId, ct);
        var lineas = (await cn.QueryAsync<LineaFila>(new CommandDefinition("""
            SELECT l.cuenta_patron, cu.nombre AS cuenta_nombre, l.elemento_id, d.codigo || '/' || e.codigo AS elemento, l.periodo_numero, l.importe
            FROM gest.presupuesto_linea l
            JOIN gest.presupuesto p ON p.id = l.presupuesto_id
            LEFT JOIN conta.cuenta cu ON cu.empresa_id = p.empresa_id AND cu.codigo = l.cuenta_patron
            LEFT JOIN gest.elemento_analitico e ON e.id = l.elemento_id
            LEFT JOIN gest.dimension_analitica d ON d.id = e.dimension_id
            WHERE l.presupuesto_id = @presupuestoId
            ORDER BY l.cuenta_patron, elemento NULLS FIRST, l.periodo_numero
            """, new { presupuestoId }, cancellationToken: ct))).AsList();

        var d = Dto(c);
        d.Periodos = periodos.Select(p => p.FechaInicio.ToString("MMM yyyy", Es)).ToList();
        var idx = periodos.Select((p, i) => (p.Numero, i)).ToDictionary(x => x.Numero, x => x.i);
        foreach (var g in lineas.GroupBy(l => (l.CuentaPatron, l.ElementoId)))
        {
            var imp = new decimal[periodos.Count];
            foreach (var l in g) if (idx.TryGetValue(l.PeriodoNumero, out var i)) imp[i] += l.Importe;
            d.Lineas.Add(new LineaPresupuestoDto
            {
                CuentaPatron = g.Key.CuentaPatron, CuentaNombre = g.First().CuentaNombre, ElementoId = g.Key.ElementoId,
                Elemento = g.First().Elemento, Importes = imp
            });
        }
        d.Ingresos = d.Lineas.Where(l => l.CuentaPatron.StartsWith('7')).Sum(l => l.Total);
        d.Gastos = d.Lineas.Where(l => l.CuentaPatron.StartsWith('6')).Sum(l => l.Total);
        return d;
    }

    public async Task<long> CrearAsync(long empresaId, long ejercicioId, CrearPresupuestoCmd cmd, CancellationToken ct)
    {
        var codigo = (cmd.Codigo ?? "").Trim().ToUpperInvariant();
        var nombre = (cmd.Nombre ?? "").Trim();
        if (codigo.Length is 0 or > 20 || nombre.Length == 0)
            throw new ReglaContableException("El código (hasta 20 caracteres) y el nombre son obligatorios.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId)",
            new { ejercicioId, empresaId }, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("El ejercicio no existe en la empresa.");
        var canal = await cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT id FROM canal.canal WHERE empresa_id = @empresaId AND tipo = 'BASE' AND activo AND (@canalId::bigint IS NULL OR id = @canalId)
            ORDER BY codigo LIMIT 1
            """, new { empresaId, canalId = cmd.CanalId }, cancellationToken: ct))
            ?? throw new ReglaContableException("El canal no es un canal base de la empresa.");
        try
        {
            return await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO gest.presupuesto (empresa_id, ejercicio_id, codigo, nombre, version, canal_base_id)
                VALUES (@empresaId, @ejercicioId, @codigo, left(@nombre, 150), 1, @canal) RETURNING id
                """, new { empresaId, ejercicioId, codigo, nombre, canal }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe el presupuesto {codigo} en el ejercicio; crea una versión nueva.");
        }
    }

    private static void ExigeBorrador(CabFila c)
    {
        if (c.Estado != "BORRADOR")
            throw new ReglaContableException("Solo se modifica un presupuesto en borrador.", "Vuelve a borrador o crea una versión nueva.");
    }

    public async Task GuardarLineasAsync(long empresaId, long presupuestoId, GuardarLineasPresupuestoCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var c = await CabeceraAsync(cn, tx, empresaId, presupuestoId, ct, bloquear: true);
        ExigeBorrador(c);
        var n = (await PeriodosAsync(cn, tx, c.EjercicioId, ct)).Count;

        var lineas = new List<(string Patron, long? ElementoId, decimal[] Importes)>();
        foreach (var l in cmd.Lineas)
        {
            var patron = Presupuestos.ValidarPatron(l.CuentaPatron);
            decimal[] imp;
            if (l.Importes is { Length: > 0 })
            {
                if (l.Importes.Length != n)
                    throw new ReglaContableException($"La línea {patron} tiene {l.Importes.Length} importes y el ejercicio {n} periodos.");
                imp = l.Importes.Select(x => Math.Round(x, 2, MidpointRounding.AwayFromZero)).ToArray();
            }
            else imp = Presupuestos.RepartirLineal(Math.Round(l.Total ?? 0, 2, MidpointRounding.AwayFromZero), n);
            lineas.Add((patron, l.ElementoId, imp));
        }
        var duplicada = lineas.GroupBy(l => (l.Patron, l.ElementoId)).FirstOrDefault(g => g.Count() > 1);
        if (duplicada is not null) throw new ReglaContableException($"La cuenta {duplicada.Key.Patron} está repetida con el mismo elemento.");

        var elementos = lineas.Where(l => l.ElementoId is not null).Select(l => l.ElementoId!.Value).Distinct().ToArray();
        if (elementos.Length > 0)
        {
            var ok = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(*) FROM gest.elemento_analitico e JOIN gest.dimension_analitica d ON d.id = e.dimension_id
                WHERE d.empresa_id = @empresaId AND e.id = ANY(@elementos)
                """, new { empresaId, elementos }, tx, cancellationToken: ct));
            if (ok != elementos.Length) throw new ReglaContableException("Algún elemento analítico no es de la empresa.");
        }

        await GrabarAsync(cn, tx, presupuestoId, lineas, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task GrabarAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long presupuestoId,
        IEnumerable<(string Patron, long? ElementoId, decimal[] Importes)> lineas, CancellationToken ct)
    {
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM gest.presupuesto_linea WHERE presupuesto_id = @presupuestoId",
            new { presupuestoId }, tx, cancellationToken: ct));
        var filas = lineas.SelectMany(l => l.Importes.Select((imp, i) => new
        {
            presupuestoId, patron = l.Patron, elementoId = l.ElementoId, periodo = (short)(i + 1), imp
        })).Where(f => f.imp != 0).ToList();
        if (filas.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO gest.presupuesto_linea (presupuesto_id, cuenta_patron, elemento_id, periodo_numero, importe)
                VALUES (@presupuestoId, @patron, @elementoId, @periodo, @imp)
                """, filas, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Sustituye las líneas por lo real de otro ejercicio de la empresa (grupos 6 y 7, mismo canal base), mes a mes, con el
    /// incremento indicado. Con dimensión, una línea por elemento imputado y otra sin elemento para lo no imputado.
    /// </summary>
    public async Task GenerarDesdeRealAsync(long empresaId, long presupuestoId, GenerarDesdeRealCmd cmd, CancellationToken ct)
    {
        if (cmd.Digitos is < 1 or > 15) throw new ReglaContableException("Los dígitos de agrupación van de 1 a 15.");
        if (cmd.IncrementoPorcentaje <= -100) throw new ReglaContableException("El incremento debe ser mayor que −100 %.");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var c = await CabeceraAsync(cn, tx, empresaId, presupuestoId, ct, bloquear: true);
        ExigeBorrador(c);
        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM core.ejercicio WHERE id = @id AND empresa_id = @empresaId)",
            new { id = cmd.EjercicioOrigenId, empresaId }, tx, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("El ejercicio de origen no existe en la empresa.");
        if (cmd.DimensionId is { } dim && !await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM gest.dimension_analitica WHERE id = @dim AND empresa_id = @empresaId)",
                new { dim, empresaId }, tx, cancellationToken: ct)))
            throw new ReglaContableException("La dimensión no existe en la empresa.");
        var n = (await PeriodosAsync(cn, tx, c.EjercicioId, ct)).Count;

        var movs = (await cn.QueryAsync<OrigenFila>(new CommandDefinition("""
            WITH mov AS (
              SELECT m.apunte_id, left(cu.codigo, @digitos) AS cuenta, pe.numero AS periodo, m.debe, m.haber, a.debe AS ad, a.haber AS ah
              FROM conta.v_mayor m
              JOIN conta.asiento s ON s.id = m.asiento_id
              JOIN conta.apunte a ON a.id = m.apunte_id
              JOIN core.periodo pe ON pe.id = s.periodo_id AND pe.tipo = 'NORMAL'
              JOIN conta.cuenta cu ON cu.id = m.cuenta_id
              WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @origen AND m.canal_base_id = @canal
                AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE') AND left(cu.codigo, 1) IN ('6', '7')),
            imp AS (
              SELECT aa.apunte_id, aa.elemento_id,
                     CASE WHEN mov.debe = mov.ad THEN aa.debe ELSE ROUND(mov.debe * aa.porcentaje / 100, 2) END AS debe,
                     CASE WHEN mov.haber = mov.ah THEN aa.haber ELSE ROUND(mov.haber * aa.porcentaje / 100, 2) END AS haber
              FROM mov
              JOIN gest.apunte_analitica aa ON aa.apunte_id = mov.apunte_id
              JOIN gest.elemento_analitico e ON e.id = aa.elemento_id AND e.dimension_id = @dim)
            SELECT mov.cuenta, imp.elemento_id, mov.periodo, SUM(imp.debe) AS debe, SUM(imp.haber) AS haber
            FROM mov JOIN imp ON imp.apunte_id = mov.apunte_id
            GROUP BY mov.cuenta, imp.elemento_id, mov.periodo
            UNION ALL
            SELECT mov.cuenta, NULL, mov.periodo, SUM(mov.debe - COALESCE(t.debe, 0)), SUM(mov.haber - COALESCE(t.haber, 0))
            FROM mov LEFT JOIN (SELECT apunte_id, SUM(debe) AS debe, SUM(haber) AS haber FROM imp GROUP BY apunte_id) t ON t.apunte_id = mov.apunte_id
            GROUP BY mov.cuenta, mov.periodo
            """, new { empresaId, origen = cmd.EjercicioOrigenId, canal = c.CanalBaseId, digitos = cmd.Digitos, dim = cmd.DimensionId ?? 0 },
            tx, cancellationToken: ct))).AsList();

        var factor = 1 + cmd.IncrementoPorcentaje / 100m;
        var lineas = movs.GroupBy(m => (m.Cuenta, m.ElementoId)).Select(g =>
        {
            var imp = new decimal[n];
            foreach (var m in g)
                imp[Math.Clamp(m.Periodo, 1, n) - 1] += Presupuestos.Real(m.Cuenta, m.Debe, m.Haber);
            return (g.Key.Cuenta, g.Key.ElementoId, imp.Select(x => Math.Round(x * factor, 2, MidpointRounding.AwayFromZero)).ToArray());
        }).Where(l => l.Item3.Any(x => x != 0)).OrderBy(l => l.Cuenta).ToList();

        await GrabarAsync(cn, tx, presupuestoId, lineas, ct);
        await tx.CommitAsync(ct);
    }

    public async Task CambiarEstadoAsync(long empresaId, long presupuestoId, string estado, CancellationToken ct)
    {
        estado = (estado ?? "").Trim().ToUpperInvariant();
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var c = await CabeceraAsync(cn, tx, empresaId, presupuestoId, ct, bloquear: true);
        var valido = (c.Estado, estado) is ("BORRADOR", "APROBADO") or ("APROBADO", "BORRADOR") or ("APROBADO", "CERRADO");
        if (!valido) throw new ReglaContableException($"No se puede pasar de {c.Estado} a {estado}.",
            "BORRADOR → APROBADO → CERRADO; un aprobado puede volver a borrador.");
        await cn.ExecuteAsync(new CommandDefinition("UPDATE gest.presupuesto SET estado = @estado WHERE id = @presupuestoId",
            new { estado, presupuestoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<long> NuevaVersionAsync(long empresaId, long presupuestoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var c = await CabeceraAsync(cn, tx, empresaId, presupuestoId, ct);
        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO gest.presupuesto (empresa_id, ejercicio_id, codigo, nombre, version, canal_base_id)
            SELECT empresa_id, ejercicio_id, codigo, nombre,
                   (SELECT MAX(version) + 1 FROM gest.presupuesto x WHERE x.empresa_id = p.empresa_id AND x.ejercicio_id = p.ejercicio_id
                                                                      AND x.codigo = p.codigo),
                   canal_base_id
            FROM gest.presupuesto p WHERE p.id = @presupuestoId
            RETURNING id
            """, new { presupuestoId = c.Id }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO gest.presupuesto_linea (presupuesto_id, cuenta_patron, elemento_id, actividad_id, periodo_numero, importe)
            SELECT @id, cuenta_patron, elemento_id, actividad_id, periodo_numero, importe FROM gest.presupuesto_linea WHERE presupuesto_id = @presupuestoId
            """, new { id, presupuestoId = c.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task EliminarAsync(long empresaId, long presupuestoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var c = await CabeceraAsync(cn, tx, empresaId, presupuestoId, ct, bloquear: true);
        ExigeBorrador(c);
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM gest.presupuesto WHERE id = @presupuestoId", new { presupuestoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Presupuesto frente a real hasta un periodo (por defecto, todo el ejercicio). Lo real sale de los asientos contabilizados
    /// del ejercicio y canal del presupuesto, sin apertura, regularización ni cierre; una línea con elemento solo toma lo
    /// imputado a ese elemento o a sus hijos.
    /// </summary>
    public async Task<ComparativaPresupuestoDto> ComparativaAsync(long empresaId, long presupuestoId, int? hastaPeriodo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var p = await CompletoAsync(cn, empresaId, presupuestoId, ct);
        var periodos = await PeriodosAsync(cn, null, p.EjercicioId, ct);
        var n = periodos.Count;
        var hasta = Math.Clamp(hastaPeriodo ?? n, 1, n);
        var idx = periodos.Select((x, i) => (x.Numero, i)).ToDictionary(x => x.Numero, x => x.i);

        var real = (await cn.QueryAsync<RealFila>(new CommandDefinition("""
            WITH RECURSIVE l AS (
              SELECT DISTINCT cuenta_patron, elemento_id FROM gest.presupuesto_linea WHERE presupuesto_id = @presupuestoId),
            des AS (
              SELECT e.id AS raiz, e.id FROM gest.elemento_analitico e WHERE e.id IN (SELECT elemento_id FROM l)
              UNION ALL
              SELECT des.raiz, h.id FROM des JOIN gest.elemento_analitico h ON h.padre_id = des.id),
            mov AS (
              SELECT m.apunte_id, cu.codigo, pe.numero AS periodo, m.debe, m.haber, a.debe AS ad, a.haber AS ah
              FROM conta.v_mayor m
              JOIN conta.asiento s ON s.id = m.asiento_id
              JOIN conta.apunte a ON a.id = m.apunte_id
              JOIN core.periodo pe ON pe.id = s.periodo_id AND pe.tipo = 'NORMAL'
              JOIN conta.cuenta cu ON cu.id = m.cuenta_id
              WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND m.canal_base_id = @canal
                AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE')),
            x AS (
              SELECT des.raiz, aa.apunte_id,
                     SUM(CASE WHEN mov.debe = mov.ad THEN aa.debe ELSE ROUND(mov.debe * aa.porcentaje / 100, 2) END) AS debe,
                     SUM(CASE WHEN mov.haber = mov.ah THEN aa.haber ELSE ROUND(mov.haber * aa.porcentaje / 100, 2) END) AS haber
              FROM des
              JOIN gest.apunte_analitica aa ON aa.elemento_id = des.id
              JOIN mov ON mov.apunte_id = aa.apunte_id
              GROUP BY des.raiz, aa.apunte_id)
            SELECT l.cuenta_patron, l.elemento_id, mov.periodo,
                   SUM(CASE WHEN l.elemento_id IS NULL THEN mov.debe ELSE x.debe END) AS debe,
                   SUM(CASE WHEN l.elemento_id IS NULL THEN mov.haber ELSE x.haber END) AS haber
            FROM l
            JOIN mov ON mov.codigo LIKE l.cuenta_patron || '%'
            LEFT JOIN x ON x.raiz = l.elemento_id AND x.apunte_id = mov.apunte_id
            WHERE l.elemento_id IS NULL OR x.apunte_id IS NOT NULL
            GROUP BY l.cuenta_patron, l.elemento_id, mov.periodo
            """, new { presupuestoId, empresaId, ejercicioId = p.EjercicioId, canal = p.CanalBaseId }, cancellationToken: ct))).AsList();

        var r = new ComparativaPresupuestoDto { Presupuesto = p, HastaPeriodo = hasta };
        foreach (var l in p.Lineas)
        {
            var rp = new decimal[n];
            foreach (var m in real.Where(m => m.CuentaPatron == l.CuentaPatron && m.ElementoId == l.ElementoId))
                if (idx.TryGetValue(m.Periodo, out var i)) rp[i] += Presupuestos.Real(l.CuentaPatron, m.Debe, m.Haber);
            var pres = l.Importes.Take(hasta).Sum();
            var rea = rp.Take(hasta).Sum();
            r.Filas.Add(new FilaComparativaDto
            {
                CuentaPatron = l.CuentaPatron, CuentaNombre = l.CuentaNombre, Elemento = l.Elemento, Presupuesto = pres, Real = rea,
                Ejecucion = Presupuestos.Ejecucion(pres, rea), Favorable = Presupuestos.Favorable(l.CuentaPatron, pres, rea),
                PresupuestoAnual = l.Total, PresupuestoPeriodos = l.Importes, RealPeriodos = rp
            });
        }
        r.IngresosPresupuesto = r.Filas.Where(f => f.CuentaPatron.StartsWith('7')).Sum(f => f.Presupuesto);
        r.IngresosReal = r.Filas.Where(f => f.CuentaPatron.StartsWith('7')).Sum(f => f.Real);
        r.GastosPresupuesto = r.Filas.Where(f => f.CuentaPatron.StartsWith('6')).Sum(f => f.Presupuesto);
        r.GastosReal = r.Filas.Where(f => f.CuentaPatron.StartsWith('6')).Sum(f => f.Real);
        var solapados = Presupuestos.Solapados(p.Lineas.Select(l => (l.CuentaPatron, l.ElementoId)));
        if (solapados.Count > 0)
            r.Avisos.Add("Hay cuentas que se solapan y lo real se cuenta dos veces en los totales: " + string.Join(", ", solapados) + ".");
        return r;
    }
}
