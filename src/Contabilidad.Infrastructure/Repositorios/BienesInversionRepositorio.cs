using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IBienesInversionRepositorio
{
    Task<IReadOnlyList<ProrrataDto>> ProrratasAsync(long empresaId, CancellationToken ct);
    Task<ProrrataDto> GuardarProrrataAsync(long empresaId, GuardarProrrataCmd cmd, CancellationToken ct);
    Task<BienInversionDto> AltaAsync(long empresaId, long inmovilizadoId, AltaBienInversionCmd cmd, CancellationToken ct);
    Task QuitarAsync(long empresaId, long inmovilizadoId, CancellationToken ct);
    Task<LibroBienesInversionDto> LibroAsync(long empresaId, int anio, CancellationToken ct);
    Task<RegularizacionContabilizadaDto> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, RegularizarBienesCmd cmd, CancellationToken ct);
    Task AnularAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct);
}

/// <summary>
/// Registro de bienes de inversión y regularización de sus deducciones (arts. 107-110 LIVA). El cálculo está en
/// <see cref="RegularizacionBienesInversion"/>; aquí se leen los datos, se lleva al 303/420 y se contabiliza:
/// más deducción, 472 (D) a 6392 (H); menos deducción, 6342 (D) a 472 (H).
/// </summary>
public sealed class BienesInversionRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : IBienesInversionRepositorio
{
    internal sealed class BienFila
    {
        public long Id { get; set; }
        public long InmovilizadoId { get; set; }
        public string Codigo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public bool EsInmueble { get; set; }
        public int AnioInicio { get; set; }
        public int Anios { get; set; }
        public decimal CuotaSoportada { get; set; }
        public int ProrrataInicial { get; set; }
        public int? SectorDiferenciado { get; set; }
        public bool TransmisionExenta { get; set; }
        public string Estado { get; set; } = "";
        public DateOnly? FechaBaja { get; set; }
        public long? CanalId { get; set; }
    }
    private sealed class ProrrataFila
    {
        public int Anio { get; set; }
        public int? SectorDiferenciado { get; set; }
        public decimal? OperacionesConDerecho { get; set; }
        public decimal? OperacionesTotales { get; set; }
        public int? PorcentajeProvisional { get; set; }
        public int? PorcentajeDefinitivo { get; set; }
        public string Estado { get; set; } = "";
    }
    private sealed class ElementoFila
    {
        public long Id { get; set; }
        public long? FacturaId { get; set; }
        public DateOnly FechaInicioAmortizacion { get; set; }
        public decimal ValorAdquisicion { get; set; }
    }
    private sealed class EjercicioFila { public DateOnly FechaInicio { get; set; } public DateOnly FechaFin { get; set; } public string Estado { get; set; } = ""; }
    private sealed class AsientoFila { public long Id { get; set; } public string Estado { get; set; } = ""; public long EjercicioId { get; set; } public string EjEstado { get; set; } = ""; }
    private sealed class HechaFila
    {
        public long BienId { get; set; }
        public int Anio { get; set; }
        public string Tipo { get; set; } = "";
        public int ProrrataDefinitiva { get; set; }
        public int AniosRegularizados { get; set; }
        public decimal ImporteRegularizacion { get; set; }
        public long? AsientoId { get; set; }
        public int? AsientoNumero { get; set; }
        public DateOnly? Fecha { get; set; }
    }

    /// <summary>Bienes de la empresa con su prorrata inicial efectiva y las prorratas definitivas por (año, sector).</summary>
    internal sealed record Datos(IReadOnlyList<BienFila> Filas, IReadOnlyDictionary<(int Anio, int Sector), int?> Definitivas)
    {
        public int? Definitiva(int anio, int? sector) => Definitivas.TryGetValue((anio, sector ?? 0), out var p) ? p : null;

