using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ITesoreriaRepositorio
{
    Task<IReadOnlyList<FormaPagoDto>> ListarFormasPagoAsync(long empresaId, CancellationToken ct);
    Task<FormaPagoDto> CrearFormaPagoAsync(long empresaId, CrearFormaPagoCmd cmd, CancellationToken ct);
    Task AsignarFormaPagoTerceroAsync(long empresaId, long terceroId, long? formaPagoId, CancellationToken ct);

    Task<IReadOnlyList<CuentaBancariaDto>> ListarCuentasBancariasAsync(long empresaId, CancellationToken ct);
    Task<CuentaBancariaDto> CrearCuentaBancariaAsync(long empresaId, CrearCuentaBancariaCmd cmd, CancellationToken ct);

    Task<IReadOnlyList<VencimientoDto>> ListarVencimientosAsync(long empresaId, FiltroVencimientos filtro, CancellationToken ct);
    Task<IReadOnlyList<PrevisionDto>> PrevisionAsync(long empresaId, DateOnly hoy, int meses, CancellationToken ct);
    Task AnularVencimientoAsync(long empresaId, long vencimientoId, CancellationToken ct);
    Task<LiquidacionCreada> LiquidarAsync(long empresaId, long ejercicioId, long usuarioId, long vencimientoId, LiquidarVencimientoCmd cmd, CancellationToken ct);

    /// <summary>Como LiquidarAsync, dentro de una transacción existente (p. ej. al cobrar desde una línea del extracto y conciliarla).</summary>
    Task<LiquidacionCreada> LiquidarEnTransaccionAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long ejercicioId, long usuarioId,
        long vencimientoId, LiquidarVencimientoCmd cmd, CancellationToken ct);
}

public sealed class TesoreriaRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : ITesoreriaRepositorio
{
    private static readonly HashSet<string> TiposForma = ["EFECTIVO", "TRANSFERENCIA", "RECIBO", "TARJETA", "PAGARE", "CHEQUE", "OTRO"];

