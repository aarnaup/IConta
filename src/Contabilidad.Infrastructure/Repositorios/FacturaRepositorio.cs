using Contabilidad.Domain;
using Contabilidad.Domain.Facturacion;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;
using System.Text.Json;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IFacturaRepositorio
{
    Task<FacturaPrevisualizacion> PrevisualizarAsync(long empresaId, long ejercicioId, CrearFacturaCmd cmd, CancellationToken ct);
    Task<FacturaCreada> CrearAsync(long empresaId, long ejercicioId, long usuarioId, CrearFacturaCmd cmd, CancellationToken ct);
    Task<IReadOnlyList<FacturaResumenDto>> ListarAsync(long empresaId, long ejercicioId, string? libro, int limite, CancellationToken ct);
    Task<IReadOnlyList<PendienteDto>> ListarPendientesAsync(long empresaId, long ejercicioId, CancellationToken ct);
    /// <summary>Contabiliza una factura en borrador: genera su asiento con la plantilla y la pasa a CONTABILIZADA.</summary>
    Task<FacturaCreada> AprobarAsync(long empresaId, long ejercicioId, long facturaId, long usuarioId, CancellationToken ct);
    Task RechazarAsync(long empresaId, long ejercicioId, long facturaId, CancellationToken ct);
    /// <summary>Datos de una factura emitida para su impreso.</summary>
    Task<FacturaImpreso> ImpresoAsync(long empresaId, long ejercicioId, long facturaId, CancellationToken ct);
}

public sealed class FacturaRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : IFacturaRepositorio
{
    // ---- filas auxiliares para Dapper ----
    private sealed class EmpresaFila { public string ImpuestoIndirecto { get; set; } = ""; public string RegimenIva { get; set; } = ""; }
    private sealed class TerceroFila
    {
        public string Nombre { get; set; } = "";
        public long? CuentaClienteId { get; set; }
        public long? CuentaProveedorId { get; set; }
        public bool RecargoEquivalencia { get; set; }
        public bool EsCliente { get; set; }
        public bool EsProveedor { get; set; }
        public bool EsAcreedor { get; set; }
        public bool CriterioCaja { get; set; }
    }
    private sealed class TipoIvaFila
    {
        public long TipoIvaId { get; set; }
        public string Codigo { get; set; } = "";
        public string Impuesto { get; set; } = "";
        public long? VigenciaId { get; set; }
        public decimal? Porcentaje { get; set; }
        public decimal? Recargo { get; set; }
    }
    private sealed class VigenciaRetFila { public long VigenciaId { get; set; } public decimal Porcentaje { get; set; } }
    private sealed class PlantillaFila
    {
        public long Id { get; set; }
        public string Nombre { get; set; } = "";
        public string TipoOperacion { get; set; } = "";
        public long? DiarioId { get; set; }
        public string? ConceptoModelo { get; set; }
    }
    private sealed class PlantillaLineaFila
    {
        public short Orden { get; set; }
        public long? CuentaId { get; set; }
        public string? CuentaRol { get; set; }
        public string Lado { get; set; } = "";
        public string OrigenImporte { get; set; } = "";
        public string? Expresion { get; set; }
        public bool UsaTercero { get; set; }
        public long? CanalId { get; set; }
        public string? Concepto { get; set; }
    }
    private sealed class CuentaAutoFila
    {
        public string Rol { get; set; } = "";
        public long? TipoIvaId { get; set; }
        public long? TipoRetencionId { get; set; }
        public long CuentaId { get; set; }
    }

    private sealed record Preparada(
        LibroFactura Libro, FacturaCalculada Calculo, long EjercicioId, DateOnly FechaContable,
        long PlantillaId, string PlantillaNombre, long DiarioId, string Concepto, IReadOnlyList<ApunteCmd> Apuntes,
        bool CriterioCaja, string? Clave349);