        public BienInversionRegularizable Bien(BienFila f) => new(f.Id, f.Codigo, f.Descripcion, f.AnioInicio, f.Anios, f.CuotaSoportada,
            Definitiva(f.AnioInicio, f.SectorDiferenciado) ?? f.ProrrataInicial,
            f.Estado == "VENDIDO" ? f.FechaBaja : null, f.TransmisionExenta, f.Estado == "BAJA" ? f.FechaBaja : null);

        public int? Definitiva(BienInversionRegularizable b, int anio)
            => Definitiva(anio, Filas.First(f => f.Id == b.Id).SectorDiferenciado);
    }

    internal static async Task<Datos> DatosAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CancellationToken ct)
    {
        var filas = (await cn.QueryAsync<BienFila>(new CommandDefinition("""
            SELECT b.id, i.id AS inmovilizado_id, i.codigo, i.descripcion, b.es_inmueble, b.anio_inicio_utilizacion AS anio_inicio,
                   b.anios_regularizacion AS anios, b.cuota_soportada, b.prorrata_inicial, b.sector_diferenciado, b.transmision_exenta,
                   i.estado, i.fecha_baja, i.canal_id
            FROM inmov.bien_inversion b JOIN inmov.inmovilizado i ON i.id = b.inmovilizado_id
            WHERE i.empresa_id = @empresaId
            ORDER BY i.codigo
            """, new { empresaId }, tx, cancellationToken: ct))).AsList();
        var prorratas = await cn.QueryAsync<ProrrataFila>(new CommandDefinition(
            "SELECT anio, sector_diferenciado, porcentaje_definitivo FROM fiscal.prorrata WHERE empresa_id = @empresaId",
            new { empresaId }, tx, cancellationToken: ct));
        return new Datos(filas, prorratas.ToDictionary(p => (p.Anio, p.SectorDiferenciado ?? 0), p => p.PorcentajeDefinitivo));
    }

    /// <summary>Lo que el 303/420 del periodo debe llevar en la casilla de regularización de bienes de inversión.</summary>
    internal static async Task<(decimal Importe, List<string> Avisos)> ParaDeclaracionAsync(NpgsqlConnection cn, NpgsqlTransaction? tx,
        long empresaId, PeriodoFiscal p, CancellationToken ct)
    {
        var datos = await DatosAsync(cn, tx, empresaId, ct);
        var avisos = new List<string>();
        var bienes = datos.Filas.Select(datos.Bien).ToList();
        var regs = RegularizacionBienesInversion.DelPeriodo(bienes, p, datos.Definitiva);
        foreach (var r in regs)
        {
            var b = bienes.First(x => x.Id == r.BienId);
            var que = r.Tipo == "TRANSMISION" ? $"venta, {r.AniosRegularizados} año(s) al {r.Prorrata} %" : $"{r.Anio}: {r.Prorrata} % frente a {b.ProrrataInicial} %";
            avisos.Add(r.Importe != 0
                ? $"Regularización del bien de inversión {b.Codigo} {b.Descripcion} ({que}): {r.Importe:N2}."
                : $"Bien de inversión {b.Codigo}: {r.Nota}");
        }
        if (p.EsUltimoDelAnio)
        {
            var sinRegistrar = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(DISTINCT f.id) FROM fiscal.factura f
                JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
                WHERE f.empresa_id = @empresaId AND f.libro = 'RECIBIDAS' AND f.estado = 'CONTABILIZADA' AND fi.es_bien_inversion
                  AND fi.regla_deduccion = 'PRORRATA' AND f.fecha_contable BETWEEN @inicio AND @fin
                  AND NOT EXISTS (SELECT 1 FROM inmov.inmovilizado i JOIN inmov.bien_inversion b ON b.inmovilizado_id = i.id WHERE i.factura_id = f.id)
                """, new { empresaId, inicio = p.InicioAnio, fin = new DateOnly(p.Anio, 12, 31) }, tx, cancellationToken: ct));
            if (sinRegistrar > 0)
                avisos.Add($"Hay {sinRegistrar} factura(s) de {p.Anio} con bienes de inversión y prorrata que no están en el registro de bienes de inversión: " +
                           "dalos de alta (Inmovilizado → ficha → Bien de inversión) para regularizarlos los años siguientes.");
        }
        return (regs.Sum(r => r.Importe), avisos);
    }

    // =====================================================================
    // Prorrata
    // =====================================================================
    public async Task<IReadOnlyList<ProrrataDto>> ProrratasAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<ProrrataDto>(new CommandDefinition("""
            SELECT anio, sector_diferenciado, operaciones_con_derecho, operaciones_totales, porcentaje_provisional, porcentaje_definitivo, estado
            FROM fiscal.prorrata WHERE empresa_id = @empresaId ORDER BY anio DESC, sector_diferenciado NULLS FIRST
            """, new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<ProrrataDto> GuardarProrrataAsync(long empresaId, GuardarProrrataCmd cmd, CancellationToken ct)
    {
        if (cmd.Anio is < 2000 or > 2100) throw new ReglaContableException("El año no es válido.");
        int? definitivo = cmd.PorcentajeDefinitivo;
        if (cmd.OperacionesTotales is { } tot)
        {
            var con = cmd.OperacionesConDerecho ?? throw new ReglaContableException("Indica las operaciones con derecho a deducción.");
            if (tot <= 0 || con < 0 || con > tot) throw new ReglaContableException("Las operaciones con derecho deben estar entre 0 y el total.");
            // Art. 104.Dos LIVA: el porcentaje se redondea a la unidad superior
            var calculado = (int)Math.Ceiling(Math.Round(con * 100m / tot, 6));
            if (definitivo is { } d && d != calculado)
                throw new ReglaContableException($"Con esas operaciones el definitivo es {calculado} %, no {d} %.");
            definitivo = calculado;
        }
        foreach (var (n, v) in new[] { ("provisional", cmd.PorcentajeProvisional), ("definitivo", definitivo) })
            if (v is < 0 or > 100) throw new ReglaContableException($"El porcentaje {n} debe estar entre 0 y 100.");
        if (cmd.PorcentajeProvisional is null && definitivo is null)
            throw new ReglaContableException("Indica el porcentaje provisional, el definitivo o las operaciones del año.");

        await using var cn = await conexiones.AbrirAsync(ct);
        var sector = cmd.SectorDiferenciado is { } s ? (short?)s : null;
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.prorrata (empresa_id, anio, sector_diferenciado, operaciones_con_derecho, operaciones_totales,
                                         porcentaje_provisional, porcentaje_definitivo, estado)
            VALUES (@empresaId, @anio, @sector, @con, @tot, @prov, @def, CASE WHEN @def IS NULL THEN 'PROVISIONAL' ELSE 'DEFINITIVA' END)
            ON CONFLICT (empresa_id, anio, COALESCE(sector_diferenciado, 0)) DO UPDATE SET
              operaciones_con_derecho = COALESCE(EXCLUDED.operaciones_con_derecho, fiscal.prorrata.operaciones_con_derecho),
              operaciones_totales = COALESCE(EXCLUDED.operaciones_totales, fiscal.prorrata.operaciones_totales),
              porcentaje_provisional = COALESCE(EXCLUDED.porcentaje_provisional, fiscal.prorrata.porcentaje_provisional),
              porcentaje_definitivo = COALESCE(EXCLUDED.porcentaje_definitivo, fiscal.prorrata.porcentaje_definitivo),
              estado = CASE WHEN COALESCE(EXCLUDED.porcentaje_definitivo, fiscal.prorrata.porcentaje_definitivo) IS NULL
                            THEN 'PROVISIONAL' ELSE 'DEFINITIVA' END
            """, new { empresaId, anio = (short)cmd.Anio, sector, con = cmd.OperacionesConDerecho, tot = cmd.OperacionesTotales,
                       prov = (short?)cmd.PorcentajeProvisional, def = (short?)definitivo }, cancellationToken: ct));
        return (await ProrratasAsync(empresaId, ct)).First(x => x.Anio == cmd.Anio && x.SectorDiferenciado == cmd.SectorDiferenciado);
    }

    // =====================================================================
    // Registro
    // =====================================================================
    public async Task<BienInversionDto> AltaAsync(long empresaId, long inmovilizadoId, AltaBienInversionCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var i = await cn.QuerySingleOrDefaultAsync<ElementoFila>(new CommandDefinition("""
            SELECT id, factura_id, fecha_inicio_amortizacion, valor_adquisicion FROM inmov.inmovilizado
            WHERE id = @inmovilizadoId AND empresa_id = @empresaId FOR UPDATE
            """, new { inmovilizadoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El elemento de inmovilizado no existe.");
        if (i.ValorAdquisicion <= RegularizacionBienesInversion.ImporteMinimo)
            throw new ReglaContableException($"Solo son bienes de inversión los de más de {RegularizacionBienesInversion.ImporteMinimo:N2} € (art. 108 LIVA).");
        if (await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM inmov.bien_inversion WHERE inmovilizado_id = @inmovilizadoId)", new { inmovilizadoId }, tx, cancellationToken: ct)))
            throw new ReglaContableException("El elemento ya está en el registro de bienes de inversión.");

        var anio = cmd.AnioInicio ?? i.FechaInicioAmortizacion.Year;
        if (anio is < 2000 or > 2100) throw new ReglaContableException("El año de inicio no es válido.");

        // Cuota y % de la factura de compra, si el elemento la tiene
        var fac = i.FacturaId is { } fid
            ? await cn.QuerySingleAsync<(decimal Cuota, decimal? Pct)>(new CommandDefinition("""
                SELECT COALESCE(SUM(cuota) FILTER (WHERE es_bien_inversion), 0) AS cuota,
                       MIN(porcentaje_deduccion) FILTER (WHERE es_bien_inversion) AS pct
                FROM fiscal.factura_iva WHERE factura_id = @fid
                """, new { fid }, tx, cancellationToken: ct))
            : (0m, null);
        var cuota = cmd.CuotaSoportada ?? (fac.Cuota != 0 ? fac.Cuota
            : throw new ReglaContableException("Indica la cuota soportada: el elemento no tiene factura con líneas marcadas como bien de inversión."));
        if (cuota <= 0) throw new ReglaContableException("La cuota soportada debe ser mayor que cero.");

        var prorrata = cmd.ProrrataInicial ?? await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT COALESCE(porcentaje_definitivo, porcentaje_provisional) FROM fiscal.prorrata
            WHERE empresa_id = @empresaId AND anio = @anio AND sector_diferenciado IS NOT DISTINCT FROM @sector
            """, new { empresaId, anio = (short)anio, sector = (short?)cmd.SectorDiferenciado }, tx, cancellationToken: ct))
            ?? (fac.Pct is { } pct ? (int)Math.Round(pct) : 100);
        if (prorrata is < 0 or > 100) throw new ReglaContableException("La prorrata inicial debe estar entre 0 y 100.");

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO inmov.bien_inversion (inmovilizado_id, es_inmueble, anio_inicio_utilizacion, anios_regularizacion, cuota_soportada,
                                              cuota_deducida, prorrata_inicial, sector_diferenciado, transmision_exenta)
            VALUES (@inmovilizadoId, @inmueble, @anio, @anios, @cuota, @deducida, @prorrata, @sector, @exenta)
            """, new { inmovilizadoId, inmueble = cmd.EsInmueble, anio = (short)anio, anios = (short)(cmd.EsInmueble ? 10 : 5), cuota,
                       deducida = Math.Round(cuota * prorrata / 100m, 2, MidpointRounding.AwayFromZero), prorrata = (short)prorrata,
                       sector = (short?)cmd.SectorDiferenciado, exenta = cmd.TransmisionExenta }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (await LibroAsync(empresaId, anio, ct)).Bienes.First(b => b.InmovilizadoId == inmovilizadoId);
    }

    public async Task QuitarAsync(long empresaId, long inmovilizadoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var id = await cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT b.id FROM inmov.bien_inversion b JOIN inmov.inmovilizado i ON i.id = b.inmovilizado_id
            WHERE b.inmovilizado_id = @inmovilizadoId AND i.empresa_id = @empresaId FOR UPDATE OF b
            """, new { inmovilizadoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El elemento no está en el registro de bienes de inversión.");
        if (await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM inmov.bien_inversion_regularizacion WHERE bien_inversion_id = @id)", new { id }, tx, cancellationToken: ct)))
            throw new ReglaContableException("El bien ya tiene regularizaciones contabilizadas: anúlalas antes de quitarlo del registro.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM inmov.bien_inversion WHERE id = @id", new { id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // =====================================================================
    // Libro registro de bienes de inversión
    // =====================================================================
    public async Task<LibroBienesInversionDto> LibroAsync(long empresaId, int anio, CancellationToken ct)
    {
        if (anio is < 2000 or > 2100) throw new ReglaContableException("El año no es válido.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var datos = await DatosAsync(cn, null, empresaId, ct);
        var hechas = (await HechasAsync(cn, null, empresaId, ct)).ToLookup(h => h.BienId);
        var avisos = new List<string>();

        var lista = new List<BienInversionDto>();
        foreach (var f in datos.Filas)
        {
            var b = datos.Bien(f);
            var dto = new BienInversionDto
            {
                Id = f.Id, InmovilizadoId = f.InmovilizadoId, Codigo = f.Codigo, Descripcion = f.Descripcion, EsInmueble = f.EsInmueble,
                AnioInicio = b.AnioInicio, Anios = b.Anios, UltimoAnio = b.UltimoAnio, CuotaSoportada = b.CuotaSoportada,
                ProrrataInicial = b.ProrrataInicial, DeduccionInicial = b.DeduccionInicial, SectorDiferenciado = f.SectorDiferenciado,
                TransmisionExenta = f.TransmisionExenta, Estado = f.Estado, FechaBaja = f.FechaBaja
            };
            dto.Cuadro.Add(new AnioBienInversionDto
            {
                Anio = b.AnioInicio, Tipo = "INICIO", Prorrata = b.ProrrataInicial, Importe = 0,
                Nota = datos.Definitiva(b.AnioInicio, f.SectorDiferenciado) is null && b.ProrrataInicial != 100
                    ? "Prorrata del año de inicio aún provisional: el cálculo cambiará con la definitiva." : null
            });
            for (var y = b.AnioInicio; y <= b.UltimoAnio; y++)
            {
                var hecha = hechas[f.Id].FirstOrDefault(h => h.Anio == y);
                RegularizacionBien? r = Equals(b.FechaTransmision?.Year, y) ? RegularizacionBienesInversion.Transmision(b)
                    : RegularizacionBienesInversion.Anual(b, y, datos.Definitiva(y, f.SectorDiferenciado));
                if (hecha is not null)
                {
                    dto.Cuadro.Add(new AnioBienInversionDto
                    {
                        Anio = y, Tipo = hecha.Tipo, Prorrata = hecha.ProrrataDefinitiva, AniosRegularizados = hecha.AniosRegularizados,
                        Importe = hecha.ImporteRegularizacion, Contabilizada = true, AsientoId = hecha.AsientoId, AsientoNumero = hecha.AsientoNumero,
                        Nota = r is not null && r.Importe != hecha.ImporteRegularizacion
                            ? $"Con los datos actuales saldría {r.Importe:N2}: anula el asiento y vuelve a contabilizar." : null
                    });
                    continue;
                }
                if (r is null) continue;
                // Años futuros sin prorrata todavía: no se muestran como pendientes
                if (r.Tipo == "ANUAL" && r.Prorrata == 0 && r.Importe == 0 && r.Nota?.StartsWith("Falta", StringComparison.Ordinal) == true && y > anio) continue;
                dto.Cuadro.Add(new AnioBienInversionDto
                {
                    Anio = y, Tipo = r.Tipo, Prorrata = r.Nota?.StartsWith("Falta", StringComparison.Ordinal) == true ? null : r.Prorrata,
                    AniosRegularizados = r.AniosRegularizados, Importe = r.Importe, Nota = r.Nota
                });
            }
            lista.Add(dto);
        }

        var delAnio = lista.SelectMany(b => b.Cuadro.Where(c => c.Anio == anio && c.Tipo != "INICIO")).ToList();
        var faltan = lista.Where(b => b.Cuadro.Any(c => c.Anio == anio && c.Tipo == "ANUAL" && c.Prorrata is null)).Select(b => b.Codigo).ToList();
        if (faltan.Count > 0)
            avisos.Add($"Falta el porcentaje definitivo de prorrata de {anio} para regularizar {string.Join(", ", faltan)}. " +
                       "Si ese año no hubo prorrata, registra 100 % como definitivo.");
        var pendiente = delAnio.Where(c => !c.Contabilizada && c.Importe != 0).Sum(c => c.Importe);
        if (pendiente != 0)
            avisos.Add($"Regularización de {anio} sin contabilizar: {pendiente:N2}.");
        return new LibroBienesInversionDto(anio, lista, delAnio.Sum(c => c.Importe), delAnio.Where(c => c.Contabilizada).Sum(c => c.Importe), avisos);
    }

    private static async Task<List<HechaFila>> HechasAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CancellationToken ct)
        => (await cn.QueryAsync<HechaFila>(new CommandDefinition("""
               SELECT r.bien_inversion_id AS bien_id, r.anio, r.tipo, r.prorrata_definitiva, r.anios_regularizados, r.importe_regularizacion,
                      r.asiento_id, s.numero AS asiento_numero, r.fecha
               FROM inmov.bien_inversion_regularizacion r
               JOIN inmov.bien_inversion b ON b.id = r.bien_inversion_id
               JOIN inmov.inmovilizado i ON i.id = b.inmovilizado_id
               LEFT JOIN conta.asiento s ON s.id = r.asiento_id
               WHERE i.empresa_id = @empresaId
               """, new { empresaId }, tx, cancellationToken: ct))).AsList();

    // =====================================================================
    // Asiento de regularización
    // =====================================================================
    public async Task<RegularizacionContabilizadaDto> ContabilizarAsync(long empresaId, long ejercicioId, long usuarioId, RegularizarBienesCmd cmd,
        CancellationToken ct)
    {
        var p = PeriodoFiscal.Resolver(cmd.Anio, string.IsNullOrWhiteSpace(cmd.Periodo) ? "4T" : cmd.Periodo);
        var fecha = cmd.Fecha ?? p.Fin;

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"bienes-inversion|{empresaId}" }, tx, cancellationToken: ct));

        var ej = await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
            "SELECT fecha_inicio, fecha_fin, estado FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
            new { ejercicioId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        if (fecha < ej.FechaInicio || fecha > ej.FechaFin) throw new ReglaContableException("La fecha del asiento debe estar dentro del ejercicio seleccionado.");

        var datos = await DatosAsync(cn, tx, empresaId, ct);
        var hechas = await HechasAsync(cn, tx, empresaId, ct);
        var regs = RegularizacionBienesInversion.DelPeriodo(datos.Filas.Select(datos.Bien), p, datos.Definitiva)
            .Where(r => r.Importe != 0 && !hechas.Any(h => h.BienId == r.BienId && h.Anio == r.Anio)).ToList();
        if (regs.Count == 0)
            throw new ReglaContableException($"No hay regularizaciones de bienes de inversión pendientes en {p.Codigo} {p.Anio}.",
                "La anual va en el último periodo del año y necesita la prorrata definitiva; la de una venta, en el periodo de la venta.");

        var soportado = await ConsultasContables.CuentaPorRolAsync(cn, tx, empresaId, "IVA_SOPORTADO", "4720", ct)
            ?? throw new ReglaContableException("No se encuentra la cuenta de IVA soportado (472).");
        var positivos = await cn.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT inmov.asegurar_cuenta(@empresaId, '6392')",
            new { empresaId }, tx, cancellationToken: ct)) ?? throw new ReglaContableException("La empresa no tiene la cuenta 6392.");
        var negativos = await cn.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT inmov.asegurar_cuenta(@empresaId, '6342')",
            new { empresaId }, tx, cancellationToken: ct)) ?? throw new ReglaContableException("La empresa no tiene la cuenta 6342.");

        var apuntes = new List<ApunteGenerado>();
        foreach (var r in regs)
        {
            var f = datos.Filas.First(x => x.Id == r.BienId);
            var canal = f.CanalId ?? throw new ReglaContableException($"El elemento {f.Codigo} no tiene canal.");
            if (r.Importe > 0) { apuntes.Add(new(soportado, canal, r.Importe, 0)); apuntes.Add(new(positivos, canal, 0, r.Importe)); }
            else { apuntes.Add(new(negativos, canal, -r.Importe, 0)); apuntes.Add(new(soportado, canal, 0, -r.Importe)); }
        }

        var concepto = $"Regularización de bienes de inversión {p.Codigo} {p.Anio}";
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, new CrearAsientoCmd
        {
            Fecha = fecha, DiarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct), Concepto = concepto,
            Apuntes = ConsultasContables.AApuntes(apuntes, concepto)
        }, "BIEN_INVERSION", null, $"BI {p.Anio} {p.Codigo}", ct);

        foreach (var r in regs)
        {
            var b = datos.Bien(datos.Filas.First(x => x.Id == r.BienId));
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO inmov.bien_inversion_regularizacion (bien_inversion_id, anio, prorrata_definitiva, importe_regularizacion, tipo,
                                                                 anios_regularizados, prorrata_inicial, fecha, asiento_id)
                VALUES (@bien, @anio, @prorrata, @importe, @tipo, @anios, @inicial, @fecha, @asiento)
                """, new { bien = r.BienId, anio = (short)r.Anio, prorrata = (short)r.Prorrata, importe = r.Importe, tipo = r.Tipo,
                           anios = (short)r.AniosRegularizados, inicial = (short)b.ProrrataInicial, fecha, asiento = a.Id }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return new RegularizacionContabilizadaDto(a.Id, a.Numero, regs.Sum(r => r.Importe), regs.Select(r => new AnioBienInversionDto
        {
            Anio = r.Anio, Tipo = r.Tipo, Prorrata = r.Prorrata, AniosRegularizados = r.AniosRegularizados, Importe = r.Importe, Nota = r.Nota,
            Contabilizada = true, AsientoId = a.Id, AsientoNumero = a.Numero
        }).ToList());
    }

    public async Task AnularAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var a = await cn.QuerySingleOrDefaultAsync<AsientoFila>(new CommandDefinition("""
            SELECT s.id, s.estado, s.ejercicio_id, e.estado AS ej_estado FROM conta.asiento s JOIN core.ejercicio e ON e.id = s.ejercicio_id
            WHERE s.id = @asientoId AND s.empresa_id = @empresaId AND s.origen = 'BIEN_INVERSION' FOR UPDATE OF s
            """, new { asientoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El asiento no es una regularización de bienes de inversión.");
        if (a.Estado == "ANULADO") throw new ReglaContableException("El asiento ya está anulado.");
        if (a.EjercicioId != ejercicioId) throw new ReglaContableException("El asiento pertenece a otro ejercicio: selecciónalo para anularlo.");
        if (a.EjEstado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM inmov.bien_inversion_regularizacion WHERE asiento_id = @asientoId",
            new { asientoId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'ANULADO' WHERE id = @asientoId",
            new { asientoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
