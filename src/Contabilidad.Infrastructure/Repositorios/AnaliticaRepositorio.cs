using Contabilidad.Domain;
using Contabilidad.Domain.Analitica;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IAnaliticaRepositorio
{
    Task<IReadOnlyList<DimensionAnaliticaDto>> DimensionesAsync(long empresaId, CancellationToken ct);
    Task<long> CrearDimensionAsync(long empresaId, DimensionCmd cmd, CancellationToken ct);
    Task ModificarDimensionAsync(long empresaId, long dimensionId, DimensionCmd cmd, CancellationToken ct);
    Task EliminarDimensionAsync(long empresaId, long dimensionId, CancellationToken ct);
    Task<long> CrearElementoAsync(long empresaId, long dimensionId, ElementoCmd cmd, CancellationToken ct);
    Task ModificarElementoAsync(long empresaId, long elementoId, ElementoCmd cmd, CancellationToken ct);
    Task EliminarElementoAsync(long empresaId, long elementoId, CancellationToken ct);

    Task<IReadOnlyList<RepartoCuentaDto>> RepartosAsync(long empresaId, CancellationToken ct);
    Task GuardarRepartoAsync(long empresaId, GuardarRepartoCuentaCmd cmd, CancellationToken ct);

    Task<ApunteAnaliticaDto> ApunteAsync(long empresaId, long apunteId, CancellationToken ct);
    Task ImputarApunteAsync(long empresaId, long apunteId, IReadOnlyList<ImputacionCmd> reparto, CancellationToken ct);
    Task<IReadOnlyList<ApunteAnaliticaDto>> SinImputarAsync(long empresaId, long ejercicioId, long dimensionId, DateOnly? desde, DateOnly? hasta,
        CancellationToken ct);
    Task<InformeAnaliticoDto> InformeAsync(long empresaId, long ejercicioId, long dimensionId, long? canalId, DateOnly? desde, DateOnly? hasta,
        bool detalle, CancellationToken ct);
}

/// <summary>Apunte recién grabado y la analítica que trae (null: la de la cuenta por defecto).</summary>
internal sealed record ApunteAImputar(long ApunteId, long CuentaId, decimal Debe, decimal Haber, IReadOnlyList<ImputacionCmd>? Analitica);

/// <summary>Graba la analítica de los apuntes de un asiento dentro de su transacción.</summary>
internal static class ImputadorAnalitico
{
    private sealed class ElementoFila
    {
        public long Id { get; set; }
        public long DimensionId { get; set; }
        public bool Activo { get; set; }
        public string Codigo { get; set; } = "";
    }

    private sealed class DefectoFila
    {
        public long CuentaId { get; set; }
        public string CuentaDefecto { get; set; } = "";
        public string Cuenta { get; set; } = "";
        public long ElementoId { get; set; }
        public long DimensionId { get; set; }
        public decimal Porcentaje { get; set; }
    }

