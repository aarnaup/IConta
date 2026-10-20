using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Contabilidad.Domain.Facturacion;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ICriterioCajaRepositorio
{
    Task<ConfiguracionFiscalDto> ConfiguracionAsync(long empresaId, CancellationToken ct);
    Task<ConfiguracionFiscalDto> GuardarConfiguracionAsync(long empresaId, GuardarConfiguracionFiscalCmd cmd, CancellationToken ct);
    Task<DevengoCajaDto> DelPeriodoAsync(long empresaId, int anio, string periodo, CancellationToken ct);
    Task<TraspasoCajaHechoDto> TraspasarAsync(long empresaId, long ejercicioId, long usuarioId, TraspasoCajaCmd cmd, CancellationToken ct);
    Task AnularAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct);
}

/// <summary>
/// Criterio de caja: las facturas se contabilizan con el impuesto en cuentas de pendiente (4779 repercutido, 4729 soportado) y, al
/// cobrar o pagar, ese impuesto se devenga o se deduce. El cálculo está en <see cref="CriterioCaja"/>; aquí se leen facturas y cobros,
/// se lleva al 303/420 y se contabiliza el traspaso del periodo: 4779 a 477 y 472 a 4729.
/// </summary>
public sealed class CriterioCajaRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : ICriterioCajaRepositorio
{
    private sealed class FacturaFila
    {
        public long Id { get; set; }
        public string Libro { get; set; } = "";
        public bool Rectificativa { get; set; }
        public DateOnly FechaOperacion { get; set; }
        public DateOnly FechaContable { get; set; }
        public decimal Total { get; set; }
        public long? CanalId { get; set; }
        public string Numero { get; set; } = "";
        public string Tercero { get; set; } = "";
        public long? RectificadaId { get; set; }
    }
    private sealed class LineaFila
    {
        public long FacturaId { get; set; }
        public long TipoIvaId { get; set; }
        public decimal Porcentaje { get; set; }
        public decimal PorcentajeRecargo { get; set; }
        public bool EsBienInversion { get; set; }
        public decimal Base { get; set; }
        public decimal Cuota { get; set; }
        public decimal CuotaRecargo { get; set; }
        public decimal CuotaDeducible { get; set; }
    }
    private sealed class CobroFila { public long FacturaId { get; set; } public DateOnly Fecha { get; set; } public decimal Importe { get; set; } }
    private sealed class HechoFila { public long FacturaId { get; set; } public long AsientoId { get; set; } public int Numero { get; set; } }
    private sealed class EjercicioFila { public DateOnly FechaInicio { get; set; } public DateOnly FechaFin { get; set; } public string Estado { get; set; } = ""; }

    private sealed record Datos(IReadOnlyList<FacturaCaja> Facturas, IReadOnlyList<CobroCaja> Cobros, IReadOnlyDictionary<long, FacturaFila> Filas);