    // =====================================================================
    // Preparación común (vista previa y alta): valida, calcula y genera los apuntes
    // =====================================================================
    private static async Task<Preparada> PrepararAsync(
        NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, CrearFacturaCmd cmd, CancellationToken ct)
    {
        var libro = cmd.Libro switch
        {
            "EMITIDAS" => LibroFactura.Emitidas,
            "RECIBIDAS" => LibroFactura.Recibidas,
            _ => throw new ReglaContableException("El libro debe ser EMITIDAS o RECIBIDAS.")
        };
        if (cmd.TipoFactura is not ("ORDINARIA" or "SIMPLIFICADA" or "RECTIFICATIVA"))
            throw new ReglaContableException($"Tipo de factura '{cmd.TipoFactura}' pendiente de implementar.");
        if (string.IsNullOrWhiteSpace(cmd.Numero))
            throw new ReglaContableException("El número de factura es obligatorio.");
        if (cmd.Lineas.Count == 0)
            throw new ReglaContableException("Añade al menos una línea de IVA/IGIC.");
        if (cmd.CanalId <= 0)
            throw new ReglaContableException("Elige el canal de la factura.");

        var devengo = cmd.FechaOperacion ?? cmd.FechaExpedicion;
        var fechaContable = cmd.FechaContable ?? cmd.FechaExpedicion;

        var empresa = await cn.QuerySingleAsync<EmpresaFila>(new CommandDefinition(
            "SELECT impuesto_indirecto, regimen_iva FROM core.empresa WHERE id = @empresaId", new { empresaId }, tx, cancellationToken: ct));

        var tercero = await cn.QuerySingleOrDefaultAsync<TerceroFila>(new CommandDefinition("""
            SELECT nombre, cuenta_cliente_id, cuenta_proveedor_id, recargo_equivalencia, es_cliente, es_proveedor, es_acreedor, criterio_caja
            FROM conta.tercero WHERE id = @terceroId AND empresa_id = @empresaId AND activo
            """, new { terceroId = cmd.TerceroId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("El tercero no existe en la empresa.");
        if (libro == LibroFactura.Emitidas && !tercero.EsCliente)
            throw new ReglaContableException("El tercero no está dado de alta como cliente.");
        if (libro == LibroFactura.Recibidas && !(tercero.EsProveedor || tercero.EsAcreedor))
            throw new ReglaContableException("El tercero no está dado de alta como proveedor.");

        // El ejercicio ELEGIDO por el usuario debe estar abierto y contener la fecha contable
        var ejercicioOk = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM core.ejercicio
                           WHERE id = @ejercicioId AND empresa_id = @empresaId AND estado = 'ABIERTO'
                             AND @fecha BETWEEN fecha_inicio AND fecha_fin)
            """, new { empresaId, ejercicioId, fecha = fechaContable }, tx, cancellationToken: ct));
        if (!ejercicioOk)
            throw new ReglaContableException(
                $"La fecha contable {fechaContable:dd/MM/yyyy} no está dentro del ejercicio seleccionado, o el ejercicio está cerrado.");

        // Integridad multiempresa de los datos opcionales
        if (cmd.ActividadId is not null && !await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM core.empresa_actividad WHERE id = @id AND empresa_id = @empresaId)",
                new { id = cmd.ActividadId, empresaId }, tx, cancellationToken: ct)))
            throw new ReglaContableException("La actividad no pertenece a la empresa.");

        // --- % de IVA/IGIC vigente en la fecha de devengo ---
        var tipoIds = cmd.Lineas.Select(l => l.TipoIvaId).Distinct().ToArray();
        var tipos = (await cn.QueryAsync<TipoIvaFila>(new CommandDefinition("""
            SELECT t.id AS tipo_iva_id, t.codigo, t.impuesto, v.id AS vigencia_id, v.porcentaje, v.recargo_equivalencia AS recargo
            FROM cat.tipo_iva t
            LEFT JOIN cat.tipo_iva_vigencia v ON v.tipo_iva_id = t.id
                 AND v.fecha_desde <= @devengo AND (v.fecha_hasta IS NULL OR v.fecha_hasta > @devengo)
            WHERE t.id = ANY(@tipoIds) AND t.activo
            """, new { devengo, tipoIds }, tx, cancellationToken: ct))).ToDictionary(t => t.TipoIvaId);

        var aplicaRecargo = libro == LibroFactura.Emitidas
            ? tercero.RecargoEquivalencia
            : empresa.RegimenIva == "RECARGO_EQUIVALENCIA";

        var entradas = new List<LineaIvaEntrada>();
        foreach (var l in cmd.Lineas)
        {
            if (!tipos.TryGetValue(l.TipoIvaId, out var t))
                throw new ReglaContableException($"El tipo de impuesto {l.TipoIvaId} no existe o está inactivo.");
            if (t.VigenciaId is null || t.Porcentaje is null)
                throw new ReglaContableException($"El tipo {t.Codigo} no tiene % vigente el {devengo:dd/MM/yyyy}.");
            if (empresa.ImpuestoIndirecto != "EXENTO" && t.Impuesto != empresa.ImpuestoIndirecto)
                throw new ReglaContableException($"La empresa usa {empresa.ImpuestoIndirecto}; el tipo {t.Codigo} es de {t.Impuesto}.");
            OperacionesIva.Validar(libro, l.TipoOperacion, empresa.ImpuestoIndirecto);

            var interior = l.TipoOperacion == "INTERIOR";
            entradas.Add(new LineaIvaEntrada(
                t.TipoIvaId, t.VigenciaId, l.TipoOperacion, l.Base,
                OperacionesIva.LlevaCuota(libro, l.TipoOperacion) ? t.Porcentaje.Value : 0m,
                interior && aplicaRecargo ? (t.Recargo ?? 0m) : 0m,
                // En recargo de equivalencia el IVA (y el recargo) soportado en las compras interiores no se deduce: es más coste
                libro == LibroFactura.Recibidas && interior && empresa.RegimenIva == "RECARGO_EQUIVALENCIA" ? "NINGUNA" : l.ReglaDeduccion,
                l.PorcentajeDeduccion, l.EsBienInversion));
        }

        // --- Prorrata (año natural + sector diferenciado de la actividad) ---
        decimal? prorrata = null;
        if (libro == LibroFactura.Recibidas && cmd.Lineas.Any(l => l.ReglaDeduccion == "PRORRATA"))
        {
            short? sector = null;
            if (cmd.ActividadId is not null)
                sector = await cn.ExecuteScalarAsync<short?>(new CommandDefinition(
                    "SELECT sector_diferenciado FROM core.empresa_actividad WHERE id = @actividadId AND empresa_id = @empresaId",
                    new { actividadId = cmd.ActividadId, empresaId }, tx, cancellationToken: ct));
            var pct = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
                SELECT COALESCE(porcentaje_definitivo, porcentaje_provisional) FROM fiscal.prorrata
                WHERE empresa_id = @empresaId AND anio = @anio AND sector_diferenciado IS NOT DISTINCT FROM @sector::smallint
                """, new { empresaId, anio = (short)fechaContable.Year, sector }, tx, cancellationToken: ct));
            prorrata = pct ?? throw new ReglaContableException($"No hay prorrata definida para {fechaContable.Year}.",
                "Créala en fiscal.prorrata (provisional o definitiva) antes de aplicar la regla PRORRATA.");
        }