    // ---------------------------------------------------------------- formas de pago
    public async Task<IReadOnlyList<FormaPagoDto>> ListarFormasPagoAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<FormaPagoDto>(new CommandDefinition(
            "SELECT id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos FROM core.forma_pago WHERE empresa_id = @empresaId ORDER BY codigo",
            new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<FormaPagoDto> CrearFormaPagoAsync(long empresaId, CrearFormaPagoCmd cmd, CancellationToken ct)
    {
        var codigo = (cmd.Codigo ?? "").Trim().ToUpperInvariant();
        if (codigo.Length is < 1 or > 10) throw new ReglaContableException("El código debe tener entre 1 y 10 caracteres.");
        if (string.IsNullOrWhiteSpace(cmd.Nombre)) throw new ReglaContableException("El nombre es obligatorio.");
        if (!TiposForma.Contains(cmd.Tipo)) throw new ReglaContableException("Tipo de forma de pago no válido.");
        // Valida plazos y días con la misma regla que se usará al generar vencimientos
        CalculadorVencimientos.Generar(1m * Math.Max(cmd.NumPlazos, (short)1), new DateOnly(2000, 1, 1), cmd.NumPlazos, cmd.DiasPrimerPlazo, cmd.DiasEntrePlazos);

        await using var cn = await conexiones.AbrirAsync(ct);
        try
        {
            return await cn.QuerySingleAsync<FormaPagoDto>(new CommandDefinition("""
                INSERT INTO core.forma_pago (empresa_id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos)
                VALUES (@empresaId, @codigo, @nombre, @tipo, @numPlazos, @diasPrimerPlazo, @diasEntrePlazos)
                RETURNING id, codigo, nombre, tipo, num_plazos, dias_primer_plazo, dias_entre_plazos
                """, new { empresaId, codigo, nombre = cmd.Nombre.Trim(), cmd.Tipo, cmd.NumPlazos, cmd.DiasPrimerPlazo, cmd.DiasEntrePlazos }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe una forma de pago con el código {codigo}.");
        }
    }

    public async Task AsignarFormaPagoTerceroAsync(long empresaId, long terceroId, long? formaPagoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        if (formaPagoId is not null && !await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM core.forma_pago WHERE id = @formaPagoId AND empresa_id = @empresaId)",
                new { formaPagoId, empresaId }, cancellationToken: ct)))
            throw new ReglaContableException("La forma de pago no pertenece a la empresa.");
        var n = await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE conta.tercero SET forma_pago_id = @formaPagoId WHERE id = @terceroId AND empresa_id = @empresaId",
            new { formaPagoId, terceroId, empresaId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("El tercero no existe en la empresa.");
    }

    // ---------------------------------------------------------------- cuentas bancarias
    public async Task<IReadOnlyList<CuentaBancariaDto>> ListarCuentasBancariasAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<CuentaBancariaDto>(new CommandDefinition("""
            SELECT cb.id, COALESCE(cb.alias, cb.iban) AS alias, cb.iban, cb.bic, b.nombre AS banco,
                   cb.cuenta_contable_id, c.codigo AS cuenta_contable, cb.activa,
                   COALESCE((SELECT SUM(m.importe) FROM tes.movimiento_bancario m WHERE m.cuenta_bancaria_id = cb.id), 0) AS saldo
            FROM tes.cuenta_bancaria cb
            LEFT JOIN cat.banco b ON b.id = cb.banco_id
            LEFT JOIN conta.cuenta c ON c.id = cb.cuenta_contable_id
            WHERE cb.empresa_id = @empresaId AND cb.tercero_id IS NULL
            ORDER BY cb.activa DESC, alias
            """, new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<CuentaBancariaDto> CrearCuentaBancariaAsync(long empresaId, CrearCuentaBancariaCmd cmd, CancellationToken ct)
    {
        var iban = Iban.Normalizar(cmd.Iban);
        if (!Iban.EsValido(iban)) throw new ReglaContableException("El IBAN no es válido (revisa los dígitos de control).");

        await using var cn = await conexiones.AbrirAsync(ct);
        var cuenta = await cn.QuerySingleOrDefaultAsync<CuentaFila>(new CommandDefinition(
            "SELECT codigo, es_imputable FROM conta.cuenta WHERE id = @id AND empresa_id = @empresaId AND activa",
            new { id = cmd.CuentaContableId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("La cuenta contable no existe en el plan de la empresa.");
        if (!cuenta.Codigo.StartsWith("57")) throw new ReglaContableException("La cuenta contable de un banco debe ser del grupo 57 (tesorería).");
        if (!cuenta.EsImputable) throw new ReglaContableException("La cuenta contable elegida no admite apuntes; usa una subcuenta.");
        if (cmd.BancoId is not null && !await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM cat.banco WHERE id = @id)", new { id = cmd.BancoId }, cancellationToken: ct)))
            throw new ReglaContableException("El banco no existe en el catálogo.");

        try
        {
            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO tes.cuenta_bancaria (empresa_id, banco_id, alias, iban, bic, cuenta_contable_id)
                VALUES (@empresaId, @bancoId, @alias, @iban, @bic, @cuentaContableId) RETURNING id
                """, new { empresaId, bancoId = cmd.BancoId, alias = string.IsNullOrWhiteSpace(cmd.Alias) ? null : cmd.Alias.Trim(), iban,
                    bic = string.IsNullOrWhiteSpace(cmd.Bic) ? null : cmd.Bic.Trim().ToUpperInvariant(), cuentaContableId = cmd.CuentaContableId }, cancellationToken: ct));
            return (await ListarCuentasBancariasAsync(empresaId, ct)).First(c => c.Id == id);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Esa cuenta bancaria ya está dada de alta.");
        }
    }

    // ---------------------------------------------------------------- consulta de vencimientos
    public async Task<IReadOnlyList<VencimientoDto>> ListarVencimientosAsync(long empresaId, FiltroVencimientos f, CancellationToken ct)
    {
        if (f.Tipo is not (null or "COBRO" or "PAGO")) throw new ReglaContableException("El tipo debe ser COBRO o PAGO.");
        if (f.Estado is not (null or "PENDIENTE" or "PARCIAL" or "LIQUIDADO" or "ANULADO" or "ABIERTOS"))
            throw new ReglaContableException("Estado de vencimiento no válido.");

        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<VencimientoDto>(new CommandDefinition("""
            SELECT v.id, v.tipo, v.tercero_id, t.nombre AS tercero, v.factura_id,
                   CASE WHEN fa.id IS NULL THEN NULL ELSE COALESCE(fa.serie, '') || fa.numero END AS factura,
                   v.fecha_vencimiento, v.importe, v.importe_pendiente, v.estado, v.num_plazo, v.total_plazos,
                   fp.nombre AS forma_pago,
                   (v.estado IN ('PENDIENTE','PARCIAL') AND v.fecha_vencimiento < CURRENT_DATE) AS vencido
            FROM tes.vencimiento v
            JOIN conta.tercero t ON t.id = v.tercero_id
            LEFT JOIN fiscal.factura fa ON fa.id = v.factura_id
            LEFT JOIN core.forma_pago fp ON fp.id = v.forma_pago_id
            WHERE v.empresa_id = @empresaId
              AND (@tipo::text IS NULL OR v.tipo = @tipo)
              AND (@estado::text IS NULL OR (@estado = 'ABIERTOS' AND v.estado IN ('PENDIENTE','PARCIAL')) OR v.estado = @estado)
              AND (@desde::date IS NULL OR v.fecha_vencimiento >= @desde)
              AND (@hasta::date IS NULL OR v.fecha_vencimiento <= @hasta)
              AND (@terceroId::bigint IS NULL OR v.tercero_id = @terceroId)
            ORDER BY v.fecha_vencimiento, v.id
            LIMIT @limite
            """, new { empresaId, tipo = f.Tipo, estado = f.Estado, desde = f.Desde, hasta = f.Hasta, terceroId = f.TerceroId, limite = Math.Clamp(f.Limite, 1, 1000) },
            cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<PrevisionDto>> PrevisionAsync(long empresaId, DateOnly hoy, int meses, CancellationToken ct)
    {
        meses = Math.Clamp(meses, 1, 24);
        await using var cn = await conexiones.AbrirAsync(ct);
        var filas = await cn.QueryAsync<(string Periodo, string Tipo, decimal Importe)>(new CommandDefinition("""
            SELECT CASE WHEN fecha_vencimiento < @hoy THEN 'VENCIDO' ELSE to_char(fecha_vencimiento, 'YYYY-MM') END AS periodo,
                   tipo, SUM(importe_pendiente) AS importe
            FROM tes.vencimiento
            WHERE empresa_id = @empresaId AND estado IN ('PENDIENTE','PARCIAL')
              AND fecha_vencimiento < (date_trunc('month', @hoy::timestamp) + make_interval(months => @meses))::date
            GROUP BY 1, 2
            """, new { empresaId, hoy, meses }, cancellationToken: ct));

        // Todos los meses del horizonte aparecen, aunque no tengan movimientos
        var mapa = new SortedDictionary<string, PrevisionDto>(StringComparer.Ordinal);
        PrevisionDto Dame(string periodo) => mapa.TryGetValue(periodo, out var p) ? p : mapa[periodo] = new PrevisionDto { Periodo = periodo };
        Dame("VENCIDO");
        for (var i = 0; i < meses; i++) Dame(hoy.AddDays(1 - hoy.Day).AddMonths(i).ToString("yyyy-MM"));
        foreach (var (periodo, tipo, importe) in filas)
        {
            var p = Dame(periodo);
            if (tipo == "COBRO") p.Cobros += importe; else p.Pagos += importe;
        }
        return mapa.Values.OrderBy(p => p.Periodo == "VENCIDO" ? "" : p.Periodo, StringComparer.Ordinal).ToList();
    }

    public async Task AnularVencimientoAsync(long empresaId, long vencimientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE tes.vencimiento SET estado = 'ANULADO'
            WHERE id = @vencimientoId AND empresa_id = @empresaId AND estado = 'PENDIENTE' AND importe_pendiente = importe
            """, new { vencimientoId, empresaId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("Solo se anulan vencimientos pendientes sin cobros ni pagos registrados.");
    }

    // ---------------------------------------------------------------- cobro / pago
    private sealed class VencFila
    {
        public string Tipo { get; set; } = "";
        public long TerceroId { get; set; }
        public long? FacturaId { get; set; }
        public decimal Importe { get; set; }
        public decimal ImportePendiente { get; set; }
        public string Estado { get; set; } = "";
        public long? CuentaTercero { get; set; }
        public string TerceroNombre { get; set; } = "";
        public string? FacturaNumero { get; set; }
        public long? CanalFactura { get; set; }
    }
    private sealed class CuentaFila { public string Codigo { get; set; } = ""; public bool EsImputable { get; set; } }
    private sealed class BancoFila { public long? CuentaContableId { get; set; } public bool Activa { get; set; } }

    public async Task<LiquidacionCreada> LiquidarAsync(long empresaId, long ejercicioId, long usuarioId, long vencimientoId,
        LiquidarVencimientoCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var r = await LiquidarEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, vencimientoId, cmd, ct);
        await tx.CommitAsync(ct);
        return r;
    }

    public async Task<LiquidacionCreada> LiquidarEnTransaccionAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long ejercicioId,
        long usuarioId, long vencimientoId, LiquidarVencimientoCmd cmd, CancellationToken ct)
    {
        // Bloqueo de la fila: dos cobros simultáneos del mismo vencimiento no pueden pasarse del importe
        var v = await cn.QuerySingleOrDefaultAsync<VencFila>(new CommandDefinition("""
            SELECT v.tipo, v.tercero_id, v.factura_id, v.importe, v.importe_pendiente, v.estado,
                   CASE v.tipo WHEN 'COBRO' THEN t.cuenta_cliente_id ELSE t.cuenta_proveedor_id END AS cuenta_tercero,
                   t.nombre AS tercero_nombre, COALESCE(fa.serie, '') || fa.numero AS factura_numero, fa.canal_id AS canal_factura
            FROM tes.vencimiento v
            JOIN conta.tercero t ON t.id = v.tercero_id
            LEFT JOIN fiscal.factura fa ON fa.id = v.factura_id
            WHERE v.id = @vencimientoId AND v.empresa_id = @empresaId
            FOR UPDATE OF v
            """, new { vencimientoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El vencimiento no existe.");

        if (v.Estado is not ("PENDIENTE" or "PARCIAL"))
            throw new ReglaContableException($"El vencimiento está {v.Estado.ToLowerInvariant()}; no admite más cobros ni pagos.");
        var importe = Math.Round(cmd.Importe ?? v.ImportePendiente, 2, MidpointRounding.AwayFromZero);
        if (importe <= 0) throw new ReglaContableException("El importe debe ser mayor que cero.");
        if (importe > v.ImportePendiente)
            throw new ReglaContableException($"El importe supera lo pendiente ({v.ImportePendiente:N2}).");
        if (v.CuentaTercero is null)
            throw new ReglaContableException($"{v.TerceroNombre} no tiene subcuenta de {(v.Tipo == "COBRO" ? "cliente" : "proveedor")}.");

        var banco = await cn.QuerySingleOrDefaultAsync<BancoFila>(new CommandDefinition(
            "SELECT cuenta_contable_id, activa FROM tes.cuenta_bancaria WHERE id = @id AND empresa_id = @empresaId AND tercero_id IS NULL",
            new { id = cmd.CuentaBancariaId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La cuenta bancaria no pertenece a la empresa.");
        if (!banco.Activa) throw new ReglaContableException("La cuenta bancaria está desactivada.");
        if (banco.CuentaContableId is null) throw new ReglaContableException("La cuenta bancaria no tiene cuenta contable asignada (572…).");

        var canalId = cmd.CanalId ?? v.CanalFactura
            ?? throw new ReglaContableException("Indica el canal del asiento: el vencimiento no viene de una factura con canal.");
        var diarioId = cmd.DiarioId ?? await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'BANCOS' ORDER BY id LIMIT 1",
            new { empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("No hay un diario de bancos configurado.");

        var cobro = v.Tipo == "COBRO";
        var referencia = v.FacturaNumero is { Length: > 0 } ? $" factura {v.FacturaNumero}" : "";
        var concepto = $"{(cobro ? "Cobro" : "Pago")}{referencia} {v.TerceroNombre}";
        var apuntes = new List<ApunteCmd>
        {
            // Cobro:  Banco (D) / Cliente (H).   Pago:  Proveedor (D) / Banco (H)
            new() { CuentaId = banco.CuentaContableId.Value, CanalId = canalId, Debe = cobro ? importe : 0, Haber = cobro ? 0 : importe, Concepto = concepto },
            new() { CuentaId = v.CuentaTercero.Value, TerceroId = v.TerceroId, CanalId = canalId, Debe = cobro ? 0 : importe, Haber = cobro ? importe : 0, Concepto = concepto }
        };

        // El asiento valida que la fecha cae en el ejercicio elegido (abierto) y que cuentas, tercero y canal son de la empresa
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
            new CrearAsientoCmd { Fecha = cmd.Fecha, DiarioId = diarioId, Concepto = concepto, Apuntes = apuntes },
            "TESORERIA", null, v.FacturaNumero, ct);

        var movimientoId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO tes.movimiento_bancario (empresa_id, cuenta_bancaria_id, fecha, importe, concepto, origen, asiento_id)
            VALUES (@empresaId, @cuentaBancariaId, @fecha, @importe, @concepto, 'MANUAL', @asientoId) RETURNING id
            """, new { empresaId, cuentaBancariaId = cmd.CuentaBancariaId, fecha = cmd.Fecha, importe = cobro ? importe : -importe, concepto, asientoId = a.Id },
            tx, cancellationToken: ct));

        var cobroPagoId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO tes.cobro_pago (empresa_id, vencimiento_id, movimiento_id, fecha, importe, asiento_id, creado_por)
            VALUES (@empresaId, @vencimientoId, @movimientoId, @fecha, @importe, @asientoId, @usuarioId) RETURNING id
            """, new { empresaId, vencimientoId, movimientoId, fecha = cmd.Fecha, importe, asientoId = a.Id, usuarioId }, tx, cancellationToken: ct));

        var pendiente = v.ImportePendiente - importe;
        var estado = CalculadorVencimientos.EstadoTras(v.Importe, pendiente);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE tes.vencimiento SET importe_pendiente = @pendiente, estado = @estado, cuenta_bancaria_id = @cuentaBancariaId WHERE id = @vencimientoId",
            new { pendiente, estado, cuentaBancariaId = cmd.CuentaBancariaId, vencimientoId }, tx, cancellationToken: ct));

        return new LiquidacionCreada(cobroPagoId, a.Id, a.Numero, estado, pendiente);
    }
}