    /// <summary>Facturas en criterio de caja que pueden devengar algo en el periodo, con sus líneas interiores y sus cobros/pagos.</summary>
    private static async Task<Datos> DatosAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, string impuesto, PeriodoFiscal p,
        CancellationToken ct)
    {
        var filas = (await cn.QueryAsync<FacturaFila>(new CommandDefinition("""
            SELECT f.id, f.libro, (f.tipo_factura = 'RECTIFICATIVA') AS rectificativa,
                   COALESCE(f.fecha_operacion, f.fecha_expedicion) AS fecha_operacion, f.fecha_contable, f.total, f.canal_id,
                   COALESCE(f.serie, '') || f.numero AS numero, t.nombre AS tercero,
                   CASE WHEN o.criterio_caja AND o.estado = 'CONTABILIZADA' THEN o.id END AS rectificada_id
            FROM fiscal.factura f JOIN conta.tercero t ON t.id = f.tercero_id
            LEFT JOIN fiscal.factura o ON o.id = f.factura_rectificada_id
            WHERE f.empresa_id = @empresaId AND f.estado = 'CONTABILIZADA' AND f.criterio_caja AND f.fecha_contable <= @fin
              -- el límite de una rectificativa ligada es el de su factura original
              AND make_date(EXTRACT(YEAR FROM COALESCE(CASE WHEN o.criterio_caja THEN COALESCE(o.fecha_operacion, o.fecha_expedicion) END,
                                                       f.fecha_operacion, f.fecha_expedicion))::int + 1, 12, 31) >= @inicio
              AND EXISTS (SELECT 1 FROM fiscal.factura_iva fi JOIN cat.tipo_iva ti ON ti.id = fi.tipo_iva_id
                          WHERE fi.factura_id = f.id AND fi.tipo_operacion = 'INTERIOR' AND ti.impuesto = @impuesto)
            ORDER BY f.fecha_contable, f.id
            """, new { empresaId, impuesto, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct))).AsList();
        var ids = filas.Select(f => f.Id).ToArray();
        var lineas = (await cn.QueryAsync<LineaFila>(new CommandDefinition("""
            SELECT fi.factura_id, fi.tipo_iva_id, fi.porcentaje, fi.porcentaje_recargo, fi.es_bien_inversion,
                   fi.base, fi.cuota, fi.cuota_recargo, fi.cuota_deducible
            FROM fiscal.factura_iva fi WHERE fi.factura_id = ANY(@ids) AND fi.tipo_operacion = 'INTERIOR'
            """, new { ids }, tx, cancellationToken: ct))).ToLookup(l => l.FacturaId);
        var cobros = (await cn.QueryAsync<CobroFila>(new CommandDefinition("""
            SELECT v.factura_id, cp.fecha, cp.importe
            FROM tes.cobro_pago cp JOIN tes.vencimiento v ON v.id = cp.vencimiento_id
            WHERE v.factura_id = ANY(@ids)
            UNION ALL
            SELECT v.factura_id, rv.fecha_devolucion, -cp.importe            -- recibos devueltos: dejan de estar cobrados
            FROM tes.remesa_vencimiento rv JOIN tes.cobro_pago cp ON cp.id = rv.cobro_pago_id
            JOIN tes.vencimiento v ON v.id = cp.vencimiento_id
            WHERE rv.estado = 'DEVUELTO' AND v.factura_id = ANY(@ids)
            """, new { ids }, tx, cancellationToken: ct))).Select(c => new CobroCaja(c.FacturaId, c.Fecha, c.Importe)).ToList();

        var facturas = filas.Select(f => new FacturaCaja(f.Id, f.Libro, f.Rectificativa, f.FechaOperacion, f.FechaContable, f.Total, f.CanalId,
            lineas[f.Id].Select(l => new LineaCaja(l.TipoIvaId, l.Porcentaje, l.PorcentajeRecargo, l.EsBienInversion, l.Base, l.Cuota,
                l.CuotaRecargo, l.CuotaDeducible)).ToList(), f.RectificadaId)).ToList();
        return new Datos(facturas, cobros, filas.ToDictionary(f => f.Id));
    }

    /// <summary>Para el 303/420: grupos devengados por caja en el periodo e información adicional (facturas del periodo).</summary>
    internal static async Task<(IReadOnlyList<GrupoIva> Grupos, InformacionCaja? Info, List<string> Avisos)> ParaDeclaracionAsync(
        NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, string impuesto, PeriodoFiscal p, CancellationToken ct)
    {
        var datos = await DatosAsync(cn, tx, empresaId, impuesto, p, ct);
        if (datos.Facturas.Count == 0) return ([], null, []);
        var devengos = CriterioCaja.DelPeriodo(datos.Facturas, datos.Cobros, p);
        var delPeriodo = datos.Facturas.Where(f => f.FechaContable >= p.Inicio && f.FechaContable <= p.Fin).ToList();
        var info = new InformacionCaja(
            delPeriodo.Where(f => f.Libro == "EMITIDAS").Sum(f => f.Lineas.Sum(l => l.Base)),
            delPeriodo.Where(f => f.Libro == "EMITIDAS").Sum(f => f.Lineas.Sum(l => l.Cuota + l.CuotaRecargo)),
            delPeriodo.Where(f => f.Libro == "RECIBIDAS").Sum(f => f.Lineas.Sum(l => l.Base)),
            delPeriodo.Where(f => f.Libro == "RECIBIDAS").Sum(f => f.Lineas.Sum(l => l.Cuota)));
        var avisos = new List<string>();
        var limite = devengos.Count(d => CriterioCaja.FechaLimite(d.Factura) >= p.Inicio && CriterioCaja.FechaLimite(d.Factura) <= p.Fin
                                         && CriterioCaja.ProporcionEn(d.Factura, datos.Facturas, datos.Cobros, p.Fin.AddDays(-1)) < 1);
        if (limite > 0)
            avisos.Add($"{limite} factura(s) en criterio de caja llegan al 31 de diciembre del año siguiente sin cobrar o pagar del todo: el resto se devenga ahora.");
        var hechos = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM fiscal.devengo_caja WHERE empresa_id = @empresaId AND anio = @anio AND periodo = @periodo",
            new { empresaId, anio = (short)p.Anio, periodo = p.Codigo }, tx, cancellationToken: ct));
        if (devengos.Count > 0 && hechos == 0 && !p.EsAnual)
            avisos.Add("Criterio de caja: contabiliza el traspaso del IVA devengado en el periodo (Modelos fiscales → Criterio de caja) antes de liquidar.");
        return (CriterioCaja.Grupos(devengos), info, avisos);
    }

    /// <summary>Impuesto de criterio de caja devengado en el periodo que aún no se ha traspasado a 477/472.</summary>
    internal static async Task<decimal> PendienteAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, string impuesto, PeriodoFiscal p,
        CancellationToken ct)
    {
        var datos = await DatosAsync(cn, tx, empresaId, impuesto, p, ct);
        if (datos.Facturas.Count == 0) return 0;
        var hechos = (await HechosAsync(cn, tx, empresaId, p, ct)).Select(h => h.FacturaId).ToHashSet();
        return CriterioCaja.DelPeriodo(datos.Facturas, datos.Cobros, p).Where(d => !hechos.Contains(d.Factura.Id)).Sum(d => Math.Abs(d.Cuota));
    }

    // =====================================================================
    // Configuración
    // =====================================================================
    public async Task<ConfiguracionFiscalDto> ConfiguracionAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<ConfiguracionFiscalDto>(new CommandDefinition(
                   "SELECT regimen_iva, impuesto_indirecto, regimen_irpf, sii, tributa_is, is_tipo, is_porcentaje, is_modalidad_202 FROM core.empresa WHERE id = @empresaId",
                   new { empresaId }, cancellationToken: ct))
               ?? throw new ReglaContableException("La empresa no existe.");
    }

    public async Task<ConfiguracionFiscalDto> GuardarConfiguracionAsync(long empresaId, GuardarConfiguracionFiscalCmd cmd, CancellationToken ct)
    {
        var regimen = (cmd.RegimenIva ?? "").Trim().ToUpperInvariant();
        if (regimen is not ("GENERAL" or "CRITERIO_CAJA" or "SIMPLIFICADO" or "RECARGO_EQUIVALENCIA" or "AGRICULTURA_GYP" or "COMERCIANTE_MINORISTA"))
            throw new ReglaContableException("Régimen no disponible: GENERAL, CRITERIO_CAJA, SIMPLIFICADO, RECARGO_EQUIVALENCIA, AGRICULTURA_GYP o COMERCIANTE_MINORISTA.");
        var irpf = cmd.RegimenIrpf?.Trim().ToUpperInvariant();
        if (irpf is not (null or "" or "DIRECTA_NORMAL" or "DIRECTA_SIMPLIFICADA" or "OBJETIVA"))
            throw new ReglaContableException("Régimen de IRPF no válido: DIRECTA_NORMAL, DIRECTA_SIMPLIFICADA u OBJETIVA (o vacío para sociedades).");
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var actual = await cn.QuerySingleAsync<ConfiguracionFiscalDto>(new CommandDefinition(
            "SELECT regimen_iva, impuesto_indirecto, regimen_irpf, sii, tributa_is, is_tipo, is_porcentaje, is_modalidad_202 FROM core.empresa WHERE id = @empresaId FOR UPDATE",
            new { empresaId }, tx, cancellationToken: ct));
        if (actual.ImpuestoIndirecto == "IGIC" && regimen == "RECARGO_EQUIVALENCIA")
            throw new ReglaContableException("En el IGIC no hay recargo de equivalencia: el minorista va en el régimen especial del comerciante minorista.");
        if (actual.ImpuestoIndirecto != "IGIC" && regimen == "COMERCIANTE_MINORISTA")
            throw new ReglaContableException("El régimen especial del comerciante minorista es del IGIC; en el IVA es el recargo de equivalencia.");
        if (regimen == "COMERCIANTE_MINORISTA" && (irpf is null ? actual.RegimenIrpf : irpf.Length == 0 ? null : irpf) is null)
            throw new ReglaContableException("El régimen del comerciante minorista es para personas físicas.", "Indica su régimen de IRPF.");
        var irpfFinal = irpf is null ? actual.RegimenIrpf : irpf.Length == 0 ? null : irpf;
        if (regimen == "SIMPLIFICADO" && irpfFinal != "OBJETIVA")
            throw new ReglaContableException("El régimen simplificado del IVA va con la estimación objetiva del IRPF (módulos).",
                "Indica también el régimen de IRPF OBJETIVA.");
        if (regimen == "AGRICULTURA_GYP" && actual.ImpuestoIndirecto != "IVA")
            throw new ReglaContableException("El régimen especial de la agricultura, ganadería y pesca de la aplicación es el del IVA.");
        if (regimen == "AGRICULTURA_GYP" && irpfFinal is null)
            throw new ReglaContableException("El régimen especial de la agricultura, ganadería y pesca es para personas físicas (o entidades en atribución de rentas).",
                "Indica su régimen de IRPF.");
        if (regimen == "RECARGO_EQUIVALENCIA" && irpfFinal is null)
            throw new ReglaContableException("El recargo de equivalencia es para comerciantes minoristas personas físicas (o entidades en atribución de rentas).",
                "Indica su régimen de IRPF.");
        var isTipo = cmd.IsTipo?.Trim().ToUpperInvariant();
        if (isTipo is not null && !Domain.Fiscal.TipoGravamenIs.Clases.Contains(isTipo))
            throw new ReglaContableException("Tipo de gravamen no válido: GENERAL, NUEVA_CREACION, REDUCIDA_DIMENSION, MICRO o PERSONALIZADO.");
        if ((isTipo ?? actual.IsTipo) == "PERSONALIZADO" && (cmd.IsPorcentaje ?? actual.IsPorcentaje) is not (>= 0 and <= 100))
            throw new ReglaContableException("Con el tipo personalizado indica el porcentaje (0 a 100).");
        var modalidad = cmd.IsModalidad202?.Trim().ToUpperInvariant();
        if (modalidad is not (null or "ART_40_2" or "ART_40_3"))
            throw new ReglaContableException("Modalidad del pago fraccionado no válida: ART_40_2 o ART_40_3.");
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE core.empresa SET regimen_iva = @regimen, regimen_irpf = @irpfFinal, sii = COALESCE(@sii, sii),
                   tributa_is = COALESCE(@tributaIs, tributa_is), is_tipo = COALESCE(@isTipo, is_tipo),
                   is_porcentaje = CASE WHEN COALESCE(@isTipo, is_tipo) = 'PERSONALIZADO' THEN COALESCE(@isPorcentaje, is_porcentaje) END,
                   is_modalidad_202 = COALESCE(@modalidad, is_modalidad_202)
            WHERE id = @empresaId
            """, new { regimen, irpfFinal, sii = cmd.Sii, tributaIs = cmd.TributaIs, isTipo, isPorcentaje = cmd.IsPorcentaje, modalidad, empresaId },
            tx, cancellationToken: ct));
        if (regimen == "CRITERIO_CAJA")
            await cn.ExecuteAsync(new CommandDefinition("SELECT fiscal.asegurar_cuentas_caja(@empresaId)", new { empresaId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return await ConfiguracionAsync(empresaId, ct);
    }

    // =====================================================================
    // Traspaso del periodo
    // =====================================================================
    private static async Task<string> ImpuestoAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CancellationToken ct)
        => await cn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT impuesto_indirecto FROM core.empresa WHERE id = @empresaId",
               new { empresaId }, tx, cancellationToken: ct)) ?? throw new ReglaContableException("La empresa no existe.");

    public async Task<DevengoCajaDto> DelPeriodoAsync(long empresaId, int anio, string periodo, CancellationToken ct)
    {
        var p = PeriodoFiscal.Resolver(anio, periodo);
        await using var cn = await conexiones.AbrirAsync(ct);
        var datos = await DatosAsync(cn, null, empresaId, await ImpuestoAsync(cn, null, empresaId, ct), p, ct);
        var devengos = CriterioCaja.DelPeriodo(datos.Facturas, datos.Cobros, p);
        var hechos = (await HechosAsync(cn, null, empresaId, p, ct)).ToDictionary(h => h.FacturaId);
        var lineas = devengos.Select(d =>
        {
            var f = datos.Filas[d.Factura.Id];
            var h = hechos.GetValueOrDefault(d.Factura.Id);
            return new LineaTraspasoCajaDto
            {
                FacturaId = f.Id, Libro = f.Libro, Factura = f.Numero, Tercero = f.Tercero, Total = f.Total, Base = d.Base, Cuota = d.Cuota,
                Traspasado = h is not null, AsientoId = h?.AsientoId, AsientoNumero = h?.Numero
            };
        }).ToList();
        var avisos = new List<string>();
        if (datos.Facturas.Count == 0) avisos.Add("No hay facturas en criterio de caja con impuesto pendiente en este periodo.");
        return new DevengoCajaDto(p.Anio, p.Codigo, lineas, lineas.Where(l => l.Libro == "EMITIDAS").Sum(l => l.Cuota),
            lineas.Where(l => l.Libro == "RECIBIDAS").Sum(l => l.Cuota), lineas.Where(l => !l.Traspasado).Sum(l => l.Cuota), avisos);
    }

    private static async Task<List<HechoFila>> HechosAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, PeriodoFiscal p, CancellationToken ct)
        => (await cn.QueryAsync<HechoFila>(new CommandDefinition("""
               SELECT d.factura_id, d.asiento_id, s.numero FROM fiscal.devengo_caja d JOIN conta.asiento s ON s.id = d.asiento_id
               WHERE d.empresa_id = @empresaId AND d.anio = @anio AND d.periodo = @periodo
               """, new { empresaId, anio = (short)p.Anio, periodo = p.Codigo }, tx, cancellationToken: ct))).AsList();

    public async Task<TraspasoCajaHechoDto> TraspasarAsync(long empresaId, long ejercicioId, long usuarioId, TraspasoCajaCmd cmd, CancellationToken ct)
    {
        var p = PeriodoFiscal.Resolver(cmd.Anio, cmd.Periodo);
        if (p.EsAnual) throw new ReglaContableException("El traspaso se hace por periodos de liquidación (trimestres o meses).");
        var fecha = cmd.Fecha ?? p.Fin;

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"criterio-caja|{empresaId}" }, tx, cancellationToken: ct));
        var ej = await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
            "SELECT fecha_inicio, fecha_fin, estado FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
            new { ejercicioId, empresaId }, tx, cancellationToken: ct)) ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        if (fecha < ej.FechaInicio || fecha > ej.FechaFin) throw new ReglaContableException("La fecha del asiento debe estar dentro del ejercicio seleccionado.");
        if (fecha < p.Fin) throw new ReglaContableException("La fecha del asiento no puede ser anterior al fin del periodo.");

        var datos = await DatosAsync(cn, tx, empresaId, await ImpuestoAsync(cn, tx, empresaId, ct), p, ct);
        var hechos = (await HechosAsync(cn, tx, empresaId, p, ct)).Select(h => h.FacturaId).ToHashSet();
        var devengos = CriterioCaja.DelPeriodo(datos.Facturas, datos.Cobros, p).Where(d => !hechos.Contains(d.Factura.Id)).ToList();
        if (devengos.Count == 0)
            throw new ReglaContableException($"No hay IVA de criterio de caja pendiente de traspasar en {p.Codigo} {p.Anio}.");

        await cn.ExecuteAsync(new CommandDefinition("SELECT fiscal.asegurar_cuentas_caja(@empresaId)", new { empresaId }, tx, cancellationToken: ct));
        var reglas = (await cn.QueryAsync<(string Rol, long? TipoIvaId, long? TipoRetencionId, long CuentaId)>(new CommandDefinition(
            "SELECT rol, tipo_iva_id, tipo_retencion_id, cuenta_id FROM conta.cuenta_automatica WHERE empresa_id = @empresaId",
            new { empresaId }, tx, cancellationToken: ct))).Select(r => new CuentaAutomatica(r.Rol, r.TipoIvaId, r.TipoRetencionId, r.CuentaId)).ToList();
        var cuentas = new ResolutorCuentas(reglas, null, null);
        var canalDefecto = await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM canal.canal WHERE empresa_id = @empresaId ORDER BY id LIMIT 1", new { empresaId }, tx, cancellationToken: ct));

        var apuntes = new List<ApunteGenerado>();
        void Par(long debe, long haber, long canal, decimal importe)
        {
            if (importe == 0) return;
            if (importe < 0) (debe, haber, importe) = (haber, debe, -importe);
            apuntes.Add(new(debe, canal, importe, 0)); apuntes.Add(new(haber, canal, 0, importe));
        }
        foreach (var d in devengos)
        {
            var canal = d.Factura.CanalId ?? canalDefecto;
            foreach (var l in d.Lineas)
                if (d.Factura.Libro == "EMITIDAS")
                    Par(cuentas.Resolver(null, "IVA_REPERCUTIDO_CAJA", l.TipoIvaId), cuentas.Resolver(null, "IVA_REPERCUTIDO", l.TipoIvaId), canal,
                        l.Cuota + l.CuotaRecargo);
                else
                    Par(cuentas.Resolver(null, "IVA_SOPORTADO", l.TipoIvaId), cuentas.Resolver(null, "IVA_SOPORTADO_CAJA", l.TipoIvaId), canal,
                        l.CuotaDeducible);
        }
        if (apuntes.Count == 0) throw new ReglaContableException("Lo devengado en el periodo no tiene cuota que traspasar.");

        var concepto = $"Criterio de caja: impuesto devengado {p.Codigo} {p.Anio}";
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, new CrearAsientoCmd
        {
            Fecha = fecha, DiarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct), Concepto = concepto,
            Apuntes = ConsultasContables.AApuntes(apuntes, concepto)
        }, "CRITERIO_CAJA", null, $"CAJA {p.Anio} {p.Codigo}", ct);

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.devengo_caja (empresa_id, factura_id, anio, periodo, base, cuota, asiento_id)
            VALUES (@empresaId, @facturaId, @anio, @periodo, @base, @cuota, @asientoId)
            """, devengos.Select(d => new { empresaId, facturaId = d.Factura.Id, anio = (short)p.Anio, periodo = p.Codigo, @base = d.Base,
                                            cuota = d.Cuota, asientoId = a.Id }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new TraspasoCajaHechoDto(a.Id, a.Numero, devengos.Where(d => d.Factura.Libro == "EMITIDAS").Sum(d => d.Cuota),
            devengos.Where(d => d.Factura.Libro == "RECIBIDAS").Sum(d => d.Cuota));
    }

    public async Task AnularAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var a = await cn.QuerySingleOrDefaultAsync<(long EjercicioId, string Estado, string EjEstado)?>(new CommandDefinition("""
            SELECT s.ejercicio_id, s.estado, e.estado AS ej_estado FROM conta.asiento s JOIN core.ejercicio e ON e.id = s.ejercicio_id
            WHERE s.id = @asientoId AND s.empresa_id = @empresaId AND s.origen = 'CRITERIO_CAJA' FOR UPDATE OF s
            """, new { asientoId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El asiento no es un traspaso de criterio de caja.");
        if (a.Estado == "ANULADO") throw new ReglaContableException("El asiento ya está anulado.");
        if (a.EjercicioId != ejercicioId) throw new ReglaContableException("El asiento pertenece a otro ejercicio: selecciónalo para anularlo.");
        if (a.EjEstado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM fiscal.devengo_caja WHERE asiento_id = @asientoId", new { asientoId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'ANULADO' WHERE id = @asientoId", new { asientoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
