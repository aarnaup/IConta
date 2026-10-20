using System.Text.Json;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IFiscalRepositorio
{
    Task<IReadOnlyList<ModeloSoportado>> ModelosDisponiblesAsync(long empresaId, CancellationToken ct);
    Task<DeclaracionVista> CalcularAsync(long empresaId, CalcularModeloCmd cmd, CancellationToken ct);
    Task<DeclaracionVista> GuardarAsync(long empresaId, long usuarioId, CalcularModeloCmd cmd, CancellationToken ct);
    Task<IReadOnlyList<DeclaracionResumenDto>> ListarAsync(long empresaId, int? anio, CancellationToken ct);
    Task<DeclaracionVista> ObtenerAsync(long empresaId, long declaracionId, CancellationToken ct);
    Task<DeclaracionVista> PresentarAsync(long empresaId, long declaracionId, PresentarDeclaracionCmd cmd, CancellationToken ct);
    Task AnularAsync(long empresaId, long declaracionId, CancellationToken ct);
}

public sealed class FiscalRepositorio(IConexionFactory conexiones) : IFiscalRepositorio
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- filas auxiliares para Dapper ----
    private sealed class EmpresaFila
    {
        public string ImpuestoIndirecto { get; set; } = "";
        public string RegimenIva { get; set; } = "";
        public string? RegimenIrpf { get; set; }
    }
    private sealed class GrupoFila
    {
        public string Libro { get; set; } = "";
        public bool Rectificativa { get; set; }
        public string TipoOperacion { get; set; } = "";
        public decimal Porcentaje { get; set; }
        public decimal PorcentajeRecargo { get; set; }
        public bool EsBienInversion { get; set; }
        public decimal Base { get; set; }
        public decimal Cuota { get; set; }
        public decimal CuotaRecargo { get; set; }
        public decimal CuotaDeducible { get; set; }
    }
    private sealed class ProrrataFila { public decimal Regularizacion { get; set; } public int SinDefinitiva { get; set; } }
    private sealed class OperacionFila
    {
        public long TerceroId { get; set; }
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Clave { get; set; } = "";
        public int Trimestre { get; set; }
        public decimal Importe { get; set; }
    }
    private sealed class DeclaracionFila
    {
        public long Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Administracion { get; set; } = "";
        public string Nombre { get; set; } = "";
        public int Anio { get; set; }
        public string Periodo { get; set; } = "";
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
        public short NumeroPresentacion { get; set; }
        public string Estado { get; set; } = "";
        public decimal? Resultado { get; set; }
        public decimal? AIngresar { get; set; }
        public decimal? ADevolver { get; set; }
        public decimal? ACompensar { get; set; }
        public string? Datos { get; set; }
        public string? Opciones { get; set; }
    }
    private sealed class TotalesFila { public decimal Ingresos { get; set; } public decimal Gastos { get; set; } }
    private sealed class ExistenteFila { public long Id { get; set; } public string Estado { get; set; } = ""; }
    private sealed class CasillaFila { public string Casilla { get; set; } = ""; public string? Texto { get; set; } public decimal? Importe { get; set; } }

    // =====================================================================
    // Modelos aplicables a cada empresa
    // =====================================================================
    public async Task<IReadOnlyList<ModeloSoportado>> ModelosDisponiblesAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await EmpresaAsync(cn, null, empresaId, ct);
        var directa = e.RegimenIrpf is "DIRECTA_NORMAL" or "DIRECTA_SIMPLIFICADA";
        return ModelosFiscales.Soportados.Where(m => m.Impuesto switch
        {
            "IVA" => e.ImpuestoIndirecto == "IVA",
            "IGIC" => e.ImpuestoIndirecto == "IGIC",
            "IRPF" => directa,
            "TERCEROS" => m.Codigo == "415" ? e.ImpuestoIndirecto == "IGIC" : e.ImpuestoIndirecto != "IGIC",
            _ => false
        }).ToList();
    }

    private static async Task<EmpresaFila> EmpresaAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<EmpresaFila>(new CommandDefinition(
               "SELECT impuesto_indirecto, regimen_iva, regimen_irpf FROM core.empresa WHERE id = @empresaId", new { empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("La empresa no existe.");

    // =====================================================================
    // Cálculo
    // =====================================================================
    public async Task<DeclaracionVista> CalcularAsync(long empresaId, CalcularModeloCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var d = await CalcularInternoAsync(cn, null, empresaId, cmd, ct);
        return VistaDe(null, ModelosFiscales.Obtener(cmd.Modelo), "SIN_GUARDAR", d);
    }

    private static async Task<DeclaracionCalculada> CalcularInternoAsync(
        NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CalcularModeloCmd cmd, CancellationToken ct)
    {
        var modelo = ModelosFiscales.Obtener(cmd.Modelo);
        var p = ModelosFiscales.ResolverPeriodo(modelo, cmd.Anio, cmd.Periodo);
        var empresa = await EmpresaAsync(cn, tx, empresaId, ct);
        var (plazoDesde, plazoHasta) = PlazoPresentacion.Calcular(modelo.Codigo, p);

        var d = new DeclaracionCalculada
        {
            Modelo = modelo.Codigo, Anio = p.Anio, Periodo = p.Codigo, FechaInicio = p.Inicio, FechaFin = p.Fin,
            PlazoDesde = plazoDesde, PlazoHasta = plazoHasta
        };

        switch (modelo.Impuesto)
        {
            case "IVA" or "IGIC":
                await CalcularImpuestoIndirectoAsync(cn, tx, empresaId, empresa, modelo, p, cmd, d, ct);
                break;
            case "IRPF":
                await Calcular130Async(cn, tx, empresaId, empresa, modelo, p, d, ct);
                break;
            case "TERCEROS":
                await Calcular347Async(cn, tx, empresaId, p, d, ct);
                break;
        }
        return d;
    }

    private static async Task<long> ModeloIdAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, ModeloSoportado m, CancellationToken ct)
        => await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
               "SELECT id FROM fiscal.modelo_fiscal WHERE administracion = @a AND codigo = @c", new { a = m.Administracion, c = m.Codigo }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException($"El modelo {m.Codigo} no está en el catálogo (fiscal.modelo_fiscal).");

    // ---------------------------------------------------------------- 303 / 420 / 390 / 425
    private static async Task CalcularImpuestoIndirectoAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa,
        ModeloSoportado modelo, PeriodoFiscal p, CalcularModeloCmd cmd, DeclaracionCalculada d, CancellationToken ct)
    {
        if (empresa.ImpuestoIndirecto != modelo.Impuesto)
            throw new ReglaContableException($"El modelo {modelo.Codigo} es de {modelo.Impuesto} y la empresa tributa por {empresa.ImpuestoIndirecto}.");
        if (empresa.RegimenIva != "GENERAL")
            throw new ReglaContableException($"El régimen {empresa.RegimenIva} todavía no está soportado en {modelo.Codigo}.",
                "Solo el régimen general. Criterio de caja, simplificado, agricultura y recargo de equivalencia como régimen de la empresa están pendientes.");

        var igic = modelo.Impuesto == "IGIC";
        var grupos = (await cn.QueryAsync<GrupoFila>(new CommandDefinition("""
            SELECT f.libro, (f.tipo_factura = 'RECTIFICATIVA') AS rectificativa, fi.tipo_operacion, fi.porcentaje, fi.porcentaje_recargo,
                   fi.es_bien_inversion, SUM(fi.base) AS base, SUM(fi.cuota) AS cuota, SUM(fi.cuota_recargo) AS cuota_recargo,
                   SUM(fi.cuota_deducible) AS cuota_deducible
            FROM fiscal.factura f
            JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
            JOIN cat.tipo_iva t ON t.id = fi.tipo_iva_id
            WHERE f.empresa_id = @empresaId AND f.estado = 'CONTABILIZADA'
              AND f.fecha_contable BETWEEN @inicio AND @fin AND t.impuesto = @impuesto
            GROUP BY 1, 2, 3, 4, 5, 6
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin, impuesto = modelo.Impuesto }, tx, cancellationToken: ct)))
            .Select(g => new GrupoIva(g.Libro, g.Rectificativa, g.TipoOperacion, g.Porcentaje, g.PorcentajeRecargo, g.EsBienInversion,
                                      g.Base, g.Cuota, g.CuotaRecargo, g.CuotaDeducible)).ToList();

        var anual = p.EsAnual;

        // Regularización por % definitivo de prorrata: solo en el último periodo; se calcula sobre todo el año natural
        decimal regularizacion = 0;
        if (p.EsUltimoDelAnio)
        {
            var pr = await cn.QuerySingleAsync<ProrrataFila>(new CommandDefinition("""
                SELECT COALESCE(SUM(CASE WHEN pr.id IS NOT NULL
                                         THEN ROUND(fi.cuota * (pr.porcentaje_definitivo - fi.porcentaje_deduccion) / 100.0, 2) END), 0) AS regularizacion,
                       COUNT(*) FILTER (WHERE pr.id IS NULL) AS sin_definitiva
                FROM fiscal.factura f
                JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
                JOIN cat.tipo_iva t ON t.id = fi.tipo_iva_id
                LEFT JOIN core.empresa_actividad a ON a.id = f.actividad_id
                LEFT JOIN fiscal.prorrata pr ON pr.empresa_id = f.empresa_id AND pr.anio = @anio
                     AND pr.sector_diferenciado IS NOT DISTINCT FROM a.sector_diferenciado AND pr.porcentaje_definitivo IS NOT NULL
                WHERE f.empresa_id = @empresaId AND f.libro = 'RECIBIDAS' AND f.estado = 'CONTABILIZADA'
                  AND fi.regla_deduccion = 'PRORRATA' AND fi.tipo_operacion IN ('INTERIOR','IMPORTACION','INTRACOM_ADQUISICION','INVERSION_SUJETO_PASIVO')
                  AND t.impuesto = @impuesto
                  AND f.fecha_contable BETWEEN @inicioAnio AND @finAnio
                """, new { empresaId, anio = (short)p.Anio, impuesto = modelo.Impuesto, inicioAnio = p.InicioAnio, finAnio = new DateOnly(p.Anio, 12, 31) },
                tx, cancellationToken: ct));
            regularizacion = pr.Regularizacion;
            if (pr.SinDefinitiva > 0)
                d.Avisos.Add($"Hay {pr.SinDefinitiva} línea(s) con prorrata sin porcentaje definitivo de {p.Anio}: no se regularizan. Fija el definitivo (Modelos fiscales → Prorrata) y recalcula.");
        }

        // Bienes de inversión: regularización anual (último periodo) y por venta (periodo de la venta), arts. 107-110 LIVA
        var (regBienes, avisosBienes) = await BienesInversionRepositorio.ParaDeclaracionAsync(cn, tx, empresaId, p, ct);
        d.Avisos.AddRange(avisosBienes);

        // Compensaciones de periodos anteriores (solo autoliquidaciones, no el resumen anual)
        decimal pendiente = 0;
        if (!anual)
        {
            var claveCompensada = igic ? "COMP" : "68";
            var mid = await ModeloIdAsync(cn, tx, modelo, ct);
            pendiente = Math.Max(0m, await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                SELECT COALESCE(SUM(d.a_compensar), 0) - COALESCE(SUM(c.importe), 0)
                FROM fiscal.declaracion d
                LEFT JOIN fiscal.declaracion_casilla c ON c.declaracion_id = d.id AND c.casilla = @clave
                WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid AND d.estado = 'PRESENTADA'
                  AND d.fecha_fin < @inicio AND d.fecha_fin >= (@inicio::date - interval '4 years')::date
                  AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                        WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
                """, new { empresaId, mid, inicio = p.Inicio, clave = claveCompensada }, tx, cancellationToken: ct)));
        }

        CalculadorImpuestoIndirecto.Calcular(igic,
            new EntradaImpuestoIndirecto(grupos, regularizacion, pendiente, cmd.SolicitaDevolucion, p.EsUltimoDelAnio, regBienes), d);

        if (anual)
        {
            // El resumen anual es informativo: no genera ingreso, devolución ni compensación
            d.AIngresar = d.ADevolver = d.ACompensar = 0;
            d.Casillas.RemoveAll(c => c.Clave is "110" or "68" or "87" or "69" or "71" or "COMP" or "PEND_ANT" or "PEND_SIG" or "LIQ");
            var baseModelo = igic ? ModelosFiscales.Obtener("420") : ModelosFiscales.Obtener("303");
            var midBase = await ModeloIdAsync(cn, tx, baseModelo, ct);
            var claveAntes = igic ? "RES" : "64";
            var presentadas = (await cn.QueryAsync<decimal>(new CommandDefinition("""
                SELECT c.importe FROM fiscal.declaracion d JOIN fiscal.declaracion_casilla c ON c.declaracion_id = d.id AND c.casilla = @clave
                WHERE d.empresa_id = @empresaId AND d.modelo_id = @midBase AND d.anio = @anio AND d.estado = 'PRESENTADA'
                  AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                        WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
                """, new { empresaId, midBase, anio = (short)p.Anio, clave = claveAntes }, tx, cancellationToken: ct))).AsList();
            var anualAntes = d[claveAntes];
            d.Avisos.Add("Resumen anual calculado con las mismas claves que la autoliquidación, no con el formato oficial del " +
                         (igic ? "425" : "390") + " (que tiene muchas más casillas).");
            if (presentadas.Count == 0)
                d.Avisos.Add($"No hay autoliquidaciones {baseModelo.Codigo} presentadas en {p.Anio} con las que contrastar.");
            else
            {
                var suma = presentadas.Sum();
                d.Avisos.Add(suma == anualAntes
                    ? $"Cuadra con la suma de las {presentadas.Count} autoliquidaciones {baseModelo.Codigo} presentadas ({suma:N2})."
                    : $"NO cuadra: resultado anual {anualAntes:N2} frente a {suma:N2} en las {presentadas.Count} autoliquidaciones {baseModelo.Codigo} presentadas. Hay facturas contabilizadas después de presentar o periodos sin presentar.");
            }
        }
    }

    // ---------------------------------------------------------------- 130
    private static async Task Calcular130Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa,
        ModeloSoportado modelo, PeriodoFiscal p, DeclaracionCalculada d, CancellationToken ct)
    {
        if (empresa.RegimenIrpf is not ("DIRECTA_NORMAL" or "DIRECTA_SIMPLIFICADA"))
            throw new ReglaContableException("El modelo 130 es para empresarios o profesionales en estimación directa.",
                "Esta empresa no tiene configurado ese régimen de IRPF.");

        // Acumulado del año natural hasta el fin del trimestre; los asientos de regularización y cierre no cuentan
        var totales = await cn.QuerySingleAsync<TotalesFila>(new CommandDefinition("""
            SELECT COALESCE(SUM(CASE WHEN c.codigo LIKE ANY(@ing) THEN a.haber - a.debe END), 0) AS ingresos,
                   COALESCE(SUM(CASE WHEN c.codigo LIKE ANY(@gas) THEN a.debe - a.haber END), 0) AS gastos
            FROM conta.apunte a
            JOIN conta.asiento s ON s.id = a.asiento_id
            JOIN conta.cuenta c ON c.id = a.cuenta_id
            WHERE a.empresa_id = @empresaId AND s.estado = 'CONTABILIZADO'
              AND s.origen NOT IN ('REGULARIZACION', 'CIERRE', 'APERTURA')
              AND s.fecha BETWEEN @inicioAnio AND @fin
            """, new
        {
            empresaId, inicioAnio = p.InicioAnio, fin = p.Fin,
            ing = Calculador130.PrefijosIngresos.Select(x => x + "%").ToArray(),
            gas = Calculador130.PrefijosGastos.Select(x => x + "%").ToArray()
        }, tx, cancellationToken: ct));

        var retenciones = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(retencion_total), 0) FROM fiscal.factura
            WHERE empresa_id = @empresaId AND libro = 'EMITIDAS' AND estado = 'CONTABILIZADA' AND fecha_contable BETWEEN @inicioAnio AND @fin
            """, new { empresaId, inicioAnio = p.InicioAnio, fin = p.Fin }, tx, cancellationToken: ct));

        var mid = await ModeloIdAsync(cn, tx, modelo, ct);
        var anteriores = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(d.a_ingresar), 0) FROM fiscal.declaracion d
            WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid AND d.anio = @anio AND d.estado = 'PRESENTADA' AND d.fecha_fin < @inicio
              AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                    WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
            """, new { empresaId, mid, anio = (short)p.Anio, inicio = p.Inicio }, tx, cancellationToken: ct));

        Calculador130.Calcular(new Entrada130(totales.Ingresos, totales.Gastos, retenciones, anteriores), d);

        var previos = p.Codigo == "1T" ? 0 : await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(DISTINCT d.periodo) FROM fiscal.declaracion d
            WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid AND d.anio = @anio AND d.estado = 'PRESENTADA' AND d.fecha_fin < @inicio
            """, new { empresaId, mid, anio = (short)p.Anio, inicio = p.Inicio }, tx, cancellationToken: ct));
        var esperados = p.Codigo[0] - '1';
        if (previos < esperados)
            d.Avisos.Add($"Solo hay {previos} de {esperados} trimestres anteriores presentados en la aplicación: los pagos fraccionados anteriores pueden estar incompletos.");
        d.Avisos.Add($"Ingresos: cuentas {string.Join(", ", Calculador130.PrefijosIngresos)}. Gastos: cuentas {string.Join(", ", Calculador130.PrefijosGastos)}. Revisa que correspondan a tu plan contable.");
    }

    // ---------------------------------------------------------------- 347 / 415
    private static async Task Calcular347Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, PeriodoFiscal p,
        DeclaracionCalculada d, CancellationToken ct)
    {
        var filas = await cn.QueryAsync<OperacionFila>(new CommandDefinition("""
            SELECT f.tercero_id, n.numero AS nif, t.nombre,
                   COALESCE(f.clave_operacion_347, CASE f.libro WHEN 'EMITIDAS' THEN 'B' ELSE 'A' END) AS clave,
                   EXTRACT(QUARTER FROM f.fecha_contable)::int AS trimestre,
                   SUM(f.base_total + f.cuota_total + f.recargo_total) AS importe
            FROM fiscal.factura f
            JOIN conta.tercero t ON t.id = f.tercero_id
            JOIN core.nif n ON n.id = t.nif_id
            WHERE f.empresa_id = @empresaId AND f.estado = 'CONTABILIZADA' AND f.fecha_contable BETWEEN @inicio AND @fin
              -- No van al 347 las operaciones del 349 (intracomunitarias), las importaciones ni las exportaciones
              AND NOT EXISTS (SELECT 1 FROM fiscal.factura_iva x WHERE x.factura_id = f.id
                              AND x.tipo_operacion IN ('INTRACOM_ENTREGA','INTRACOM_ADQUISICION','IMPORTACION','EXPORTACION'))
            GROUP BY 1, 2, 3, 4, 5
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct));
        Calculador347.Calcular(filas.Select(f => new Operacion347(f.TerceroId, f.Nif, f.Nombre, f.Clave, f.Trimestre, f.Importe)).ToList(), d);
    }

    // =====================================================================
    // Guardar / consultar / presentar
    // =====================================================================
    private static DeclaracionVista VistaDe(long? id, ModeloSoportado m, string estado, DeclaracionCalculada d)
        => new(id, m.Codigo, m.Nombre, estado, d.Anio, d.Periodo, d.FechaInicio, d.FechaFin, d.Casillas, d.Resultado, d.AIngresar,
               d.ADevolver, d.ACompensar, d.Avisos, d.Detalle, d.PlazoDesde, d.PlazoHasta);

    public async Task<DeclaracionVista> GuardarAsync(long empresaId, long usuarioId, CalcularModeloCmd cmd, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var modelo = ModelosFiscales.Obtener(cmd.Modelo);
        var mid = await ModeloIdAsync(cn, tx, modelo, ct);
        var p = ModelosFiscales.ResolverPeriodo(modelo, cmd.Anio, cmd.Periodo);

        // Bloqueo del periodo para que dos guardados simultáneos no se pisen
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"declaracion|{empresaId}|{modelo.Codigo}|{p.Anio}|{p.Codigo}" }, tx, cancellationToken: ct));

        var existente = await cn.QuerySingleOrDefaultAsync<ExistenteFila>(new CommandDefinition("""
            SELECT id, estado FROM fiscal.declaracion
            WHERE empresa_id = @empresaId AND modelo_id = @mid AND anio = @anio AND periodo = @periodo
            ORDER BY numero_presentacion DESC LIMIT 1
            """, new { empresaId, mid, anio = (short)p.Anio, periodo = p.Codigo }, tx, cancellationToken: ct));
        if (existente is { Estado: "PRESENTADA" })
            throw new ReglaContableException($"El modelo {modelo.Codigo} {p.Anio} {p.Codigo} ya está presentado.",
                "Las declaraciones complementarias y sustitutivas todavía no están soportadas.");

        var d = await CalcularInternoAsync(cn, tx, empresaId, cmd, ct);
        var datos = JsonSerializer.Serialize(new { avisos = d.Avisos, detalle = d.Detalle }, Json);
        var opciones = JsonSerializer.Serialize(new { cmd.SolicitaDevolucion }, Json);

        long id;
        if (existente is not null)
        {
            id = existente.Id;
            await cn.ExecuteAsync(new CommandDefinition("""
                UPDATE fiscal.declaracion SET estado = 'CALCULADA', fecha_inicio = @inicio, fecha_fin = @fin, resultado = @resultado,
                    a_ingresar = @aIngresar, a_devolver = @aDevolver, a_compensar = @aCompensar, datos = @datos::jsonb, opciones = @opciones::jsonb,
                    calculada_en = now(), creado_por = @usuarioId
                WHERE id = @id
                """, new { id, inicio = d.FechaInicio, fin = d.FechaFin, resultado = d.Resultado, aIngresar = d.AIngresar, aDevolver = d.ADevolver,
                    aCompensar = d.ACompensar, datos, opciones, usuarioId }, tx, cancellationToken: ct));
            await cn.ExecuteAsync(new CommandDefinition("DELETE FROM fiscal.declaracion_casilla WHERE declaracion_id = @id", new { id }, tx, cancellationToken: ct));
        }
        else
        {
            id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO fiscal.declaracion (empresa_id, modelo_id, anio, periodo, fecha_inicio, fecha_fin, estado, resultado, a_ingresar,
                                                a_devolver, a_compensar, datos, opciones, calculada_en, creado_por)
                VALUES (@empresaId, @mid, @anio, @periodo, @inicio, @fin, 'CALCULADA', @resultado, @aIngresar, @aDevolver, @aCompensar,
                        @datos::jsonb, @opciones::jsonb, now(), @usuarioId)
                RETURNING id
                """, new { empresaId, mid, anio = (short)p.Anio, periodo = p.Codigo, inicio = d.FechaInicio, fin = d.FechaFin, resultado = d.Resultado,
                    aIngresar = d.AIngresar, aDevolver = d.ADevolver, aCompensar = d.ACompensar, datos, opciones, usuarioId }, tx, cancellationToken: ct));
        }

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.declaracion_casilla (declaracion_id, casilla, importe, texto, orden) VALUES (@id, @clave, @importe, @texto, @orden)
            """, d.Casillas.Select((c, i) => new { id, clave = c.Clave, importe = c.Importe, texto = c.Descripcion, orden = (short)i }),
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return VistaDe(id, modelo, "CALCULADA", d);
    }

    public async Task<IReadOnlyList<DeclaracionResumenDto>> ListarAsync(long empresaId, int? anio, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<DeclaracionResumenDto>(new CommandDefinition("""
            SELECT d.id, m.codigo AS modelo, m.nombre, d.anio, d.periodo, d.numero_presentacion, d.estado, d.resultado,
                   d.a_ingresar, d.a_devolver, d.a_compensar, d.fecha_presentacion, d.justificante,
                   s.id AS asiento_id, s.numero AS asiento_numero
            FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id
            LEFT JOIN conta.asiento s ON s.id = d.asiento_id AND s.estado <> 'ANULADO'
            WHERE d.empresa_id = @empresaId AND d.estado <> 'ANULADA' AND (@anio::int IS NULL OR d.anio = @anio)
            ORDER BY d.anio DESC, d.fecha_fin DESC, m.codigo, d.numero_presentacion DESC
            """, new { empresaId, anio }, cancellationToken: ct))).AsList();
    }

    private static async Task<DeclaracionFila> CargarAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long declaracionId, bool bloquear, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<DeclaracionFila>(new CommandDefinition($"""
               SELECT d.id, m.codigo, m.administracion, m.nombre, d.anio, d.periodo, d.fecha_inicio, d.fecha_fin, d.numero_presentacion, d.estado,
                      d.resultado, d.a_ingresar, d.a_devolver, d.a_compensar, d.datos::text AS datos, d.opciones::text AS opciones
               FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id
               WHERE d.id = @declaracionId AND d.empresa_id = @empresaId {(bloquear ? "FOR UPDATE OF d" : "")}
               """, new { declaracionId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("La declaración no existe.");

    public async Task<DeclaracionVista> ObtenerAsync(long empresaId, long declaracionId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var f = await CargarAsync(cn, null, empresaId, declaracionId, false, ct);
        var casillas = (await cn.QueryAsync<CasillaFila>(new CommandDefinition(
            "SELECT casilla, texto, importe FROM fiscal.declaracion_casilla WHERE declaracion_id = @declaracionId ORDER BY orden, casilla",
            new { declaracionId }, cancellationToken: ct))).Select(c => new Casilla(c.Casilla, c.Texto ?? "", c.Importe ?? 0m)).ToList();

        var avisos = new List<string>(); object? detalle = null;
        if (!string.IsNullOrWhiteSpace(f.Datos))
        {
            using var doc = JsonDocument.Parse(f.Datos);
            if (doc.RootElement.TryGetProperty("avisos", out var a) && a.ValueKind == JsonValueKind.Array)
                avisos.AddRange(a.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (doc.RootElement.TryGetProperty("detalle", out var det) && det.ValueKind != JsonValueKind.Null)
                detalle = det.Clone();
        }
        var p = PeriodoFiscal.Resolver(f.Anio, f.Periodo);
        var (pd, ph) = PlazoPresentacion.Calcular(f.Codigo, p);
        return new DeclaracionVista(f.Id, f.Codigo, f.Nombre, f.Estado, f.Anio, f.Periodo, f.FechaInicio, f.FechaFin, casillas,
            f.Resultado ?? 0, f.AIngresar ?? 0, f.ADevolver ?? 0, f.ACompensar ?? 0, avisos, detalle, pd, ph);
    }

    public async Task<DeclaracionVista> PresentarAsync(long empresaId, long declaracionId, PresentarDeclaracionCmd cmd, CancellationToken ct)
    {
        await using (var cn = await conexiones.AbrirAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var f = await CargarAsync(cn, tx, empresaId, declaracionId, true, ct);
            if (f.Estado != "CALCULADA")
                throw new ReglaContableException(f.Estado == "PRESENTADA" ? "La declaración ya está presentada." : "Guarda (calcula) la declaración antes de presentarla.");
            if (cmd.FechaPresentacion <= f.FechaFin)
                throw new ReglaContableException("La fecha de presentación debe ser posterior al fin del periodo.");
            if (cmd.Justificante is { Length: > 30 })
                throw new ReglaContableException("El justificante no puede superar los 30 caracteres.");

            // Si los datos contables cambiaron desde que se guardó, no se presenta una cifra obsoleta
            var solicita = false;
            if (!string.IsNullOrWhiteSpace(f.Opciones))
            {
                using var doc = JsonDocument.Parse(f.Opciones);
                solicita = doc.RootElement.TryGetProperty("solicitaDevolucion", out var s) && s.ValueKind == JsonValueKind.True;
            }
            var actual = await CalcularInternoAsync(cn, tx, empresaId,
                new CalcularModeloCmd { Modelo = f.Codigo, Anio = f.Anio, Periodo = f.Periodo, SolicitaDevolucion = solicita }, ct);
            if (actual.Resultado != (f.Resultado ?? 0) || actual.AIngresar != (f.AIngresar ?? 0) || actual.ADevolver != (f.ADevolver ?? 0))
                throw new ReglaContableException("Los datos contables han cambiado desde que se calculó la declaración.",
                    $"Guardada: {f.Resultado:N2}; ahora: {actual.Resultado:N2}. Vuelve a calcularla y revísala.");

            await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE fiscal.declaracion SET estado = 'PRESENTADA', fecha_presentacion = @fecha, justificante = @justificante WHERE id = @declaracionId",
                new { fecha = cmd.FechaPresentacion, justificante = string.IsNullOrWhiteSpace(cmd.Justificante) ? null : cmd.Justificante.Trim(), declaracionId },
                tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        return await ObtenerAsync(empresaId, declaracionId, ct);
    }

    public async Task AnularAsync(long empresaId, long declaracionId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var n = await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE fiscal.declaracion SET estado = 'ANULADA' WHERE id = @declaracionId AND empresa_id = @empresaId AND estado IN ('BORRADOR','CALCULADA')",
            new { declaracionId, empresaId }, cancellationToken: ct));
        if (n == 0) throw new ReglaContableException("Solo se pueden descartar declaraciones calculadas que no estén presentadas.");
    }
}
