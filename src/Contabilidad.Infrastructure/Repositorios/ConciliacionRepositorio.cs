using Contabilidad.Domain;
using Contabilidad.Domain.Tesoreria;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IConciliacionRepositorio
{
    Task<ImportacionDto> ImportarAsync(long empresaId, long usuarioId, ImportarExtractoCmd cmd, CancellationToken ct);
    Task<IReadOnlyList<ExtractoResumenDto>> ListarExtractosAsync(long empresaId, long? cuentaBancariaId, CancellationToken ct);
    Task EliminarExtractoAsync(long empresaId, long extractoId, CancellationToken ct);

    Task<EstadoConciliacionDto> EstadoAsync(long empresaId, long cuentaBancariaId, DateOnly? hasta, CancellationToken ct);
    Task<ConciliacionAutomaticaDto> AutomaticaAsync(long empresaId, long usuarioId, ConciliacionAutomaticaCmd cmd, CancellationToken ct);
    Task<ConciliacionHechaDto> ConciliarAsync(long empresaId, long usuarioId, ConciliarCmd cmd, CancellationToken ct);
    Task DeshacerAsync(long empresaId, long conciliacionId, CancellationToken ct);
    Task CambiarEstadoLineaAsync(long empresaId, long lineaId, bool ignorar, CancellationToken ct);

    Task<ConciliacionHechaDto> AsientoDesdeLineaAsync(long empresaId, long ejercicioId, long usuarioId, long lineaId, AsientoDesdeLineaCmd cmd, CancellationToken ct);
    Task<ConciliacionHechaDto> LiquidarDesdeLineaAsync(long empresaId, long ejercicioId, long usuarioId, long lineaId, LiquidarDesdeLineaCmd cmd, CancellationToken ct);
}

