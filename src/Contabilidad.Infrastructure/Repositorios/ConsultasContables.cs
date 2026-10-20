using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

/// <summary>Consultas que comparten la liquidación del IVA/IGIC y el cierre de ejercicio.</summary>
internal static class ConsultasContables
{
    private sealed class SaldoFila
    {
        public long CuentaId { get; set; }
        public string Codigo { get; set; } = "";
        public long CanalBaseId { get; set; }
        public decimal Saldo { get; set; }
    }

    /// <summary>
    /// Saldos (debe − haber) por cuenta y canal base de los asientos contabilizados del ejercicio hasta <paramref name="hasta"/>.
    /// <paramref name="prefijos"/> filtra por el inicio del código y <paramref name="cuentaIdsExtra"/> añade cuentas concretas;
    /// con <paramref name="prefijos"/> = null, todas las cuentas. Solo devuelve saldos distintos de cero.
    /// <paramref name="sinAsientosDeCierre"/> ignora la regularización y el cierre (saldos "antes de cerrar").
    /// </summary>
    public static async Task<List<SaldoCuenta>> SaldosAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId,
        DateOnly hasta, string[]? prefijos, long[]? cuentaIdsExtra, CancellationToken ct, bool sinAsientosDeCierre = false)
    {
        var filas = await cn.QueryAsync<SaldoFila>(new CommandDefinition("""
            SELECT m.cuenta_id, c.codigo, m.canal_base_id, SUM(m.debe - m.haber) AS saldo
            FROM conta.v_mayor m
            JOIN conta.cuenta c ON c.id = m.cuenta_id
            WHERE m.empresa_id = @empresaId AND m.ejercicio_id = @ejercicioId AND m.fecha <= @hasta
              AND (@todas OR c.codigo LIKE ANY(@prefijos::text[]) OR c.id = ANY(@extra::bigint[]))
              AND (NOT @sinCierre OR NOT EXISTS (SELECT 1 FROM conta.asiento s WHERE s.id = m.asiento_id
                                                 AND s.origen IN ('REGULARIZACION', 'CIERRE')))
            GROUP BY m.cuenta_id, c.codigo, m.canal_base_id
            HAVING SUM(m.debe - m.haber) <> 0
            ORDER BY m.canal_base_id, c.codigo
            """, new
        {
            empresaId, ejercicioId, hasta, todas = prefijos is null, sinCierre = sinAsientosDeCierre,
            prefijos = prefijos?.Select(p => p + "%").ToArray() ?? [],
            extra = cuentaIdsExtra ?? []
        }, tx, cancellationToken: ct));
        return filas.Select(f => new SaldoCuenta(f.CuentaId, f.Codigo, f.CanalBaseId, f.Saldo)).ToList();
    }

    /// <summary>
    /// Cuenta configurada para un rol en conta.cuenta_automatica (sin tipo de IVA ni retención) o, si no hay,
    /// la subcuenta del PGC con ese código rellenado con ceros (p. ej. 4750 → 47500000). Null si no existe ninguna.
    /// </summary>
    public static Task<long?> CuentaPorRolAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, string rol, string codigoPgc,
        CancellationToken ct)
        => cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT id FROM (
              SELECT ca.cuenta_id AS id, 1 AS prioridad
              FROM conta.cuenta_automatica ca
              WHERE ca.empresa_id = @empresaId AND ca.rol = @rol AND ca.tipo_iva_id IS NULL AND ca.tipo_retencion_id IS NULL
              UNION ALL
              SELECT c.id, 2
              FROM conta.cuenta c JOIN core.empresa e ON e.id = c.empresa_id
              WHERE c.empresa_id = @empresaId AND c.codigo = rpad(@codigoPgc, e.digitos_cuenta, '0') AND c.es_imputable AND c.activa
            ) x ORDER BY prioridad LIMIT 1
            """, new { empresaId, rol, codigoPgc }, tx, cancellationToken: ct));

    /// <summary>Cuentas configuradas para un rol, con o sin tipo de IVA (p. ej. todas las de IVA_SOPORTADO).</summary>
    public static async Task<long[]> CuentasDeRolAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, string rol, CancellationToken ct)
        => (await cn.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT cuenta_id FROM conta.cuenta_automatica WHERE empresa_id = @empresaId AND rol = @rol",
            new { empresaId, rol }, tx, cancellationToken: ct))).ToArray();

    /// <summary>Diario indicado (si es de la empresa) o, por defecto, el primer diario general activo.</summary>
    public static async Task<long> DiarioAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long? diarioId, CancellationToken ct)
        => diarioId ?? await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
               "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'GENERAL' ORDER BY id LIMIT 1",
               new { empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("No hay un diario general configurado.");

    /// <summary>Códigos de los canales (id → código) para mostrarlos.</summary>
    public static async Task<Dictionary<long, string>> CodigosCanalAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CancellationToken ct)
        => (await cn.QueryAsync<(long Id, string Codigo)>(new CommandDefinition(
               "SELECT id, codigo FROM canal.canal WHERE empresa_id = @empresaId", new { empresaId }, tx, cancellationToken: ct)))
           .ToDictionary(x => x.Id, x => x.Codigo);

    public static List<ApunteCmd> AApuntes(IEnumerable<ApunteGenerado> apuntes, string concepto)
        => apuntes.Select(a => new ApunteCmd { CuentaId = a.CuentaId, CanalId = a.CanalId, Debe = a.Debe, Haber = a.Haber, Concepto = concepto }).ToList();
}