        // --- Retención IRPF ---
        RetencionEntrada? retencion = null;
        if (cmd.TipoRetencionId is long tipoRetId)
        {
            if (libro == LibroFactura.Emitidas)
            {
                var profesional = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT EXISTS (
                        SELECT 1 FROM core.empresa_actividad a JOIN cat.epigrafe_iae e ON e.id = a.epigrafe_id
                        WHERE a.empresa_id = @empresaId AND e.es_profesional
                          AND a.fecha_alta <= @devengo AND (a.fecha_baja IS NULL OR a.fecha_baja >= @devengo))
                    """, new { empresaId, devengo }, tx, cancellationToken: ct));
                if (!profesional)
                    throw new ReglaContableException("La retención IRPF en facturas emitidas solo se aplica si la empresa tiene una actividad con epígrafe profesional.");
            }
            var v = await cn.QuerySingleOrDefaultAsync<VigenciaRetFila>(new CommandDefinition("""
                SELECT v.id AS vigencia_id, v.porcentaje
                FROM cat.tipo_retencion_irpf_vigencia v JOIN cat.tipo_retencion_irpf t ON t.id = v.tipo_retencion_id
                WHERE t.id = @tipoRetId AND t.activo AND v.fecha_desde <= @devengo AND (v.fecha_hasta IS NULL OR v.fecha_hasta > @devengo)
                """, new { tipoRetId, devengo }, tx, cancellationToken: ct))
                ?? throw new ReglaContableException("El tipo de retención no tiene % vigente en esa fecha.");
            retencion = new RetencionEntrada(tipoRetId, v.VigenciaId, v.Porcentaje);
        }

        var calculo = FacturaCalculadora.Calcular(libro, entradas, prorrata, retencion);

        // --- 349: clave de las operaciones intracomunitarias ---
        var intracom = cmd.Lineas.Any(l => l.TipoOperacion is "INTRACOM_ENTREGA" or "INTRACOM_ADQUISICION");
        var clave349 = string.IsNullOrWhiteSpace(cmd.Clave349) ? null : cmd.Clave349.Trim().ToUpperInvariant();
        if (clave349 is not null && !intracom)
            throw new ReglaContableException("La clave del 349 solo se indica en facturas con operaciones intracomunitarias.");
        if (intracom)
        {
            var validas = libro == LibroFactura.Emitidas ? Calculador349.ClavesEmitidas : Calculador349.ClavesRecibidas;
            clave349 ??= validas[0];
            if (!validas.Contains(clave349))
                throw new ReglaContableException($"Clave del 349 no válida: {string.Join(", ", validas)}.",
                    libro == LibroFactura.Emitidas ? "E entregas de bienes, S servicios prestados, T operaciones triangulares."
                                                   : "A adquisiciones de bienes, I servicios recibidos.");
        }

        // --- Factura rectificada ---
        if (cmd.FacturaRectificadaId is { } rectId)
        {
            if (cmd.TipoFactura != "RECTIFICATIVA") throw new ReglaContableException("Solo una factura rectificativa indica la factura que rectifica.");
            var okRect = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM fiscal.factura WHERE id = @rectId AND empresa_id = @empresaId AND libro = @libro
                               AND tercero_id = @terceroId AND estado = 'CONTABILIZADA')
                """, new { rectId, empresaId, libro = cmd.Libro, terceroId = cmd.TerceroId }, tx, cancellationToken: ct));
            if (!okRect) throw new ReglaContableException("La factura rectificada no existe o no es del mismo libro y tercero.");
        }

        // --- Criterio de caja: la empresa está en el régimen o (en compras) el proveedor lo está ---
        var criterioCaja = empresa.RegimenIva == "CRITERIO_CAJA" || (libro == LibroFactura.Recibidas && tercero.CriterioCaja);
        if (criterioCaja && calculo.Lineas.Any(l => l.TipoOperacion == "INTERIOR" && (l.Cuota != 0 || l.CuotaRecargo != 0)))
            await cn.ExecuteAsync(new CommandDefinition("SELECT fiscal.asegurar_cuentas_caja(@empresaId)", new { empresaId }, tx, cancellationToken: ct));

        // --- Plantilla (explícita o la de por defecto según el libro) ---
        var tipoOp = libro == LibroFactura.Emitidas ? "VENTA" : "COMPRA";
        var plantilla = await cn.QuerySingleOrDefaultAsync<PlantillaFila>(new CommandDefinition("""
            SELECT id, nombre, tipo_operacion, diario_id, concepto_modelo FROM conta.plantilla_asiento
            WHERE (empresa_id = @empresaId OR empresa_id IS NULL) AND activa
              AND ((@plantillaId::bigint IS NOT NULL AND id = @plantillaId)
                OR (@plantillaId::bigint IS NULL AND tipo_operacion = @tipoOp))
            ORDER BY (empresa_id IS NULL), id LIMIT 1
            """, new { empresaId, plantillaId = cmd.PlantillaId, tipoOp }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("No hay plantilla de asiento para esta factura.",
                "Carga database/03_facturacion_demo.sql o crea una plantilla.");
        var tipoValido = libro == LibroFactura.Emitidas
            ? plantilla.TipoOperacion == "VENTA"
            : plantilla.TipoOperacion is "COMPRA" or "GASTO";
        if (!tipoValido)
            throw new ReglaContableException($"La plantilla '{plantilla.Nombre}' no es válida para facturas {cmd.Libro.ToLowerInvariant()}.");

        var lineasPlantilla = (await cn.QueryAsync<PlantillaLineaFila>(new CommandDefinition("""
            SELECT orden, cuenta_id, cuenta_rol, lado, origen_importe, expresion, usa_tercero, canal_id, concepto
            FROM conta.plantilla_asiento_linea WHERE plantilla_id = @id ORDER BY orden
            """, new { plantilla.Id }, tx, cancellationToken: ct)))
            .Select(f => new LineaPlantilla(f.Orden, f.CuentaId, f.CuentaRol, f.Lado, f.OrigenImporte, f.Expresion, f.UsaTercero, f.CanalId, f.Concepto))
            .ToList();

        var reglas = (await cn.QueryAsync<CuentaAutoFila>(new CommandDefinition(
            "SELECT rol, tipo_iva_id, tipo_retencion_id, cuenta_id FROM conta.cuenta_automatica WHERE empresa_id = @empresaId",
            new { empresaId }, tx, cancellationToken: ct)))
            .Select(r => new CuentaAutomatica(r.Rol, r.TipoIvaId, r.TipoRetencionId, r.CuentaId)).ToList();

        var resolutor = new ResolutorCuentas(reglas, tercero.CuentaClienteId, tercero.CuentaProveedorId);
        var numeroCompleto = string.IsNullOrWhiteSpace(cmd.Serie) ? cmd.Numero : $"{cmd.Serie}{cmd.Numero}";

        var apuntes = GeneradorAsientoFactura.Generar(libro, calculo, lineasPlantilla, resolutor,
            cmd.TerceroId, cmd.ActividadId, cmd.CanalId, tercero.Nombre, numeroCompleto, cmd.FechaExpedicion, plantilla.ConceptoModelo, criterioCaja,
            empresa.RegimenIva == "RECARGO_EQUIVALENCIA");
        if (apuntes.Count < 2)
            throw new ReglaContableException("La plantilla no generó apuntes suficientes para esta factura.");

        var diarioId = cmd.DiarioId ?? plantilla.DiarioId
            ?? await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = @tipo ORDER BY id LIMIT 1",
                new { empresaId, tipo = libro == LibroFactura.Emitidas ? "VENTAS" : "COMPRAS" }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("No hay diario de compras/ventas configurado.");

        var concepto = !string.IsNullOrWhiteSpace(cmd.Descripcion) ? cmd.Descripcion.Trim() : apuntes[0].Concepto ?? $"Factura {numeroCompleto}";

        return new Preparada(libro, calculo, ejercicioId, fechaContable, plantilla.Id, plantilla.Nombre, diarioId, concepto, apuntes,
            criterioCaja, clave349);
    }

    // =====================================================================
    public async Task<FacturaPrevisualizacion> PrevisualizarAsync(long empresaId, long ejercicioId, CrearFacturaCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var p = await PrepararAsync(cn, null, empresaId, ejercicioId, cmd, ct);

        var ids = p.Apuntes.Select(a => a.CuentaId).Distinct().ToArray();
        var cuentas = (await cn.QueryAsync<CuentaDto>(new CommandDefinition(
            "SELECT id, codigo, nombre FROM conta.cuenta WHERE empresa_id = @empresaId AND id = ANY(@ids)", new { empresaId, ids }, cancellationToken: ct))).ToDictionary(c => c.Id);

        var asiento = p.Apuntes.Select(a => new ApuntePrevioDto(
            a.CuentaId, cuentas[a.CuentaId].Codigo, cuentas[a.CuentaId].Nombre, a.CanalId, a.Debe, a.Haber, a.Concepto)).ToList();
        return new FacturaPrevisualizacion(p.Calculo, asiento, p.PlantillaId, p.PlantillaNombre, p.DiarioId, p.Concepto);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<FacturaCreada> CrearAsync(long empresaId, long ejercicioId, long usuarioId, CrearFacturaCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var p = await PrepararAsync(cn, tx, empresaId, ejercicioId, cmd, ct);
        var t = p.Calculo.Totales;

        long facturaId;
        try
        {
            facturaId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO fiscal.factura
                  (empresa_id, ejercicio_id, libro, tipo_factura, serie, numero, tercero_id, actividad_id, canal_id, plantilla_id,
                   fecha_expedicion, fecha_operacion, fecha_contable, descripcion,
                   base_total, cuota_total, recargo_total, retencion_total, total, clave_operacion_347, estado, creado_por, comando,
                   criterio_caja, clave_operacion_349, factura_rectificada_id)
                VALUES
                  (@empresaId, @ejercicioId, @libro, @tipoFactura, @serie, @numero, @terceroId, @actividadId, @canalId, @plantillaId,
                   @fechaExpedicion, @fechaOperacion, @fechaContable, @descripcion,
                   @base, @cuota, @recargo, @retencion, @total, @clave347, 'BORRADOR', @usuarioId, @comando::jsonb,
                   @criterioCaja, @clave349, @rectificada)
                RETURNING id
                """, new
            {
                empresaId, ejercicioId, libro = cmd.Libro, tipoFactura = cmd.TipoFactura,
                serie = cmd.Serie, numero = cmd.Numero.Trim(), terceroId = cmd.TerceroId, actividadId = cmd.ActividadId,
                canalId = cmd.CanalId, plantillaId = p.PlantillaId, fechaExpedicion = cmd.FechaExpedicion,
                fechaOperacion = cmd.FechaOperacion, fechaContable = p.FechaContable, descripcion = cmd.Descripcion,
                @base = t.Base, cuota = t.Cuota, recargo = t.Recargo, retencion = t.Retencion, total = t.Total,
                clave347 = p.Libro == LibroFactura.Emitidas ? "B" : "A",
                usuarioId, comando = JsonSerializer.Serialize(cmd, JsonOpts),
                criterioCaja = p.CriterioCaja, clave349 = p.Clave349, rectificada = cmd.FacturaRectificadaId
            }, tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException("Ya existe una factura con ese número para ese tercero.");
        }

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.factura_iva
              (factura_id, tipo_iva_id, tipo_iva_vigencia_id, tipo_operacion, base, porcentaje, cuota,
               porcentaje_recargo, cuota_recargo, regla_deduccion, porcentaje_deduccion, cuota_deducible, es_bien_inversion)
            VALUES
              (@facturaId, @tipoIvaId, @vigenciaId, @tipoOperacion, @base, @porcentaje, @cuota,
               @porcentajeRecargo, @cuotaRecargo, @reglaDeduccion, @porcentajeDeduccion, @cuotaDeducible, @esBienInversion)
            """, p.Calculo.Lineas.Select(l => new
        {
            facturaId, tipoIvaId = l.TipoIvaId, vigenciaId = l.VigenciaId, tipoOperacion = l.TipoOperacion, @base = l.Base,
            porcentaje = l.Porcentaje, cuota = l.Cuota, porcentajeRecargo = l.PorcentajeRecargo, cuotaRecargo = l.CuotaRecargo,
            reglaDeduccion = l.ReglaDeduccion, porcentajeDeduccion = l.PorcentajeDeduccion, cuotaDeducible = l.CuotaDeducible,
            esBienInversion = l.EsBienInversion
        }), tx, cancellationToken: ct));

        if (p.Calculo.Retencion is { } r)
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO fiscal.factura_retencion (factura_id, tipo_retencion_id, vigencia_id, base, porcentaje, importe)
                VALUES (@facturaId, @tipoRetencionId, @vigenciaId, @base, @porcentaje, @importe)
                """, new { facturaId, tipoRetencionId = r.TipoRetencionId, vigenciaId = r.VigenciaId, @base = r.Base, porcentaje = r.Porcentaje, importe = r.Importe },
                tx, cancellationToken: ct));

        long? asientoId = null; int? asientoNumero = null;
        if (!cmd.Borrador)
        {
            var numeroCompleto = string.IsNullOrWhiteSpace(cmd.Serie) ? cmd.Numero : $"{cmd.Serie}{cmd.Numero}";
            var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, new CrearAsientoCmd
            {
                Fecha = p.FechaContable, DiarioId = p.DiarioId, Concepto = p.Concepto, Apuntes = p.Apuntes.ToList()
            }, "FACTURA", p.PlantillaId, numeroCompleto, ct);

            await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE fiscal.factura SET asiento_id = @asientoId, estado = 'CONTABILIZADA' WHERE id = @facturaId",
                new { asientoId = a.Id, facturaId }, tx, cancellationToken: ct));
            asientoId = a.Id; asientoNumero = a.Numero;
            await VencimientosGenerador.GenerarAsync(cn, tx, empresaId, facturaId, cmd.FormaPagoId, ct);
        }

        await tx.CommitAsync(ct);
        return new FacturaCreada(facturaId, asientoId, asientoNumero, asientoId is null ? "BORRADOR" : "CONTABILIZADA", p.Calculo.Totales);
    }

    // ---------------------------------------------------------------- revisión de borradores
    private sealed class FacturaBorradorFila { public string Estado { get; set; } = ""; public string? Comando { get; set; } public decimal Total { get; set; } public long CreadoPor { get; set; } }

    public async Task<FacturaCreada> AprobarAsync(long empresaId, long ejercicioId, long facturaId, long usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var f = await cn.QuerySingleOrDefaultAsync<FacturaBorradorFila>(new CommandDefinition("""
            SELECT estado, comando::text AS comando, total, COALESCE(creado_por, 0) AS creado_por
            FROM fiscal.factura WHERE id = @facturaId AND empresa_id = @empresaId AND ejercicio_id = @ejercicioId FOR UPDATE
            """, new { facturaId, empresaId, ejercicioId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La factura no existe en este ejercicio.");
        if (f.Estado != "BORRADOR") throw new ReglaContableException("Solo se pueden aprobar facturas en borrador.");
        if (string.IsNullOrWhiteSpace(f.Comando))
            throw new ReglaContableException("La factura no conserva sus datos originales; vuelve a introducirla.");

        var cmd = JsonSerializer.Deserialize<CrearFacturaCmd>(f.Comando, JsonOpts)
                  ?? throw new ReglaContableException("No se pudieron leer los datos de la factura.");
        cmd.Borrador = false;

        // Se recalcula con los datos vigentes: si el total cambió (p. ej. cambió un tipo de impuesto) se pide revisar
        var p = await PrepararAsync(cn, tx, empresaId, ejercicioId, cmd, ct);
        if (p.Calculo.Totales.Total != f.Total)
            throw new ReglaContableException("El total recalculado no coincide con el del borrador.",
                $"borrador {f.Total:N2} / ahora {p.Calculo.Totales.Total:N2}. Rechaza el borrador y créala de nuevo.");

        var numeroCompleto = string.IsNullOrWhiteSpace(cmd.Serie) ? cmd.Numero : $"{cmd.Serie}{cmd.Numero}";
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, f.CreadoPor == 0 ? usuarioId : f.CreadoPor,
            new CrearAsientoCmd { Fecha = p.FechaContable, DiarioId = p.DiarioId, Concepto = p.Concepto, Apuntes = p.Apuntes.ToList() },
            "FACTURA", p.PlantillaId, numeroCompleto, ct);

        await cn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE fiscal.factura SET asiento_id = @asientoId, estado = 'CONTABILIZADA', plantilla_id = @plantillaId,
                   criterio_caja = @criterioCaja, clave_operacion_349 = @clave349
            WHERE id = @facturaId
            """,
            new { asientoId = a.Id, plantillaId = p.PlantillaId, facturaId, criterioCaja = p.CriterioCaja, clave349 = p.Clave349 }, tx, cancellationToken: ct));
        await VencimientosGenerador.GenerarAsync(cn, tx, empresaId, facturaId, cmd.FormaPagoId, ct);

        await tx.CommitAsync(ct);
        return new FacturaCreada(facturaId, a.Id, a.Numero, "CONTABILIZADA", p.Calculo.Totales);
    }

    public async Task RechazarAsync(long empresaId, long ejercicioId, long facturaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE fiscal.factura SET estado = 'ANULADA'
            WHERE id = @facturaId AND empresa_id = @empresaId AND ejercicio_id = @ejercicioId AND estado = 'BORRADOR'
            """, new { facturaId, empresaId, ejercicioId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("No hay una factura en borrador con ese identificador en este ejercicio.");
    }

    public async Task<IReadOnlyList<PendienteDto>> ListarPendientesAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT 'FACTURA' AS tipo, f.id, (CASE f.libro WHEN 'EMITIDAS' THEN 'Emitida ' ELSE 'Recibida ' END) || COALESCE(f.serie, '') || f.numero AS referencia,
                   f.fecha_expedicion AS fecha, t.nombre AS descripcion, f.total AS importe, COALESCE(u.nombre, '') AS creado_por
            FROM fiscal.factura f
            JOIN conta.tercero t ON t.id = f.tercero_id
            LEFT JOIN core.usuario u ON u.id = f.creado_por
            WHERE f.empresa_id = @empresaId AND f.ejercicio_id = @ejercicioId AND f.estado = 'BORRADOR'
            ORDER BY f.fecha_expedicion, f.id
            """;
        return (await cn.QueryAsync<PendienteDto>(new CommandDefinition(sql, new { empresaId, ejercicioId }, cancellationToken: ct))).AsList();
    }

    public async Task<FacturaImpreso> ImpresoAsync(long empresaId, long ejercicioId, long facturaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var f = await cn.QuerySingleOrDefaultAsync<FacturaImpresoFila>(new CommandDefinition("""
            SELECT f.libro, f.tipo_factura, f.serie, f.numero, f.fecha_expedicion, f.fecha_operacion, f.descripcion, f.estado,
                   f.criterio_caja, f.base_total, f.cuota_total, f.recargo_total, f.retencion_total, f.total,
                   COALESCE(r.serie, '') || r.numero AS rectificada, r.fecha_expedicion AS fecha_rectificada,
                   f.empresa_id, f.tercero_id
            FROM fiscal.factura f LEFT JOIN fiscal.factura r ON r.id = f.factura_rectificada_id
            WHERE f.id = @facturaId AND f.empresa_id = @empresaId AND f.ejercicio_id = @ejercicioId
            """, new { facturaId, empresaId, ejercicioId }, cancellationToken: ct))
            ?? throw new ReglaContableException("La factura no existe en este ejercicio.");
        if (f.Libro != "EMITIDAS") throw new ReglaContableException("Solo se imprimen las facturas emitidas.");
        if (f.Estado == "ANULADA") throw new ReglaContableException("La factura está anulada.");

        const string sqlParte = """
            SELECT {0} AS nombre, n.numero AS nif, COALESCE({1}, n.direccion) AS direccion, n.codigo_postal, n.poblacion,
                   pr.nombre AS provincia, p.iso2 AS pais
            FROM core.nif n JOIN cat.pais p ON p.id = n.pais_id LEFT JOIN cat.provincia pr ON pr.id = n.provincia_id
            """;
        var emisor = await cn.QuerySingleAsync<ParteImpreso>(new CommandDefinition(
            string.Format(sqlParte, "n.razon_social", "NULL") + " JOIN core.empresa e ON e.nif_id = n.id WHERE e.id = @empresaId",
            new { empresaId }, cancellationToken: ct));
        var destinatario = await cn.QuerySingleAsync<ParteImpreso>(new CommandDefinition(
            string.Format(sqlParte, "t.nombre", "t.direccion") + " JOIN conta.tercero t ON t.nif_id = n.id WHERE t.id = @terceroId",
            new { terceroId = f.TerceroId }, cancellationToken: ct));

        var lineas = (await cn.QueryAsync<LineaIvaImpreso>(new CommandDefinition("""
            SELECT ti.impuesto, fi.tipo_operacion, fi.base, fi.porcentaje, fi.cuota, fi.porcentaje_recargo, fi.cuota_recargo
            FROM fiscal.factura_iva fi JOIN cat.tipo_iva ti ON ti.id = fi.tipo_iva_id
            WHERE fi.factura_id = @facturaId ORDER BY fi.porcentaje DESC, fi.id
            """, new { facturaId }, cancellationToken: ct))).AsList();
        var retenciones = (await cn.QueryAsync<RetencionImpreso>(new CommandDefinition(
            "SELECT base, porcentaje, importe FROM fiscal.factura_retencion WHERE factura_id = @facturaId ORDER BY id",
            new { facturaId }, cancellationToken: ct))).AsList();
        var venc = (await cn.QueryAsync<VencimientoImpresoFila>(new CommandDefinition("""
            SELECT v.fecha_vencimiento AS fecha, v.importe, fp.nombre AS forma_pago, cb.iban
            FROM tes.vencimiento v LEFT JOIN core.forma_pago fp ON fp.id = v.forma_pago_id
            LEFT JOIN tes.cuenta_bancaria cb ON cb.id = v.cuenta_bancaria_id
            WHERE v.factura_id = @facturaId AND v.estado <> 'ANULADO' ORDER BY v.fecha_vencimiento, v.id
            """, new { facturaId }, cancellationToken: ct))).AsList();

        return new FacturaImpreso
        {
            TipoFactura = f.TipoFactura, Serie = f.Serie, Numero = f.Numero, FechaExpedicion = f.FechaExpedicion, FechaOperacion = f.FechaOperacion,
            Descripcion = f.Descripcion, Estado = f.Estado, CriterioCaja = f.CriterioCaja, Rectificada = f.Rectificada, FechaRectificada = f.FechaRectificada,
            BaseTotal = f.BaseTotal, CuotaTotal = f.CuotaTotal, RecargoTotal = f.RecargoTotal, RetencionTotal = f.RetencionTotal, Total = f.Total,
            Emisor = emisor, Destinatario = destinatario, Lineas = lineas, Retenciones = retenciones,
            Vencimientos = venc.Select(v => new VencimientoImpreso { Fecha = v.Fecha, Importe = v.Importe }).ToList(),
            FormaPago = venc.Select(v => v.FormaPago).FirstOrDefault(x => x is not null),
            Iban = venc.Select(v => v.Iban).FirstOrDefault(x => x is not null),
        };
    }

    private sealed class FacturaImpresoFila
    {
        public string Libro { get; set; } = "";
        public string TipoFactura { get; set; } = "";
        public string? Serie { get; set; }
        public string Numero { get; set; } = "";
        public DateOnly FechaExpedicion { get; set; }
        public DateOnly? FechaOperacion { get; set; }
        public string? Descripcion { get; set; }
        public string Estado { get; set; } = "";
        public bool CriterioCaja { get; set; }
        public decimal BaseTotal { get; set; }
        public decimal CuotaTotal { get; set; }
        public decimal RecargoTotal { get; set; }
        public decimal RetencionTotal { get; set; }
        public decimal Total { get; set; }
        public string? Rectificada { get; set; }
        public DateOnly? FechaRectificada { get; set; }
        public long EmpresaId { get; set; }
        public long TerceroId { get; set; }
    }

    private sealed class VencimientoImpresoFila
    {
        public DateOnly Fecha { get; set; }
        public decimal Importe { get; set; }
        public string? FormaPago { get; set; }
        public string? Iban { get; set; }
    }

    public async Task<IReadOnlyList<FacturaResumenDto>> ListarAsync(long empresaId, long ejercicioId, string? libro, int limite, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = """
            SELECT f.id, f.libro, f.serie, f.numero, f.fecha_expedicion AS fecha, t.nombre AS tercero,
                   f.base_total AS base_imponible, f.cuota_total AS cuota, f.recargo_total AS recargo,
                   f.retencion_total AS retencion, f.total, f.estado, a.numero AS asiento_numero
            FROM fiscal.factura f
            JOIN conta.tercero t ON t.id = f.tercero_id
            LEFT JOIN conta.asiento a ON a.id = f.asiento_id
            WHERE f.empresa_id = @empresaId AND f.ejercicio_id = @ejercicioId AND (@libro::text IS NULL OR f.libro = @libro)
            ORDER BY f.fecha_expedicion DESC, f.id DESC LIMIT @limite
            """;
        return (await cn.QueryAsync<FacturaResumenDto>(new CommandDefinition(sql, new { empresaId, ejercicioId, libro, limite }, cancellationToken: ct))).AsList();
    }
}