/// <summary>
/// Conciliación bancaria: casa las líneas de los extractos con los apuntes de la cuenta contable del banco (572…), vengan de donde
/// vengan (cobros y pagos, asientos manuales, liquidaciones de impuestos…). Los asientos de apertura y cierre no son movimientos de
/// dinero y no se concilian.
/// </summary>
public sealed class ConciliacionRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos, ITesoreriaRepositorio tesoreria)
    : IConciliacionRepositorio
{
    private sealed class CuentaFila
    {
        public long Id { get; set; }
        public string Iban { get; set; } = "";
        public string? Alias { get; set; }
        public long? CuentaContableId { get; set; }
        public string? CuentaContable { get; set; }
        public string Nombre => string.IsNullOrWhiteSpace(Alias) ? Iban : $"{Alias} ({Iban})";
    }
    private sealed class ExistenteFila
    {
        public DateOnly Fecha { get; set; }
        public DateOnly? FechaValor { get; set; }
        public decimal Importe { get; set; }
        public string Concepto { get; set; } = "";
        public string? ConceptoComun { get; set; }
        public string? Documento { get; set; }
        public string? Referencia1 { get; set; }
        public string? Referencia2 { get; set; }
        public string Clave => new LineaExtracto(Fecha, FechaValor, Importe, Concepto, ConceptoComun, Documento, Referencia1, Referencia2).Clave;
    }
    private sealed class OrigenSaldoFila { public DateOnly FechaDesde { get; set; } public decimal SaldoInicial { get; set; } }
    private sealed class LineaFila
    {
        public long Id { get; set; }
        public long CuentaBancariaId { get; set; }
        public DateOnly Fecha { get; set; }
        public decimal Importe { get; set; }
        public string? Concepto { get; set; }
        public string Estado { get; set; } = "";
    }

    // Apuntes de la cuenta del banco que son movimientos de dinero (sin apertura ni cierre)
    private const string SqlApuntesBanco = """
        SELECT a.id AS apunte_id, s.id AS asiento_id, s.numero, e.codigo AS ejercicio, d.codigo AS diario, s.fecha,
               a.debe - a.haber AS importe, COALESCE(a.concepto, s.concepto) AS concepto
        FROM conta.apunte a
        JOIN conta.asiento s ON s.id = a.asiento_id
        JOIN core.ejercicio e ON e.id = s.ejercicio_id
        JOIN conta.diario d ON d.id = s.diario_id
        WHERE a.empresa_id = @empresaId AND a.cuenta_id = @cuentaContableId AND s.estado = 'CONTABILIZADO'
          AND s.origen NOT IN ('APERTURA', 'CIERRE')
        """;

    // =====================================================================
    // Importación de extractos
    // =====================================================================
    public async Task<ImportacionDto> ImportarAsync(long empresaId, long usuarioId, ImportarExtractoCmd cmd, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(cmd.Contenido ?? ""); }
        catch (FormatException) { throw new ReglaContableException("El contenido del fichero no es válido."); }
        if (bytes.Length == 0) throw new ReglaContableException("El fichero está vacío.");
        if (bytes.Length > 10_000_000) throw new ReglaContableException("El fichero es demasiado grande (máximo 10 MB).");
        var lectura = LectorExtractos.Leer(bytes);
        var nombre = string.IsNullOrWhiteSpace(cmd.Nombre) ? null : cmd.Nombre.Trim()[..Math.Min(cmd.Nombre.Trim().Length, 200)];

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var cuentas = (await cn.QueryAsync<CuentaFila>(new CommandDefinition(
            "SELECT id, iban, alias, cuenta_contable_id FROM tes.cuenta_bancaria WHERE empresa_id = @empresaId AND tercero_id IS NULL AND activa",
            new { empresaId }, tx, cancellationToken: ct))).ToList();

        var avisos = new List<string>(lectura.Avisos);
        var importados = new List<ExtractoImportadoDto>();
        foreach (var e in lectura.Extractos)
        {
            CuentaFila cuenta;
            if (cmd.CuentaBancariaId is { } elegida)
            {
                cuenta = cuentas.FirstOrDefault(c => c.Id == elegida) ?? throw new ReglaContableException("La cuenta bancaria no pertenece a la empresa o está desactivada.");
                if (e.Cuenta is not null && !e.EsDeIban(cuenta.Iban))
                    throw new ReglaContableException($"El fichero es de la cuenta {e.Entidad} {e.Oficina} {e.Cuenta}, que no corresponde a {cuenta.Iban}.");
                if (lectura.Extractos.Count > 1) throw new ReglaContableException("El fichero trae varias cuentas: importa sin elegir cuenta y se asignará cada una por su IBAN.");
            }
            else
            {
                if (e.Cuenta is null) throw new ReglaContableException("Elige la cuenta bancaria: en un CSV no viene indicada.");
                cuenta = cuentas.FirstOrDefault(c => e.EsDeIban(c.Iban))
                    ?? throw new ReglaContableException($"No hay ninguna cuenta bancaria dada de alta con entidad {e.Entidad}, oficina {e.Oficina} y número {e.Cuenta}.");
            }

            // Bloqueo por cuenta: dos importaciones a la vez no duplican líneas
            await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
                new { clave = $"extracto|{cuenta.Id}" }, tx, cancellationToken: ct));
            var existentes = (await cn.QueryAsync<ExistenteFila>(new CommandDefinition("""
                SELECT fecha, fecha_valor, importe, COALESCE(concepto, '') AS concepto, concepto_comun, documento, referencia1, referencia2
                FROM tes.extracto_linea WHERE cuenta_bancaria_id = @cuenta AND fecha BETWEEN @desde AND @hasta
                """, new { cuenta = cuenta.Id, desde = e.Desde, hasta = e.Hasta }, tx, cancellationToken: ct))).Select(l => l.Clave);
            var nuevas = Conciliador.Nuevas(e.Lineas, existentes);

            var solapa = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM tes.extracto WHERE cuenta_bancaria_id = @cuenta AND fecha_desde <= @hasta AND fecha_hasta >= @desde)
                """, new { cuenta = cuenta.Id, desde = e.Desde, hasta = e.Hasta }, tx, cancellationToken: ct));
            if (solapa && nuevas.Count > 0)
                avisos.Add($"{cuenta.Nombre}: el periodo se solapa con otro extracto ya importado; las líneas repetidas no se han vuelto a importar.");
            if (nuevas.Count == 0)
            {
                avisos.Add($"{cuenta.Nombre}: todas las líneas ({e.Lineas.Count}) ya estaban importadas.");
                importados.Add(new ExtractoImportadoDto(0, cuenta.Id, cuenta.Nombre, e.Desde, e.Hasta, e.SaldoInicial, e.SaldoFinal, e.Lineas.Count, 0, e.Lineas.Count));
                continue;
            }

            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO tes.extracto (empresa_id, cuenta_bancaria_id, formato, fichero, fecha_desde, fecha_hasta, saldo_inicial, saldo_final, importado_por)
                VALUES (@empresaId, @cuenta, @formato, @nombre, @desde, @hasta, @si, @sf, @usuarioId) RETURNING id
                """, new { empresaId, cuenta = cuenta.Id, formato = lectura.Formato, nombre, desde = e.Desde, hasta = e.Hasta,
                           si = e.SaldoInicial, sf = e.SaldoFinal, usuarioId }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO tes.extracto_linea (extracto_id, empresa_id, cuenta_bancaria_id, orden, fecha, fecha_valor, importe, concepto, concepto_comun,
                                                documento, referencia1, referencia2)
                VALUES (@id, @empresaId, @cuenta, @orden, @Fecha, @FechaValor, @Importe, @Concepto, @ConceptoComun, @Documento, @Referencia1, @Referencia2)
                """, nuevas.Select((l, i) => new
            {
                id, empresaId, cuenta = cuenta.Id, orden = i + 1, l.Fecha, l.FechaValor, l.Importe, Concepto = Recortar(l.Concepto, 400),
                l.ConceptoComun, Documento = Recortar(l.Documento, 20), Referencia1 = Recortar(l.Referencia1, 20), Referencia2 = Recortar(l.Referencia2, 20)
            }), tx, cancellationToken: ct));
            importados.Add(new ExtractoImportadoDto(id, cuenta.Id, cuenta.Nombre, e.Desde, e.Hasta, e.SaldoInicial, e.SaldoFinal, e.Lineas.Count,
                nuevas.Count, e.Lineas.Count - nuevas.Count));
        }
        await tx.CommitAsync(ct);
        return new ImportacionDto(lectura.Formato, importados, avisos);
    }

    private static string? Recortar(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public async Task<IReadOnlyList<ExtractoResumenDto>> ListarExtractosAsync(long empresaId, long? cuentaBancariaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<ExtractoResumenDto>(new CommandDefinition("""
            SELECT x.id, x.cuenta_bancaria_id, COALESCE(NULLIF(c.alias, ''), c.iban) AS cuenta, x.formato, x.fichero, x.fecha_desde, x.fecha_hasta,
                   x.saldo_inicial, x.saldo_final, x.importado_en,
                   (SELECT count(*) FROM tes.extracto_linea l WHERE l.extracto_id = x.id)::int AS lineas,
                   (SELECT count(*) FROM tes.extracto_linea l WHERE l.extracto_id = x.id AND l.estado = 'CONCILIADA')::int AS conciliadas
            FROM tes.extracto x JOIN tes.cuenta_bancaria c ON c.id = x.cuenta_bancaria_id
            WHERE x.empresa_id = @empresaId AND (@cuentaBancariaId::bigint IS NULL OR x.cuenta_bancaria_id = @cuentaBancariaId)
            ORDER BY x.fecha_desde DESC, x.id DESC
            """, new { empresaId, cuentaBancariaId }, cancellationToken: ct))).AsList();
    }

    public async Task EliminarExtractoAsync(long empresaId, long extractoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var conciliadas = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT (SELECT count(*) FROM tes.extracto_linea l WHERE l.extracto_id = x.id AND l.estado = 'CONCILIADA')::int
            FROM tes.extracto x WHERE x.id = @extractoId AND x.empresa_id = @empresaId FOR UPDATE
            """, new { extractoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El extracto no existe.");
        if (conciliadas > 0) throw new ReglaContableException($"El extracto tiene {conciliadas} línea(s) conciliadas: deshaz antes esas conciliaciones.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM tes.extracto WHERE id = @extractoId", new { extractoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // =====================================================================
    // Estado de la conciliación
    // =====================================================================
    public async Task<EstadoConciliacionDto> EstadoAsync(long empresaId, long cuentaBancariaId, DateOnly? hastaPedido, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var cuenta = await CuentaAsync(cn, null, empresaId, cuentaBancariaId, ct);
        var hasta = hastaPedido ?? DateOnly.FromDateTime(DateTime.Today);
        var avisos = new List<string>();

        // Desde el primer extracto importado: lo anterior se da por conciliado
        var desde = await cn.ExecuteScalarAsync<DateOnly?>(new CommandDefinition(
            "SELECT min(fecha_desde) FROM tes.extracto WHERE cuenta_bancaria_id = @cuentaBancariaId", new { cuentaBancariaId }, cancellationToken: ct));
        if (desde is null) avisos.Add("Todavía no se ha importado ningún extracto de esta cuenta.");

        var saldoContable = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition($"""
            SELECT COALESCE(SUM(x.importe), 0) FROM ({SqlApuntesBanco}) x WHERE x.fecha <= @hasta
            """, new { empresaId, cuentaContableId = cuenta.CuentaContableId, hasta }, cancellationToken: ct));

        // Saldo del banco: saldo inicial del primer extracto que lo traiga más todas las líneas (salvo las ignoradas) hasta la fecha
        var origen = await cn.QuerySingleOrDefaultAsync<OrigenSaldoFila>(new CommandDefinition("""
            SELECT fecha_desde, saldo_inicial FROM tes.extracto
            WHERE cuenta_bancaria_id = @cuentaBancariaId AND saldo_inicial IS NOT NULL ORDER BY fecha_desde, id LIMIT 1
            """, new { cuentaBancariaId }, cancellationToken: ct));
        decimal? saldoBanco = null;
        if (origen is { } o)
            saldoBanco = o.SaldoInicial + await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                SELECT COALESCE(SUM(importe), 0) FROM tes.extracto_linea
                WHERE cuenta_bancaria_id = @cuentaBancariaId AND estado <> 'IGNORADA' AND fecha >= @desde AND fecha <= @hasta
                """, new { cuentaBancariaId, desde = o.FechaDesde, hasta }, cancellationToken: ct));
        else if (desde is not null)
            avisos.Add("Los extractos importados no traen saldo (CSV sin columna de saldo): no se puede comprobar el saldo del banco.");

        var lineas = (await cn.QueryAsync<LineaExtractoDto>(new CommandDefinition("""
            SELECT id, extracto_id, fecha, fecha_valor, importe, concepto, concepto_comun, documento, referencia1, referencia2, estado
            FROM tes.extracto_linea WHERE cuenta_bancaria_id = @cuentaBancariaId AND estado = 'PENDIENTE' AND fecha <= @hasta
            ORDER BY fecha, extracto_id, orden
            """, new { cuentaBancariaId, hasta }, cancellationToken: ct))).AsList();
        var apuntes = desde is null ? [] : (await cn.QueryAsync<ApunteBancoDto>(new CommandDefinition($"""
            SELECT x.* FROM ({SqlApuntesBanco}) x
            WHERE x.fecha BETWEEN @desde AND @hasta AND NOT EXISTS (SELECT 1 FROM tes.conciliacion_apunte ca WHERE ca.apunte_id = x.apunte_id)
            ORDER BY x.fecha, x.asiento_id, x.apunte_id
            """, new { empresaId, cuentaContableId = cuenta.CuentaContableId, desde, hasta }, cancellationToken: ct))).AsList();

        var recientes = (await cn.QueryAsync<ConciliacionDto>(new CommandDefinition("""
            SELECT c.id, c.tipo, c.importe, c.creado_en,
                   (SELECT string_agg(to_char(l.fecha, 'DD/MM') || ' ' || COALESCE(left(l.concepto, 40), ''), ' · ' ORDER BY l.fecha)
                    FROM tes.conciliacion_linea cl JOIN tes.extracto_linea l ON l.id = cl.extracto_linea_id WHERE cl.conciliacion_id = c.id) AS lineas,
                   (SELECT string_agg(e.codigo || ' nº ' || s.numero, ' · ' ORDER BY s.fecha)
                    FROM tes.conciliacion_apunte ca JOIN conta.apunte a ON a.id = ca.apunte_id JOIN conta.asiento s ON s.id = a.asiento_id
                    JOIN core.ejercicio e ON e.id = s.ejercicio_id WHERE ca.conciliacion_id = c.id) AS apuntes
            FROM tes.conciliacion c WHERE c.cuenta_bancaria_id = @cuentaBancariaId ORDER BY c.creado_en DESC, c.id DESC LIMIT 30
            """, new { cuentaBancariaId }, cancellationToken: ct))).AsList();

        var rotas = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(DISTINCT ca.conciliacion_id) FROM tes.conciliacion_apunte ca
            JOIN conta.apunte a ON a.id = ca.apunte_id JOIN conta.asiento s ON s.id = a.asiento_id
            JOIN tes.conciliacion c ON c.id = ca.conciliacion_id
            WHERE c.cuenta_bancaria_id = @cuentaBancariaId AND s.estado <> 'CONTABILIZADO'
            """, new { cuentaBancariaId }, cancellationToken: ct));
        if (rotas > 0) avisos.Add($"{rotas} conciliación(es) incluyen asientos que se han anulado después: deshazlas y vuelve a conciliar.");

        var pendLineas = lineas.Sum(l => l.Importe);
        var pendApuntes = apuntes.Sum(a => a.Importe);
        decimal? diferencia = saldoBanco is null ? null : (saldoBanco - pendLineas) - (saldoContable - pendApuntes);
        if (diferencia is { } dif && dif != 0)
            avisos.Add($"Diferencia de {dif:N2} entre banco y contabilidad después de las partidas pendientes. Puede venir de antes del primer " +
                       "extracto (saldo inicial distinto), de extractos sin importar o de líneas ignoradas.");
        return new EstadoConciliacionDto(cuenta.Id, cuenta.Nombre, cuenta.CuentaContable ?? "", desde, hasta, saldoContable, saldoBanco,
            pendLineas, pendApuntes, diferencia, lineas, apuntes, recientes, avisos);
    }

    // =====================================================================
    // Conciliar
    // =====================================================================
    public async Task<ConciliacionAutomaticaDto> AutomaticaAsync(long empresaId, long usuarioId, ConciliacionAutomaticaCmd cmd, CancellationToken ct)
    {
        if (cmd.DiasMargen is < 0 or > 60) throw new ReglaContableException("El margen de días debe estar entre 0 y 60.");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var cuenta = await CuentaAsync(cn, tx, empresaId, cmd.CuentaBancariaId, ct);
        await Bloquear(cn, tx, cuenta.Id, ct);

        var lineas = (await cn.QueryAsync<LineaFila>(new CommandDefinition(
            "SELECT id, cuenta_bancaria_id, fecha, importe, concepto, estado FROM tes.extracto_linea WHERE cuenta_bancaria_id = @id AND estado = 'PENDIENTE'",
            new { id = cuenta.Id }, tx, cancellationToken: ct))).ToList();
        var desde = lineas.Count == 0 ? DateOnly.MaxValue : lineas.Min(l => l.Fecha).AddDays(-cmd.DiasMargen);
        var apuntes = (await cn.QueryAsync<ApunteBancoDto>(new CommandDefinition($"""
            SELECT x.* FROM ({SqlApuntesBanco}) x
            WHERE x.fecha >= @desde AND NOT EXISTS (SELECT 1 FROM tes.conciliacion_apunte ca WHERE ca.apunte_id = x.apunte_id)
            """, new { empresaId, cuentaContableId = cuenta.CuentaContableId, desde }, tx, cancellationToken: ct))).ToList();

        var pares = Conciliador.Automatica(lineas.Select(l => new LineaPendiente(l.Id, l.Fecha, l.Importe, l.Concepto ?? "")),
            apuntes.Select(a => new ApunteBanco(a.ApunteId, a.Fecha, a.Importe, a.Concepto ?? "")), cmd.DiasMargen);
        var importes = lineas.ToDictionary(l => l.Id, l => l.Importe);
        foreach (var (lineaId, apunteId) in pares)
            await CrearAsync(cn, tx, empresaId, cuenta.Id, usuarioId, "AUTOMATICA", importes[lineaId], [lineaId], [apunteId], ct);
        await tx.CommitAsync(ct);
        return new ConciliacionAutomaticaDto(pares.Count, lineas.Count - pares.Count, apuntes.Count - pares.Count);
    }

    public async Task<ConciliacionHechaDto> ConciliarAsync(long empresaId, long usuarioId, ConciliarCmd cmd, CancellationToken ct)
    {
        var lineaIds = cmd.LineaIds.Distinct().ToArray();
        var apunteIds = cmd.ApunteIds.Distinct().ToArray();
        if (lineaIds.Length == 0 || apunteIds.Length == 0) throw new ReglaContableException("Elige al menos una línea del banco y un apunte contable.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var cuenta = await CuentaAsync(cn, tx, empresaId, cmd.CuentaBancariaId, ct);
        await Bloquear(cn, tx, cuenta.Id, ct);

        var lineas = (await cn.QueryAsync<LineaFila>(new CommandDefinition(
            "SELECT id, cuenta_bancaria_id, fecha, importe, concepto, estado FROM tes.extracto_linea WHERE id = ANY(@lineaIds) AND cuenta_bancaria_id = @cuenta",
            new { lineaIds, cuenta = cuenta.Id }, tx, cancellationToken: ct))).ToList();
        if (lineas.Count != lineaIds.Length) throw new ReglaContableException("Alguna línea no pertenece a esta cuenta bancaria.");
        if (lineas.Any(l => l.Estado != "PENDIENTE")) throw new ReglaContableException("Alguna línea ya está conciliada o ignorada.");

        var apuntes = (await cn.QueryAsync<ApunteBancoDto>(new CommandDefinition($"""
            SELECT x.* FROM ({SqlApuntesBanco}) x WHERE x.apunte_id = ANY(@apunteIds)
            """, new { empresaId, cuentaContableId = cuenta.CuentaContableId, apunteIds }, tx, cancellationToken: ct))).ToList();
        if (apuntes.Count != apunteIds.Length)
            throw new ReglaContableException("Algún apunte no es de la cuenta contable de este banco, no está contabilizado o es de apertura o cierre.");

        var totalBanco = lineas.Sum(l => l.Importe);
        var totalLibros = apuntes.Sum(a => a.Importe);
        if (totalBanco != totalLibros)
            throw new ReglaContableException($"Los importes no coinciden: banco {totalBanco:N2}, contabilidad {totalLibros:N2}.");

        var id = await CrearAsync(cn, tx, empresaId, cuenta.Id, usuarioId, "MANUAL", totalBanco, lineaIds, apunteIds, ct);
        await tx.CommitAsync(ct);
        return new ConciliacionHechaDto(id, null, null);
    }

    public async Task DeshacerAsync(long empresaId, long conciliacionId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var cuentaId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT cuenta_bancaria_id FROM tes.conciliacion WHERE id = @conciliacionId AND empresa_id = @empresaId", new { conciliacionId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La conciliación no existe.");
        await Bloquear(cn, tx, cuentaId, ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE tes.extracto_linea SET estado = 'PENDIENTE'
            WHERE id IN (SELECT extracto_linea_id FROM tes.conciliacion_linea WHERE conciliacion_id = @conciliacionId)
            """, new { conciliacionId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM tes.conciliacion WHERE id = @conciliacionId", new { conciliacionId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task CambiarEstadoLineaAsync(long empresaId, long lineaId, bool ignorar, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE tes.extracto_linea SET estado = @nuevo WHERE id = @lineaId AND empresa_id = @empresaId AND estado = @actual
            """, new { lineaId, empresaId, nuevo = ignorar ? "IGNORADA" : "PENDIENTE", actual = ignorar ? "PENDIENTE" : "IGNORADA" }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException(ignorar ? "Solo se pueden ignorar líneas pendientes." : "La línea no está ignorada.");
    }

    // =====================================================================
    // Contabilizar desde una línea del extracto
    // =====================================================================
    public async Task<ConciliacionHechaDto> AsientoDesdeLineaAsync(long empresaId, long ejercicioId, long usuarioId, long lineaId, AsientoDesdeLineaCmd cmd,
        CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var (linea, cuenta) = await LineaPendienteAsync(cn, tx, empresaId, lineaId, ct);
        if (cmd.CuentaId == cuenta.CuentaContableId) throw new ReglaContableException("La contrapartida no puede ser la propia cuenta del banco.");

        var concepto = string.IsNullOrWhiteSpace(cmd.Concepto) ? (linea.Concepto ?? "Movimiento bancario") : cmd.Concepto.Trim();
        if (concepto.Length > 250) concepto = concepto[..250];
        var entra = linea.Importe > 0;
        var importe = Math.Abs(linea.Importe);
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, new CrearAsientoCmd
        {
            Fecha = linea.Fecha, Concepto = concepto,
            DiarioId = cmd.DiarioId ?? await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'BANCOS' ORDER BY id LIMIT 1", new { empresaId }, tx, cancellationToken: ct))
                ?? await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct),
            Apuntes =
            [
                new() { CuentaId = cuenta.CuentaContableId!.Value, CanalId = cmd.CanalId, Debe = entra ? importe : 0, Haber = entra ? 0 : importe, Concepto = concepto },
                new() { CuentaId = cmd.CuentaId, TerceroId = cmd.TerceroId, CanalId = cmd.CanalId, Debe = entra ? 0 : importe, Haber = entra ? importe : 0, Concepto = concepto }
            ]
        }, "TESORERIA", null, "EXTRACTO", ct);

        var id = await ConciliarAsientoAsync(cn, tx, empresaId, usuarioId, cuenta, linea, a.Id, ct);
        await tx.CommitAsync(ct);
        return new ConciliacionHechaDto(id, a.Id, a.Numero);
    }

    public async Task<ConciliacionHechaDto> LiquidarDesdeLineaAsync(long empresaId, long ejercicioId, long usuarioId, long lineaId, LiquidarDesdeLineaCmd cmd,
        CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var (linea, cuenta) = await LineaPendienteAsync(cn, tx, empresaId, lineaId, ct);

        var tipo = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT tipo FROM tes.vencimiento WHERE id = @id AND empresa_id = @empresaId", new { id = cmd.VencimientoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El vencimiento no existe.");
        if ((tipo == "COBRO") != (linea.Importe > 0))
            throw new ReglaContableException(linea.Importe > 0 ? "Un abono del banco solo puede liquidar un cobro." : "Un cargo del banco solo puede liquidar un pago.");

        var r = await tesoreria.LiquidarEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, cmd.VencimientoId, new LiquidarVencimientoCmd
        {
            Fecha = linea.Fecha, Importe = Math.Abs(linea.Importe), CuentaBancariaId = cuenta.Id, CanalId = cmd.CanalId
        }, ct);
        var id = await ConciliarAsientoAsync(cn, tx, empresaId, usuarioId, cuenta, linea, r.AsientoId, ct);
        await tx.CommitAsync(ct);
        return new ConciliacionHechaDto(id, r.AsientoId, r.AsientoNumero);
    }

    // ------------------------------------------------------------------
    private async Task<long> ConciliarAsientoAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long usuarioId, CuentaFila cuenta,
        LineaFila linea, long asientoId, CancellationToken ct)
    {
        var apuntes = (await cn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM conta.apunte WHERE asiento_id = @asientoId AND cuenta_id = @cuentaContableId",
            new { asientoId, cuentaContableId = cuenta.CuentaContableId }, tx, cancellationToken: ct))).ToArray();
        return await CrearAsync(cn, tx, empresaId, cuenta.Id, usuarioId, "ASIENTO", linea.Importe, [linea.Id], apuntes, ct);
    }

    private static async Task<long> CrearAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long cuentaBancariaId, long usuarioId, string tipo,
        decimal importe, long[] lineaIds, long[] apunteIds, CancellationToken ct)
    {
        try
        {
            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO tes.conciliacion (empresa_id, cuenta_bancaria_id, tipo, importe, creado_por) VALUES (@empresaId, @cuentaBancariaId, @tipo, @importe, @usuarioId)
                RETURNING id
                """, new { empresaId, cuentaBancariaId, tipo, importe, usuarioId }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tes.conciliacion_linea (conciliacion_id, extracto_linea_id) SELECT @id, unnest(@lineaIds)", new { id, lineaIds }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tes.conciliacion_apunte (conciliacion_id, apunte_id) SELECT @id, unnest(@apunteIds)", new { id, apunteIds }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE tes.extracto_linea SET estado = 'CONCILIADA' WHERE id = ANY(@lineaIds)", new { lineaIds }, tx, cancellationToken: ct));
            return id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Alguna línea o apunte ya está conciliado.");
        }
    }

    private static async Task<(LineaFila, CuentaFila)> LineaPendienteAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long lineaId, CancellationToken ct)
    {
        var linea = await cn.QuerySingleOrDefaultAsync<LineaFila>(new CommandDefinition(
            "SELECT id, cuenta_bancaria_id, fecha, importe, concepto, estado FROM tes.extracto_linea WHERE id = @lineaId AND empresa_id = @empresaId FOR UPDATE",
            new { lineaId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La línea del extracto no existe.");
        if (linea.Estado != "PENDIENTE") throw new ReglaContableException("La línea ya está conciliada o ignorada.");
        var cuenta = await CuentaAsync(cn, tx, empresaId, linea.CuentaBancariaId, ct);
        await Bloquear(cn, tx, cuenta.Id, ct);
        return (linea, cuenta);
    }

    private static async Task<CuentaFila> CuentaAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long cuentaBancariaId, CancellationToken ct)
    {
        var c = await cn.QuerySingleOrDefaultAsync<CuentaFila>(new CommandDefinition("""
            SELECT b.id, b.iban, b.alias, b.cuenta_contable_id, k.codigo AS cuenta_contable
            FROM tes.cuenta_bancaria b LEFT JOIN conta.cuenta k ON k.id = b.cuenta_contable_id
            WHERE b.id = @cuentaBancariaId AND b.empresa_id = @empresaId AND b.tercero_id IS NULL
            """, new { cuentaBancariaId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La cuenta bancaria no pertenece a la empresa.");
        if (c.CuentaContableId is null) throw new ReglaContableException("La cuenta bancaria no tiene cuenta contable (572…) asignada.");
        return c;
    }

    private static Task Bloquear(NpgsqlConnection cn, NpgsqlTransaction tx, long cuentaBancariaId, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"conciliacion|{cuentaBancariaId}" }, tx, cancellationToken: ct));
}
