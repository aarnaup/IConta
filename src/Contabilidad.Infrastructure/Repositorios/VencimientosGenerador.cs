using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

/// <summary>Crea los vencimientos de una factura recién contabilizada, dentro de la misma transacción.</summary>
public static class VencimientosGenerador
{
    private sealed class FacturaFila
    {
        public string Libro { get; set; } = "";
        public long TerceroId { get; set; }
        public decimal Total { get; set; }
        public DateOnly FechaExpedicion { get; set; }
        public long? FormaPagoTercero { get; set; }
    }
    private sealed class FormaFila { public short NumPlazos { get; set; } public short DiasPrimerPlazo { get; set; } public short DiasEntrePlazos { get; set; } }

    /// <returns>Número de vencimientos creados (0 si el total no es positivo, p. ej. una rectificativa).</returns>
    public static async Task<int> GenerarAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long facturaId,
        long? formaPagoId, CancellationToken ct)
    {
        var f = await cn.QuerySingleAsync<FacturaFila>(new CommandDefinition("""
            SELECT f.libro, f.tercero_id, f.total, f.fecha_expedicion, t.forma_pago_id AS forma_pago_tercero
            FROM fiscal.factura f JOIN conta.tercero t ON t.id = f.tercero_id
            WHERE f.id = @facturaId AND f.empresa_id = @empresaId
            """, new { facturaId, empresaId }, tx, cancellationToken: ct));

        // Una rectificativa (total negativo) no genera cobro ni pago: se compensa a mano con los vencimientos de la original
        if (f.Total <= 0) return 0;

        var elegida = formaPagoId ?? f.FormaPagoTercero;
        FormaFila forma = new() { NumPlazos = 1 };
        if (elegida is long id)
            forma = await cn.QuerySingleOrDefaultAsync<FormaFila>(new CommandDefinition("""
                SELECT num_plazos, dias_primer_plazo, dias_entre_plazos FROM core.forma_pago
                WHERE id = @id AND empresa_id = @empresaId
                """, new { id, empresaId }, tx, cancellationToken: ct))
                ?? throw new ReglaContableException("La forma de pago no pertenece a la empresa.");

        var plazos = CalculadorVencimientos.Generar(f.Total, f.FechaExpedicion, forma.NumPlazos, forma.DiasPrimerPlazo, forma.DiasEntrePlazos);
        var tipo = f.Libro == "EMITIDAS" ? "COBRO" : "PAGO";

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tes.vencimiento
              (empresa_id, tipo, factura_id, tercero_id, forma_pago_id, fecha_vencimiento, importe, importe_pendiente, num_plazo, total_plazos)
            VALUES (@empresaId, @tipo, @facturaId, @terceroId, @formaPagoId, @fecha, @importe, @importe, @numero, @total)
            """, plazos.Select(p => new
        {
            empresaId, tipo, facturaId, terceroId = f.TerceroId, formaPagoId = elegida,
            fecha = p.Fecha, importe = p.Importe, numero = p.Numero, total = p.Total
        }), tx, cancellationToken: ct));
        return plazos.Count;
    }
}