    private sealed class CuentaFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public bool Requiere { get; set; } }

    /// <param name="exigir">Comprueba las cuentas que exigen analítica (asientos tecleados); los automáticos solo aplican el reparto por defecto.</param>
    public static async Task ImputarAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, IReadOnlyList<ApunteAImputar> apuntes,
        bool exigir, CancellationToken ct)
    {
        if (apuntes.Count == 0) return;

        // Elementos indicados a mano: de la empresa y activos
        var indicados = apuntes.Where(a => a.Analitica is { Count: > 0 }).SelectMany(a => a.Analitica!).Select(i => i.ElementoId).Distinct().ToArray();
        var elementos = indicados.Length == 0 ? [] : (await cn.QueryAsync<ElementoFila>(new CommandDefinition("""
            SELECT e.id, e.dimension_id, e.activo, e.codigo FROM gest.elemento_analitico e
            JOIN gest.dimension_analitica d ON d.id = e.dimension_id
            WHERE d.empresa_id = @empresaId AND e.id = ANY(@indicados)
            """, new { empresaId, indicados }, tx, cancellationToken: ct))).ToDictionary(e => e.Id);
        foreach (var id in indicados)
        {
            if (!elementos.TryGetValue(id, out var e)) throw new ReglaContableException("Algún elemento analítico no es de la empresa.");
            if (!e.Activo) throw new ReglaContableException($"El elemento analítico {e.Codigo} está dado de baja.");
        }

        // Reparto por defecto de cada cuenta (el de la cuenta más concreta por dimensión) y cuentas que exigen analítica
        var cuentaIds = apuntes.Select(a => a.CuentaId).Distinct().ToArray();
        var defectos = (await cn.QueryAsync<DefectoFila>(new CommandDefinition("""
            SELECT c.id AS cuenta_id, c.codigo AS cuenta, cd.codigo AS cuenta_defecto, d.elemento_id, e.dimension_id, d.porcentaje
            FROM conta.cuenta c
            JOIN conta.cuenta cd ON cd.empresa_id = c.empresa_id AND c.codigo LIKE cd.codigo || '%'
            JOIN gest.cuenta_analitica_defecto d ON d.cuenta_id = cd.id
            JOIN gest.elemento_analitico e ON e.id = d.elemento_id AND e.activo
            WHERE c.id = ANY(@cuentaIds)
            """, new { cuentaIds }, tx, cancellationToken: ct))).AsList();
        var cuentas = (await cn.QueryAsync<CuentaFila>(new CommandDefinition("""
            SELECT c.id, c.codigo,
                   EXISTS (SELECT 1 FROM conta.cuenta p WHERE p.empresa_id = c.empresa_id AND c.codigo LIKE p.codigo || '%'
                           AND p.requiere_analitica) AS requiere
            FROM conta.cuenta c WHERE c.id = ANY(@cuentaIds)
            """, new { cuentaIds }, tx, cancellationToken: ct))).ToDictionary(c => c.Id);

        var filas = new List<object>();
        foreach (var a in apuntes)
        {
            IReadOnlyList<ImputacionElemento> imp = a.Analitica is { Count: > 0 }
                ? a.Analitica.Select(i => new ImputacionElemento(i.ElementoId, elementos[i.ElementoId].DimensionId, i.Porcentaje)).ToList()
                : RepartoAnalitico.PorDefecto(cuentas[a.CuentaId].Codigo,
                    defectos.Where(d => d.CuentaId == a.CuentaId)
                            .Select(d => (d.CuentaDefecto, new ImputacionElemento(d.ElementoId, d.DimensionId, d.Porcentaje))));
            if (imp.Count == 0)
            {
                if (exigir && cuentas[a.CuentaId].Requiere)
                    throw new ReglaContableException($"La cuenta {cuentas[a.CuentaId].Codigo} exige analítica.",
                        "Indica el reparto en el apunte o define un reparto por defecto para la cuenta.");
                continue;
            }
            foreach (var p in RepartoAnalitico.Repartir(a.Debe, a.Haber, imp))
                filas.Add(new { apunteId = a.ApunteId, elementoId = p.ElementoId, porcentaje = p.Porcentaje, debe = p.Debe, haber = p.Haber });
        }
        if (filas.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO gest.apunte_analitica (apunte_id, elemento_id, porcentaje, debe, haber)
                VALUES (@apunteId, @elementoId, @porcentaje, @debe, @haber)
                """, filas, tx, cancellationToken: ct));
    }
}

/// <summary>
/// Contabilidad analítica: dimensiones libres (centros de coste, proyectos, departamentos…) con elementos jerárquicos, reparto
/// por defecto por cuenta y el informe de ingresos y gastos por elemento. Es información de gestión: imputar o cambiar la
/// analítica de un apunte no toca el asiento.
/// </summary>
public sealed class AnaliticaRepositorio(IConexionFactory conexiones) : IAnaliticaRepositorio
{
    private sealed class DimFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public string Nombre { get; set; } = ""; }

    private sealed class ImputacionFila
    {
        public long ApunteId { get; set; }
        public long ElementoId { get; set; }
        public string Dimension { get; set; } = "";
        public string Elemento { get; set; } = "";
        public string ElementoNombre { get; set; } = "";
        public decimal Porcentaje { get; set; }
        public decimal Debe { get; set; }
        public decimal Haber { get; set; }
    }

    private sealed class ApunteFila
    {
        public long ApunteId { get; set; }
        public long AsientoId { get; set; }
        public int Numero { get; set; }
        public DateOnly Fecha { get; set; }
        public string Cuenta { get; set; } = "";
        public string CuentaNombre { get; set; } = "";
        public string? Concepto { get; set; }
        public decimal Debe { get; set; }
        public decimal Haber { get; set; }
        public long CuentaId { get; set; }
    }

    private sealed class MovFila
    {
        public long? ElementoId { get; set; }
        public string Cuenta { get; set; } = "";
        public string CuentaNombre { get; set; } = "";
        public decimal Debe { get; set; }
        public decimal Haber { get; set; }
    }

    private sealed class EjFila { public DateOnly FechaInicio { get; set; } public DateOnly FechaFin { get; set; } }

    // ---------------------------------------------------------------- dimensiones y elementos
    public async Task<IReadOnlyList<DimensionAnaliticaDto>> DimensionesAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var dims = (await cn.QueryAsync<DimensionAnaliticaDto>(new CommandDefinition(
            "SELECT id, codigo, nombre FROM gest.dimension_analitica WHERE empresa_id = @empresaId ORDER BY codigo",
            new { empresaId }, cancellationToken: ct))).AsList();
        var els = (await cn.QueryAsync<ElementoAnaliticoDto>(new CommandDefinition("""
            SELECT e.id, e.dimension_id, e.padre_id, e.codigo, e.nombre, e.activo
            FROM gest.elemento_analitico e JOIN gest.dimension_analitica d ON d.id = e.dimension_id
            WHERE d.empresa_id = @empresaId ORDER BY e.codigo
            """, new { empresaId }, cancellationToken: ct))).AsList();
        foreach (var d in dims) d.Elementos = els.Where(e => e.DimensionId == d.Id).ToList();
        return dims;
    }

    private static (string Codigo, string Nombre) Limpia(string? codigo, string? nombre, int max)
    {
        var c = (codigo ?? "").Trim().ToUpperInvariant();
        var n = (nombre ?? "").Trim();
        if (c.Length == 0 || n.Length == 0) throw new ReglaContableException("Código y nombre son obligatorios.");
        if (c.Length > max) throw new ReglaContableException($"El código admite como mucho {max} caracteres.");
        if (c.Any(ch => char.IsWhiteSpace(ch) || ch is ':' or ',' or ';'))
            throw new ReglaContableException("El código no puede llevar espacios, dos puntos, comas ni punto y coma.");
        return (c, n.Length > 150 ? n[..150] : n);
    }

    public async Task<long> CrearDimensionAsync(long empresaId, DimensionCmd cmd, CancellationToken ct)
    {
        var (c, n) = Limpia(cmd.Codigo, cmd.Nombre, 10);
        await using var cn = await conexiones.AbrirAsync(ct);
        try
        {
            return await cn.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO gest.dimension_analitica (empresa_id, codigo, nombre) VALUES (@empresaId, @c, left(@n, 100)) RETURNING id",
                new { empresaId, c, n }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe la dimensión {c}.");
        }
    }

    public async Task ModificarDimensionAsync(long empresaId, long dimensionId, DimensionCmd cmd, CancellationToken ct)
    {
        var (c, n) = Limpia(cmd.Codigo, cmd.Nombre, 10);
        await using var cn = await conexiones.AbrirAsync(ct);
        try
        {
            var k = await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE gest.dimension_analitica SET codigo = @c, nombre = left(@n, 100) WHERE id = @dimensionId AND empresa_id = @empresaId",
                new { c, n, dimensionId, empresaId }, cancellationToken: ct));
            if (k == 0) throw new ReglaContableException("La dimensión no existe en la empresa.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe la dimensión {c}.");
        }
    }

    public async Task EliminarDimensionAsync(long empresaId, long dimensionId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM gest.dimension_analitica WHERE id = @dimensionId AND empresa_id = @empresaId)",
            new { dimensionId, empresaId }, tx, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("La dimensión no existe en la empresa.");
        if (await EnUsoAsync(cn, tx, "e.dimension_id = @id", dimensionId, ct))
            throw new ReglaContableException("La dimensión tiene apuntes o presupuestos imputados: da de baja sus elementos en lugar de borrarla.");
        await cn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM gest.cuenta_analitica_defecto WHERE elemento_id IN (SELECT id FROM gest.elemento_analitico WHERE dimension_id = @dimensionId);
            UPDATE gest.elemento_analitico SET padre_id = NULL WHERE dimension_id = @dimensionId;
            DELETE FROM gest.elemento_analitico WHERE dimension_id = @dimensionId;
            DELETE FROM gest.dimension_analitica WHERE id = @dimensionId;
            """, new { dimensionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static Task<bool> EnUsoAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string filtro, long id, CancellationToken ct)
        => cn.ExecuteScalarAsync<bool>(new CommandDefinition($"""
            SELECT EXISTS (SELECT 1 FROM gest.apunte_analitica a JOIN gest.elemento_analitico e ON e.id = a.elemento_id WHERE {filtro})
                OR EXISTS (SELECT 1 FROM gest.presupuesto_linea a JOIN gest.elemento_analitico e ON e.id = a.elemento_id WHERE {filtro})
            """, new { id }, tx, cancellationToken: ct));

    private static async Task ValidarPadreAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long dimensionId, long? elementoId, long? padreId,
        CancellationToken ct)
    {
        if (padreId is null) return;
        var padres = (await cn.QueryAsync<(long Id, long? PadreId, long DimensionId)>(new CommandDefinition(
            "SELECT id, padre_id, dimension_id FROM gest.elemento_analitico WHERE dimension_id = @dimensionId",
            new { dimensionId }, tx, cancellationToken: ct))).ToDictionary(x => x.Id);
        if (!padres.ContainsKey(padreId.Value)) throw new ReglaContableException("El elemento padre no es de la misma dimensión.");
        // Sin ciclos: subiendo desde el padre no se puede llegar al propio elemento
        for (long? p = padreId; p is not null; p = padres[p.Value].PadreId)
            if (p == elementoId) throw new ReglaContableException("Un elemento no puede colgar de sí mismo ni de uno de sus hijos.");
    }

    public async Task<long> CrearElementoAsync(long empresaId, long dimensionId, ElementoCmd cmd, CancellationToken ct)
    {
        var (c, n) = Limpia(cmd.Codigo, cmd.Nombre, 20);
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM gest.dimension_analitica WHERE id = @dimensionId AND empresa_id = @empresaId)",
            new { dimensionId, empresaId }, tx, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("La dimensión no existe en la empresa.");
        await ValidarPadreAsync(cn, tx, dimensionId, null, cmd.PadreId, ct);
        try
        {
            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO gest.elemento_analitico (dimension_id, padre_id, codigo, nombre, activo)
                VALUES (@dimensionId, @padreId, @c, @n, @activo) RETURNING id
                """, new { dimensionId, padreId = cmd.PadreId, c, n, activo = cmd.Activo }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe el elemento {c} en la dimensión.");
        }
    }

    public async Task ModificarElementoAsync(long empresaId, long elementoId, ElementoCmd cmd, CancellationToken ct)
    {
        var (c, n) = Limpia(cmd.Codigo, cmd.Nombre, 20);
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var dimensionId = await DimensionDeAsync(cn, tx, empresaId, elementoId, ct);
        await ValidarPadreAsync(cn, tx, dimensionId, elementoId, cmd.PadreId, ct);
        try
        {
            await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE gest.elemento_analitico SET codigo = @c, nombre = @n, padre_id = @padreId, activo = @activo WHERE id = @elementoId",
                new { c, n, padreId = cmd.PadreId, activo = cmd.Activo, elementoId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe el elemento {c} en la dimensión.");
        }
    }

    public async Task EliminarElementoAsync(long empresaId, long elementoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await DimensionDeAsync(cn, tx, empresaId, elementoId, ct);
        if (await EnUsoAsync(cn, tx, "e.id = @id", elementoId, ct))
            throw new ReglaContableException("El elemento tiene apuntes o presupuestos imputados: dalo de baja en lugar de borrarlo.");
        if (await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM gest.elemento_analitico WHERE padre_id = @elementoId)", new { elementoId }, tx, cancellationToken: ct)))
            throw new ReglaContableException("El elemento tiene elementos hijos.");
        await cn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM gest.cuenta_analitica_defecto WHERE elemento_id = @elementoId;
            DELETE FROM gest.elemento_analitico WHERE id = @elementoId;
            """, new { elementoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static async Task<long> DimensionDeAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long elementoId, CancellationToken ct)
        => await cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT e.dimension_id FROM gest.elemento_analitico e JOIN gest.dimension_analitica d ON d.id = e.dimension_id
            WHERE e.id = @elementoId AND d.empresa_id = @empresaId
            """, new { elementoId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El elemento analítico no existe en la empresa.");

    // ---------------------------------------------------------------- reparto por defecto por cuenta
    public async Task<IReadOnlyList<RepartoCuentaDto>> RepartosAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var cuentas = (await cn.QueryAsync<RepartoCuentaDto>(new CommandDefinition("""
            SELECT c.codigo AS cuenta, c.nombre AS cuenta_nombre, c.requiere_analitica
            FROM conta.cuenta c
            WHERE c.empresa_id = @empresaId
              AND (c.requiere_analitica OR EXISTS (SELECT 1 FROM gest.cuenta_analitica_defecto d WHERE d.cuenta_id = c.id))
            ORDER BY c.codigo
            """, new { empresaId }, cancellationToken: ct))).AsList();
        var imp = (await cn.QueryAsync<(string Cuenta, long ElementoId, string Dimension, string Elemento, string ElementoNombre, decimal Porcentaje)>(
            new CommandDefinition("""
            SELECT c.codigo, e.id, dm.codigo, e.codigo, e.nombre, d.porcentaje
            FROM gest.cuenta_analitica_defecto d
            JOIN conta.cuenta c ON c.id = d.cuenta_id
            JOIN gest.elemento_analitico e ON e.id = d.elemento_id
            JOIN gest.dimension_analitica dm ON dm.id = e.dimension_id
            WHERE c.empresa_id = @empresaId ORDER BY dm.codigo, e.codigo
            """, new { empresaId }, cancellationToken: ct))).AsList();
        foreach (var c in cuentas)
            c.Reparto = imp.Where(i => i.Cuenta == c.Cuenta).Select(i => new ImputacionDto
            {
                ElementoId = i.ElementoId, Dimension = i.Dimension, Elemento = i.Elemento, ElementoNombre = i.ElementoNombre, Porcentaje = i.Porcentaje
            }).ToList();
        return cuentas;
    }

    public async Task GuardarRepartoAsync(long empresaId, GuardarRepartoCuentaCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var cuentaId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM conta.cuenta WHERE empresa_id = @empresaId AND codigo = @codigo",
            new { empresaId, codigo = (cmd.Cuenta ?? "").Trim() }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException($"La cuenta {cmd.Cuenta} no existe en el plan de la empresa.");
        var imp = await ImputacionesAsync(cn, tx, empresaId, cmd.Reparto, ct);
        RepartoAnalitico.Validar(imp);

        await cn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM gest.cuenta_analitica_defecto WHERE cuenta_id = @cuentaId;
            UPDATE conta.cuenta SET requiere_analitica = @requiere WHERE id = @cuentaId;
            """, new { cuentaId, requiere = cmd.RequiereAnalitica }, tx, cancellationToken: ct));
        if (imp.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO gest.cuenta_analitica_defecto (cuenta_id, elemento_id, porcentaje) VALUES (@cuentaId, @ElementoId, @Porcentaje)",
                imp.Select(i => new { cuentaId, i.ElementoId, i.Porcentaje }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static async Task<List<ImputacionElemento>> ImputacionesAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId,
        IReadOnlyList<ImputacionCmd> reparto, CancellationToken ct)
    {
        var ids = reparto.Select(r => r.ElementoId).Distinct().ToArray();
        var dims = (await cn.QueryAsync<(long Id, long DimensionId, bool Activo)>(new CommandDefinition("""
            SELECT e.id, e.dimension_id, e.activo FROM gest.elemento_analitico e JOIN gest.dimension_analitica d ON d.id = e.dimension_id
            WHERE d.empresa_id = @empresaId AND e.id = ANY(@ids)
            """, new { empresaId, ids }, tx, cancellationToken: ct))).ToDictionary(x => x.Id);
        if (dims.Count != ids.Length) throw new ReglaContableException("Algún elemento analítico no es de la empresa.");
        if (dims.Values.Any(d => !d.Activo)) throw new ReglaContableException("Algún elemento analítico está dado de baja.");
        return reparto.Select(r => new ImputacionElemento(r.ElementoId, dims[r.ElementoId].DimensionId, r.Porcentaje)).ToList();
    }

    // ---------------------------------------------------------------- imputación de apuntes
    private const string SelectApunte = """
        SELECT a.id AS apunte_id, s.id AS asiento_id, s.numero, s.fecha, c.codigo AS cuenta, c.nombre AS cuenta_nombre,
               COALESCE(a.concepto, s.concepto) AS concepto, a.debe, a.haber, a.cuenta_id
        FROM conta.apunte a
        JOIN conta.asiento s ON s.id = a.asiento_id
        JOIN conta.cuenta c ON c.id = a.cuenta_id
        """;

    private static async Task<List<ImputacionFila>> ImputacionesDeAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long[] apunteIds, CancellationToken ct)
        => (await cn.QueryAsync<ImputacionFila>(new CommandDefinition("""
            SELECT aa.apunte_id, e.id AS elemento_id, d.codigo AS dimension, e.codigo AS elemento, e.nombre AS elemento_nombre,
                   aa.porcentaje, aa.debe, aa.haber
            FROM gest.apunte_analitica aa
            JOIN gest.elemento_analitico e ON e.id = aa.elemento_id
            JOIN gest.dimension_analitica d ON d.id = e.dimension_id
            WHERE aa.apunte_id = ANY(@apunteIds) ORDER BY d.codigo, e.codigo
            """, new { apunteIds }, tx, cancellationToken: ct))).AsList();

    private static ApunteAnaliticaDto Dto(ApunteFila f, IEnumerable<ImputacionFila> imp) => new()
    {
        ApunteId = f.ApunteId, AsientoId = f.AsientoId, Numero = f.Numero, Fecha = f.Fecha, Cuenta = f.Cuenta, CuentaNombre = f.CuentaNombre,
        Concepto = f.Concepto, Debe = f.Debe, Haber = f.Haber,
        Imputaciones = imp.Where(i => i.ApunteId == f.ApunteId).Select(i => new ImputacionDto
        {
            ElementoId = i.ElementoId, Dimension = i.Dimension, Elemento = i.Elemento, ElementoNombre = i.ElementoNombre,
            Porcentaje = i.Porcentaje, Debe = i.Debe, Haber = i.Haber
        }).ToList()
    };

    public async Task<ApunteAnaliticaDto> ApunteAsync(long empresaId, long apunteId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var f = await cn.QuerySingleOrDefaultAsync<ApunteFila>(new CommandDefinition(SelectApunte + " WHERE a.id = @apunteId AND a.empresa_id = @empresaId",
            new { apunteId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("El apunte no existe en la empresa.");
        return Dto(f, await ImputacionesDeAsync(cn, null, [apunteId], ct));
    }

    /// <summary>
    /// Sustituye la analítica de un apunte. Lista vacía: lo deja sin imputar. Se puede hacer en ejercicios cerrados porque no
    /// cambia la contabilidad, solo la información de gestión.
    /// </summary>
    public async Task ImputarApunteAsync(long empresaId, long apunteId, IReadOnlyList<ImputacionCmd> reparto, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var f = await cn.QuerySingleOrDefaultAsync<ApunteFila>(new CommandDefinition(
            SelectApunte + " WHERE a.id = @apunteId AND a.empresa_id = @empresaId AND s.estado <> 'ANULADO' FOR UPDATE OF a",
            new { apunteId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El apunte no existe en la empresa o su asiento está anulado.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM gest.apunte_analitica WHERE apunte_id = @apunteId",
            new { apunteId }, tx, cancellationToken: ct));
        if (reparto.Count > 0)
        {
            var imp = await ImputacionesAsync(cn, tx, empresaId, reparto, ct);
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO gest.apunte_analitica (apunte_id, elemento_id, porcentaje, debe, haber)
                VALUES (@apunteId, @ElementoId, @Porcentaje, @Debe, @Haber)
                """, RepartoAnalitico.Repartir(f.Debe, f.Haber, imp).Select(p => new { apunteId, p.ElementoId, p.Porcentaje, p.Debe, p.Haber }),
                tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Apuntes de gastos e ingresos (grupos 6 y 7) del periodo sin imputar del todo en la dimensión.</summary>
    public async Task<IReadOnlyList<ApunteAnaliticaDto>> SinImputarAsync(long empresaId, long ejercicioId, long dimensionId, DateOnly? desde,
        DateOnly? hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var (d, h) = await RangoAsync(cn, empresaId, ejercicioId, desde, hasta, ct);
        await DimensionAsync(cn, empresaId, dimensionId, ct);
        var filas = (await cn.QueryAsync<ApunteFila>(new CommandDefinition(SelectApunte + """
             WHERE a.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId AND s.estado = 'CONTABILIZADO'
               AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE') AND s.fecha BETWEEN @d AND @h
               AND left(c.codigo, 1) IN ('6', '7')
               AND COALESCE((SELECT SUM(aa.porcentaje) FROM gest.apunte_analitica aa JOIN gest.elemento_analitico e ON e.id = aa.elemento_id
                             WHERE aa.apunte_id = a.id AND e.dimension_id = @dimensionId), 0) < 100
             ORDER BY s.fecha, s.numero, a.linea LIMIT 500
            """, new { empresaId, ejercicioId, dimensionId, d, h }, cancellationToken: ct))).AsList();
        var imp = await ImputacionesDeAsync(cn, null, filas.Select(f => f.ApunteId).ToArray(), ct);
        return filas.Select(f => Dto(f, imp)).ToList();
    }

    private static async Task<(DateOnly, DateOnly)> RangoAsync(NpgsqlConnection cn, long empresaId, long ejercicioId, DateOnly? desde, DateOnly? hasta,
        CancellationToken ct)
    {
        var ej = await cn.QuerySingleOrDefaultAsync<EjFila>(new CommandDefinition(
            "SELECT fecha_inicio, fecha_fin FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
            new { ejercicioId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");
        var d = desde is { } x && x > ej.FechaInicio ? x : ej.FechaInicio;
        var h = hasta is { } y && y < ej.FechaFin ? y : ej.FechaFin;
        if (d > h) throw new ReglaContableException("La fecha inicial es posterior a la final.");
        return (d, h);
    }

    private static async Task<DimFila> DimensionAsync(NpgsqlConnection cn, long empresaId, long dimensionId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<DimFila>(new CommandDefinition(
               "SELECT id, codigo, nombre FROM gest.dimension_analitica WHERE id = @dimensionId AND empresa_id = @empresaId",
               new { dimensionId, empresaId }, cancellationToken: ct))
           ?? throw new ReglaContableException("La dimensión no existe en la empresa.");

    // ---------------------------------------------------------------- informe
    /// <summary>
    /// Ingresos (grupo 7), gastos (grupo 6) y resultado por elemento de la dimensión, en un canal base (por defecto el primero).
    /// Los padres suman a sus hijos. «Sin asignar» es lo que falta por imputar de los grupos 6 y 7, así que el total coincide con
    /// el resultado de la cuenta de pérdidas y ganancias antes de impuestos y regularización. «Otros» recoge lo imputado a
    /// cuentas de otros grupos (inversiones de un proyecto, por ejemplo) como debe − haber.
    /// </summary>
    public async Task<InformeAnaliticoDto> InformeAsync(long empresaId, long ejercicioId, long dimensionId, long? canalId, DateOnly? desde,
        DateOnly? hasta, bool detalle, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var (d, h) = await RangoAsync(cn, empresaId, ejercicioId, desde, hasta, ct);
        var dim = await DimensionAsync(cn, empresaId, dimensionId, ct);
        var canales = (await cn.QueryAsync<(long Id, string Codigo)>(new CommandDefinition(
            "SELECT id, codigo FROM canal.canal WHERE empresa_id = @empresaId AND tipo = 'BASE' AND activo ORDER BY codigo",
            new { empresaId }, cancellationToken: ct))).AsList();
        if (canales.Count == 0) throw new ReglaContableException("La empresa no tiene canales base.");
        var canal = canalId is null ? canales[0] : canales.Find(x => x.Id == canalId);
        if (canal.Id == 0) throw new ReglaContableException("El canal no es un canal base de la empresa.");

        // Lo imputado sale de lo grabado en el apunte (cuadra al céntimo); si el canal base recibe otro importe (canal derivado
        // con fórmula), se recalcula con el porcentaje.
        const string mov = """
            WITH mov AS (
              SELECT m.apunte_id, c.codigo AS cuenta, c.nombre AS cuenta_nombre, m.debe, m.haber, a.debe AS ad, a.haber AS ah
              FROM conta.v_mayor m
              JOIN conta.asiento s ON s.id = m.asiento_id
              JOIN conta.apunte a ON a.id = m.apunte_id
              JOIN conta.cuenta c ON c.id = m.cuenta_id
              WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND m.canal_base_id = @canalId
                AND m.fecha BETWEEN @d AND @h AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE')),
            imp AS (
              SELECT aa.apunte_id, aa.elemento_id,
                     CASE WHEN mov.debe = mov.ad THEN aa.debe ELSE ROUND(mov.debe * aa.porcentaje / 100, 2) END AS debe,
                     CASE WHEN mov.haber = mov.ah THEN aa.haber ELSE ROUND(mov.haber * aa.porcentaje / 100, 2) END AS haber
              FROM mov
              JOIN gest.apunte_analitica aa ON aa.apunte_id = mov.apunte_id
              JOIN gest.elemento_analitico e ON e.id = aa.elemento_id AND e.dimension_id = @dimensionId)
            """;
        var p = new { empresaId, ejercicioId, canalId = canal.Id, d, h, dimensionId };
        var imputado = (await cn.QueryAsync<MovFila>(new CommandDefinition(mov + """
            SELECT imp.elemento_id, mov.cuenta, mov.cuenta_nombre, SUM(imp.debe) AS debe, SUM(imp.haber) AS haber
            FROM mov JOIN imp ON imp.apunte_id = mov.apunte_id
            GROUP BY imp.elemento_id, mov.cuenta, mov.cuenta_nombre
            """, p, cancellationToken: ct))).AsList();
        var sinAsignar = (await cn.QueryAsync<MovFila>(new CommandDefinition(mov + """
            , t AS (SELECT apunte_id, SUM(debe) AS debe, SUM(haber) AS haber FROM imp GROUP BY apunte_id),
            r AS (
              SELECT mov.cuenta, mov.cuenta_nombre, mov.debe - COALESCE(t.debe, 0) AS debe, mov.haber - COALESCE(t.haber, 0) AS haber
              FROM mov LEFT JOIN t ON t.apunte_id = mov.apunte_id
              WHERE left(mov.cuenta, 1) IN ('6', '7'))
            SELECT NULL::bigint AS elemento_id, cuenta, cuenta_nombre, SUM(debe) AS debe, SUM(haber) AS haber
            FROM r GROUP BY cuenta, cuenta_nombre HAVING SUM(debe) <> 0 OR SUM(haber) <> 0
            """, p, cancellationToken: ct))).AsList();

        var elementos = (await cn.QueryAsync<ElementoAnaliticoDto>(new CommandDefinition(
            "SELECT id, dimension_id, padre_id, codigo, nombre, activo FROM gest.elemento_analitico WHERE dimension_id = @dimensionId ORDER BY codigo",
            new { dimensionId }, cancellationToken: ct))).AsList();

        static void Suma(FilaInformeAnaliticoDto f, string cuenta, decimal debe, decimal haber)
        {
            switch (cuenta[0])
            {
                case '7': f.Ingresos += haber - debe; break;
                case '6': f.Gastos += debe - haber; break;
                default: f.Otros += debe - haber; break;
            }
        }

        // Importe propio de cada elemento y acumulado hacia sus padres
        var propio = elementos.ToDictionary(e => e.Id, _ => new FilaInformeAnaliticoDto());
        foreach (var m in imputado) Suma(propio[m.ElementoId!.Value], m.Cuenta, m.Debe, m.Haber);
        var porId = elementos.ToDictionary(e => e.Id);
        var acumulado = elementos.ToDictionary(e => e.Id, e => new FilaInformeAnaliticoDto
        {
            ElementoId = e.Id, Codigo = e.Codigo, Nombre = e.Nombre
        });
        foreach (var e in elementos)
            for (long? a = e.Id; a is not null; a = porId[a.Value].PadreId)
            {
                acumulado[a.Value].Ingresos += propio[e.Id].Ingresos;
                acumulado[a.Value].Gastos += propio[e.Id].Gastos;
                acumulado[a.Value].Otros += propio[e.Id].Otros;
            }

        var r = new InformeAnaliticoDto { Dimension = $"{dim.Codigo} · {dim.Nombre}", Canal = canal.Codigo, Desde = d, Hasta = h };
        var hijos = elementos.ToLookup(e => e.PadreId);
        void Recorre(long? padre, int nivel)
        {
            foreach (var e in hijos[padre])
            {
                var f = acumulado[e.Id];
                f.Nivel = nivel;
                if (f.Ingresos != 0 || f.Gastos != 0 || f.Otros != 0 || e.Activo) r.Filas.Add(f);
                if (detalle)
                    foreach (var g in imputado.Where(m => m.ElementoId == e.Id).OrderBy(m => m.Cuenta))
                    {
                        var c = new FilaInformeAnaliticoDto { Codigo = g.Cuenta, Nombre = g.CuentaNombre, Nivel = nivel + 1, EsCuenta = true };
                        Suma(c, g.Cuenta, g.Debe, g.Haber);
                        r.Filas.Add(c);
                    }
                Recorre(e.Id, nivel + 1);
            }
        }
        Recorre(null, 0);

        r.SinAsignar = new FilaInformeAnaliticoDto { Codigo = "—", Nombre = "Sin asignar" };
        foreach (var m in sinAsignar) Suma(r.SinAsignar, m.Cuenta, m.Debe, m.Haber);
        r.Total = new FilaInformeAnaliticoDto { Codigo = "", Nombre = "Total" };
        foreach (var f in propio.Values.Append(r.SinAsignar))
        {
            r.Total.Ingresos += f.Ingresos; r.Total.Gastos += f.Gastos; r.Total.Otros += f.Otros;
        }
        if (r.SinAsignar.Ingresos != 0 || r.SinAsignar.Gastos != 0)
            r.Avisos.Add($"Hay ingresos o gastos sin imputar en la dimensión (ingresos {r.SinAsignar.Ingresos:N2}, gastos {r.SinAsignar.Gastos:N2}).");
        return r;
    }
}
