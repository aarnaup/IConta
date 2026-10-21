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
        public bool TributaIs { get; set; }
        public string IsTipo { get; set; } = "GENERAL";
        public decimal? IsPorcentaje { get; set; }
        public string IsModalidad202 { get; set; } = "ART_40_2";
    }
    private sealed class EjercicioIsFila { public long Id { get; set; } public DateOnly FechaInicio { get; set; } public DateOnly FechaFin { get; set; } }
    private sealed class Resumen303Fila
    {
        public string Periodo { get; set; } = "";
        public decimal Resultado { get; set; }
        public decimal AIngresar { get; set; }
        public decimal ADevolver { get; set; }
        public decimal ACompensar { get; set; }
        public decimal PendienteInicial { get; set; }
        public decimal Aplicada { get; set; }
        public decimal PendientePosterior { get; set; }
    }
    private sealed class Prorrata390Fila
    {
        public string Tipo { get; set; } = "";
        public decimal OperacionesTotales { get; set; }
        public decimal OperacionesConDerecho { get; set; }
        public int? PorcentajeDefinitivo { get; set; }
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
        public string TipoDeclaracion { get; set; } = "ORDINARIA";
        public string Estado { get; set; } = "";
        public decimal? Resultado { get; set; }
        public decimal? AIngresar { get; set; }
        public decimal? ADevolver { get; set; }
        public decimal? ACompensar { get; set; }
        public string? Datos { get; set; }
        public string? Opciones { get; set; }
    }
    private sealed class TotalesFila { public decimal Ingresos { get; set; } public decimal Gastos { get; set; } }
    private sealed class ExistenteFila { public long Id { get; set; } public string Estado { get; set; } = ""; public short NumeroPresentacion { get; set; } }
    private sealed class AnteriorFila { public short Numero { get; set; } public decimal Resultado { get; set; } public decimal Ingresado { get; set; } }
    private sealed class CasillaFila { public string Casilla { get; set; } = ""; public string? Texto { get; set; } public decimal? Importe { get; set; } }

    // =====================================================================
    // Modelos aplicables a cada empresa
    // =====================================================================
    public async Task<IReadOnlyList<ModeloSoportado>> ModelosDisponiblesAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await EmpresaAsync(cn, null, empresaId, ct);
        var directa = e.RegimenIrpf is "DIRECTA_NORMAL" or "DIRECTA_SIMPLIFICADA";
        // Los modelos que el administrador ha desactivado en Catálogos no se ofrecen
        var inactivos = (await cn.QueryAsync<string>(new CommandDefinition(
            "SELECT codigo FROM fiscal.modelo_fiscal WHERE NOT activo", cancellationToken: ct))).ToHashSet();
        return ModelosFiscales.Soportados.Where(m => !inactivos.Contains(m.Codigo)).Where(m => m.Impuesto switch
        {
            // Recargo de equivalencia y REAGP: no hay autoliquidación ni resumen anual; el simplificado está exonerado del 390
            "IVA" => e.ImpuestoIndirecto == "IVA" && e.RegimenIva is not ("RECARGO_EQUIVALENCIA" or "AGRICULTURA_GYP")
                     && !(m.Codigo == "390" && e.RegimenIva == "SIMPLIFICADO"),
            // Comerciante minorista: no presenta el 420 (sus ventas están exentas); el 425 informa de ellas
            "IVA_NP" => e.ImpuestoIndirecto == "IVA" && e.RegimenIva is "RECARGO_EQUIVALENCIA" or "AGRICULTURA_GYP",
            "IGIC" => e.ImpuestoIndirecto == "IGIC" && !(m.Codigo == "420" && e.RegimenIva == "COMERCIANTE_MINORISTA"),
            "IRPF" => m.Codigo == "131" ? e.RegimenIrpf == "OBJETIVA" : directa,
            "TERCEROS" => m.Codigo == "415" ? e.ImpuestoIndirecto == "IGIC" : e.ImpuestoIndirecto != "IGIC",
            "INTRACOM" => e.ImpuestoIndirecto == "IVA",
            "RETENCIONES" => true,
            "IS" => e.TributaIs,
            _ => false
        }).ToList();
    }

    private static async Task<EmpresaFila> EmpresaAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<EmpresaFila>(new CommandDefinition(
               "SELECT impuesto_indirecto, regimen_iva, regimen_irpf, tributa_is, is_tipo, is_porcentaje, is_modalidad_202 FROM core.empresa WHERE id = @empresaId",
               new { empresaId }, tx, cancellationToken: ct))
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
            case "IVA_NP":
                await Calcular309Async(cn, tx, empresaId, empresa, p, d, ct);
                break;
            case "IRPF" when modelo.Codigo == "131":
                await Calcular131Async(cn, tx, empresaId, empresa, p, d, ct);
                break;
            case "IRPF":
                await Calcular130Async(cn, tx, empresaId, empresa, modelo, p, d, ct);
                break;
            case "TERCEROS":
                await Calcular347Async(cn, tx, empresaId, p, d, ct);
                break;
            case "RETENCIONES":
                await CalcularRetencionesAsync(cn, tx, empresaId, modelo, p, d, ct);
                break;
            case "INTRACOM":
                await Calcular349Async(cn, tx, empresaId, empresa, p, d, ct);
                break;
            case "IS" when modelo.Codigo == "200":
                await Calcular200Async(cn, tx, empresaId, empresa, p, d, ct);
                break;
            case "IS":
                await Calcular202Async(cn, tx, empresaId, empresa, p, d, ct);
                break;
        }

        var tipo = (cmd.TipoDeclaracion ?? "ORDINARIA").Trim().ToUpperInvariant();
        if (!Complementaria.Tipos.Contains(tipo)) throw new ReglaContableException("Tipo de declaración no válido: ORDINARIA, COMPLEMENTARIA o SUSTITUTIVA.");
        if (tipo != "ORDINARIA")
        {
            var mid = await ModeloIdAsync(cn, tx, modelo, ct);
            var ant = await cn.QuerySingleOrDefaultAsync<AnteriorFila>(new CommandDefinition("""
                SELECT d.numero_presentacion AS numero, COALESCE(d.resultado, 0) AS resultado,
                       (SELECT COALESCE(SUM(x.a_ingresar), 0) FROM fiscal.declaracion x
                        WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo
                          AND x.estado = 'PRESENTADA') AS ingresado
                FROM fiscal.declaracion d
                WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid AND d.anio = @anio AND d.periodo = @periodo AND d.estado = 'PRESENTADA'
                ORDER BY d.numero_presentacion DESC LIMIT 1
                """, new { empresaId, mid, anio = (short)p.Anio, periodo = p.Codigo }, tx, cancellationToken: ct))
                ?? throw new ReglaContableException($"No hay un modelo {modelo.Codigo} {p.Anio} {p.Codigo} presentado que complementar o sustituir.");
            // La sustitutiva no descuenta lo ingresado: queda la suma de lo pagado para la devolución de ingresos indebidos
            Complementaria.Aplicar(modelo.Codigo, tipo, new PresentacionAnterior(ant.Numero, ant.Resultado, ant.Ingresado), d);
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
        if (empresa.RegimenIva == "RECARGO_EQUIVALENCIA")
            throw new ReglaContableException($"En el recargo de equivalencia no se presenta el {modelo.Codigo}: el IVA se paga a los proveedores con el recargo.",
                "Solo las adquisiciones intracomunitarias y las operaciones con inversión del sujeto pasivo se declaran, en el modelo 309.");
        if (empresa.RegimenIva == "SIMPLIFICADO" && modelo.Codigo == "390")
            throw new ReglaContableException("Quien tributa solo en el régimen simplificado está exonerado del 390.");
        if (empresa.RegimenIva == "AGRICULTURA_GYP")
            throw new ReglaContableException($"En el régimen especial de la agricultura, ganadería y pesca no se presenta el {modelo.Codigo}.",
                "Las adquisiciones intracomunitarias y las operaciones con inversión del sujeto pasivo se declaran en el modelo 309.");
        if (empresa.RegimenIva == "COMERCIANTE_MINORISTA" && modelo.Codigo == "420")
            throw new ReglaContableException("El comerciante minorista del IGIC no presenta el 420: sus ventas están exentas y el IGIC de las compras es coste.");
        if (empresa.RegimenIva is not ("GENERAL" or "CRITERIO_CAJA" or "SIMPLIFICADO" or "COMERCIANTE_MINORISTA"))
            throw new ReglaContableException($"El régimen {empresa.RegimenIva} todavía no está soportado en {modelo.Codigo}.");

        var igic = modelo.Impuesto == "IGIC";
        var grupos = (await cn.QueryAsync<GrupoFila>(new CommandDefinition("""
            SELECT f.libro, (f.tipo_factura = 'RECTIFICATIVA') AS rectificativa,
                   CASE WHEN fi.tipo_operacion = 'INTRACOM_ADQUISICION' AND f.clave_operacion_349 = 'I' THEN 'INTRACOM_SERVICIOS'
                        ELSE fi.tipo_operacion END AS tipo_operacion, fi.porcentaje, fi.porcentaje_recargo,
                   fi.es_bien_inversion, SUM(fi.base) AS base, SUM(fi.cuota) AS cuota, SUM(fi.cuota_recargo) AS cuota_recargo,
                   SUM(fi.cuota_deducible) AS cuota_deducible
            FROM fiscal.factura f
            JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
            JOIN cat.tipo_iva t ON t.id = fi.tipo_iva_id
            WHERE f.empresa_id = @empresaId AND f.estado = 'CONTABILIZADA'
              AND f.fecha_contable BETWEEN @inicio AND @fin AND t.impuesto = @impuesto
              AND NOT (f.criterio_caja AND fi.tipo_operacion = 'INTERIOR')     -- esas se devengan al cobrar o pagar
            GROUP BY 1, 2, 3, 4, 5, 6
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin, impuesto = modelo.Impuesto }, tx, cancellationToken: ct)))
            .Select(g => new GrupoIva(g.Libro, g.Rectificativa, g.TipoOperacion, g.Porcentaje, g.PorcentajeRecargo, g.EsBienInversion,
                                      g.Base, g.Cuota, g.CuotaRecargo, g.CuotaDeducible)).ToList();

        // Criterio de caja: lo cobrado y pagado en el periodo (y lo que llega al 31/12 del año siguiente)
        var (gruposCaja, infoCaja, avisosCaja) = await CriterioCajaRepositorio.ParaDeclaracionAsync(cn, tx, empresaId, modelo.Impuesto, p, ct);
        grupos.AddRange(gruposCaja);
        d.Avisos.AddRange(avisosCaja);

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
            // Casilla de lo compensado en cada periodo: [78] (las declaraciones guardadas antes de 2026 lo llevan en la [68])
            var claveCompensada = igic ? "COMP" : "78";
            var mid = await ModeloIdAsync(cn, tx, modelo, ct);
            pendiente = Math.Max(0m, await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                SELECT COALESCE(SUM(d.a_compensar), 0) - COALESCE(SUM(c.importe), 0)
                FROM fiscal.declaracion d
                LEFT JOIN fiscal.declaracion_casilla c ON c.declaracion_id = d.id
                     AND (c.casilla = @clave OR (@clave = '78' AND c.casilla = '68'
                          AND NOT EXISTS (SELECT 1 FROM fiscal.declaracion_casilla c2 WHERE c2.declaracion_id = d.id AND c2.casilla = '78')))
                WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid AND d.estado = 'PRESENTADA'
                  AND d.fecha_fin < @inicio AND d.fecha_fin >= (@inicio::date - interval '4 years')::date
                  AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                        WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
                """, new { empresaId, mid, inicio = p.Inicio, clave = claveCompensada }, tx, cancellationToken: ct)));
        }

        if (anual && !igic)
        {
            // 390 con las casillas del modelo oficial: necesita además las autoliquidaciones del año y la prorrata
            var mid303 = await ModeloIdAsync(cn, tx, ModelosFiscales.Obtener("303"), ct);
            var autoliq = (await cn.QueryAsync<Resumen303Fila>(new CommandDefinition("""
                SELECT d.periodo, COALESCE(d.resultado, 0) AS resultado, COALESCE(d.a_ingresar, 0) AS a_ingresar,
                       COALESCE(d.a_devolver, 0) AS a_devolver, COALESCE(d.a_compensar, 0) AS a_compensar,
                       COALESCE((SELECT c.importe FROM fiscal.declaracion_casilla c WHERE c.declaracion_id = d.id AND c.casilla = '110'), 0) AS pendiente_inicial,
                       COALESCE((SELECT c.importe FROM fiscal.declaracion_casilla c WHERE c.declaracion_id = d.id AND c.casilla IN ('78', '68')
                                 ORDER BY c.casilla DESC LIMIT 1), 0) AS aplicada,
                       COALESCE((SELECT c.importe FROM fiscal.declaracion_casilla c WHERE c.declaracion_id = d.id AND c.casilla = '87'), 0) AS pendiente_posterior
                FROM fiscal.declaracion d
                WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid303 AND d.anio = @anio AND d.estado = 'PRESENTADA'
                  AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                        WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
                ORDER BY d.fecha_inicio
                """, new { empresaId, mid303, anio = (short)p.Anio }, tx, cancellationToken: ct)))
                .Select(a => new Resumen303(a.Periodo, a.Resultado, a.AIngresar, a.ADevolver, a.ACompensar, a.PendienteInicial, a.Aplicada,
                                            a.PendientePosterior)).ToList();
            var prorratas = (await cn.QueryAsync<Prorrata390Fila>(new CommandDefinition("""
                SELECT tipo, COALESCE(operaciones_totales, 0) AS operaciones_totales, COALESCE(operaciones_con_derecho, 0) AS operaciones_con_derecho,
                       porcentaje_definitivo
                FROM fiscal.prorrata WHERE empresa_id = @empresaId AND anio = @anio ORDER BY COALESCE(sector_diferenciado, 0)
                """, new { empresaId, anio = (short)p.Anio }, tx, cancellationToken: ct)))
                .Select(x => new Prorrata390(x.Tipo, x.OperacionesTotales, x.OperacionesConDerecho, x.PorcentajeDefinitivo)).ToList();
            Calculador390.Calcular(new Entrada390(grupos, regBienes, regularizacion, autoliq, infoCaja, prorratas,
                RecargoEquivalencia: empresa.RegimenIva == "RECARGO_EQUIVALENCIA"), d);
            return;
        }

        // Régimen simplificado: lo devengado por las ventas interiores y lo soportado en operaciones corrientes van en los módulos;
        // por el régimen general quedan las adquisiciones intracomunitarias, la inversión del sujeto pasivo y los bienes de inversión
        decimal cuotaSimplificado = 0;
        if (empresa.RegimenIva == "SIMPLIFICADO")
        {
            grupos = grupos.Where(g => g.Libro == "EMITIDAS"
                    ? g.TipoOperacion != "INTERIOR"
                    : g.EsBienInversion || g.TipoOperacion is "INTRACOM_ADQUISICION" or "INTRACOM_SERVICIOS" or "INVERSION_SUJETO_PASIVO").ToList();
            var modulos = await ModulosRepositorio.DelAnioAsync(cn, tx, empresaId, p.Anio, ct);
            decimal anteriores = 0;
            if (p.EsUltimoDelAnio)
            {
                var mid303 = await ModeloIdAsync(cn, tx, modelo, ct);
                anteriores = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                    SELECT COALESCE(SUM(c.importe), 0) FROM fiscal.declaracion d
                    JOIN fiscal.declaracion_casilla c ON c.declaracion_id = d.id AND c.casilla = 'RS'
                    WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid303 AND d.anio = @anio AND d.estado = 'PRESENTADA' AND d.fecha_fin < @inicio
                      AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                            WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
                    """, new { empresaId, mid303, anio = (short)p.Anio, inicio = p.Inicio }, tx, cancellationToken: ct));
            }
            cuotaSimplificado = CalculadorModulos.Simplificado303(modulos, p.EsUltimoDelAnio, anteriores, d);
            d.Avisos.Add($"Régimen simplificado: la cuota sale de los módulos del año; las ventas interiores y las compras corrientes no van en el {modelo.Codigo}. " +
                         (igic ? "VERIFICA con el formulario del 420 del año cómo se declara el ingreso a cuenta del simplificado del IGIC."
                               : "El fichero de presentación no lleva la parte del simplificado: complétala en el formulario."));
        }

        CalculadorImpuestoIndirecto.Calcular(igic,
            new EntradaImpuestoIndirecto(grupos, regularizacion, pendiente, cmd.SolicitaDevolucion, p.EsUltimoDelAnio, regBienes, infoCaja,
                cuotaSimplificado), d);

        if (anual)
        {
            // El resumen anual es informativo: no genera ingreso, devolución ni compensación
            d.AIngresar = d.ADevolver = d.ACompensar = 0;
            d.Casillas.RemoveAll(c => c.Clave is "77" or "110" or "78" or "87" or "69" or "70" or "71" or "COMP" or "PEND_ANT" or "PEND_SIG" or "LIQ");
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

    // ---------------------------------------------------------------- 309
    private sealed class Linea309Fila
    {
        public string Clase { get; set; } = "";
        public decimal Porcentaje { get; set; }
        public decimal PorcentajeRecargo { get; set; }
        public decimal Base { get; set; }
    }

    private static async Task Calcular309Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa, PeriodoFiscal p,
        DeclaracionCalculada d, CancellationToken ct)
    {
        if (empresa.ImpuestoIndirecto != "IVA" || empresa.RegimenIva is not ("RECARGO_EQUIVALENCIA" or "AGRICULTURA_GYP"))
            throw new ReglaContableException("El 309 lo presentan los empresarios en recargo de equivalencia o en el régimen especial de la agricultura, " +
                                             "ganadería y pesca (en el régimen general esas operaciones van en el 303).");
        var lineas = (await cn.QueryAsync<Linea309Fila>(new CommandDefinition("""
            SELECT CASE WHEN fi.tipo_operacion = 'INTRACOM_ADQUISICION' AND COALESCE(f.clave_operacion_349, 'A') <> 'I' THEN 'ADQUISICION' ELSE 'ISP' END AS clase,
                   fi.porcentaje, COALESCE(v.recargo_equivalencia, 0) AS porcentaje_recargo, SUM(fi.base) AS base
            FROM fiscal.factura f
            JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
            LEFT JOIN cat.tipo_iva_vigencia v ON v.id = fi.tipo_iva_vigencia_id
            WHERE f.empresa_id = @empresaId AND f.libro = 'RECIBIDAS' AND f.estado = 'CONTABILIZADA' AND f.fecha_contable BETWEEN @inicio AND @fin
              AND fi.tipo_operacion IN ('INTRACOM_ADQUISICION', 'INVERSION_SUJETO_PASIVO')
            GROUP BY 1, 2, 3
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct)))
            .Select(x => new Linea309(x.Clase, x.Porcentaje, x.PorcentajeRecargo, x.Base)).ToList();
        Calculador309.Calcular(lineas, empresa.RegimenIva == "RECARGO_EQUIVALENCIA", d);
    }

    // ---------------------------------------------------------------- 200 / 202 (Impuesto sobre Sociedades)
    // Resultado antes del impuesto: ingresos menos gastos sin las cuentas del propio impuesto (630, 633, 638)
    private const string SqlResultadoIs = """
        SELECT COALESCE(SUM(CASE WHEN c.codigo LIKE '7%' THEN a.haber - a.debe
                                 WHEN c.codigo LIKE '6%' AND c.codigo NOT LIKE '630%' AND c.codigo NOT LIKE '633%' AND c.codigo NOT LIKE '638%'
                                 THEN a.haber - a.debe END), 0)
        FROM conta.apunte a JOIN conta.asiento s ON s.id = a.asiento_id JOIN conta.cuenta c ON c.id = a.cuenta_id
        WHERE a.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId AND s.estado = 'CONTABILIZADO'
          AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE') AND s.fecha BETWEEN @desde AND @hasta
        """;
    // Retenciones e ingresos a cuenta soportados (473), sin los pagos fraccionados ni la liquidación del propio impuesto
    private const string SqlRetencionesIs = """
        SELECT COALESCE(SUM(a.debe), 0)
        FROM conta.apunte a JOIN conta.asiento s ON s.id = a.asiento_id JOIN conta.cuenta c ON c.id = a.cuenta_id
        WHERE a.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId AND s.estado = 'CONTABILIZADO'
          AND s.origen NOT IN ('APERTURA', 'REGULARIZACION', 'CIERRE', 'IMPUESTO_SOCIEDADES') AND s.fecha BETWEEN @desde AND @hasta AND c.codigo LIKE '473%'
        """;

    private static void ExigeIs(EmpresaFila e)
    {
        if (!e.TributaIs)
            throw new ReglaContableException("La empresa no tributa por el Impuesto sobre Sociedades.", "Márcalo en la configuración fiscal de la empresa.");
    }

    /// <summary>Bases negativas pendientes al empezar el periodo: las de antes de la aplicación más las generadas en los 200 presentados, menos lo compensado.</summary>
    private static async Task<decimal> BinsDisponiblesAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, DateOnly inicio, CancellationToken ct)
        => Math.Max(0, await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE((SELECT SUM(i.bins_anteriores) FROM fiscal.impuesto_sociedades i JOIN core.ejercicio e ON e.id = i.ejercicio_id
                             WHERE i.empresa_id = @empresaId AND e.fecha_inicio <= @inicio), 0)
                 + COALESCE((SELECT SUM(GREATEST(-COALESCE(bi.importe, 0), 0) - COALESCE(cp.importe, 0))
                             FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id AND m.codigo = '200'
                             LEFT JOIN fiscal.declaracion_casilla bi ON bi.declaracion_id = d.id AND bi.casilla = 'BI'
                             LEFT JOIN fiscal.declaracion_casilla cp ON cp.declaracion_id = d.id AND cp.casilla = 'COMP'
                             WHERE d.empresa_id = @empresaId AND d.estado = 'PRESENTADA' AND d.fecha_fin < @inicio
                               AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                                    WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo
                                      AND x.estado = 'PRESENTADA')), 0)
            """, new { empresaId, inicio }, tx, cancellationToken: ct)));

    private static async Task Calcular200Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa, PeriodoFiscal p,
        DeclaracionCalculada d, CancellationToken ct)
    {
        ExigeIs(empresa);
        // Periodo impositivo: el ejercicio que empieza en el año (también si es partido: 1/7/2025–30/6/2026 es el 200 de 2025)
        var ej = await cn.QueryFirstOrDefaultAsync<EjercicioIsFila>(new CommandDefinition("""
            SELECT id, fecha_inicio, fecha_fin FROM core.ejercicio
            WHERE empresa_id = @empresaId AND EXTRACT(YEAR FROM fecha_inicio) = @anio ORDER BY fecha_inicio LIMIT 1
            """, new { empresaId, anio = p.Anio }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException($"No hay ningún ejercicio que empiece en {p.Anio}.");
        d.FechaInicio = ej.FechaInicio; d.FechaFin = ej.FechaFin; d.EjercicioId = ej.Id;
        (d.PlazoDesde, d.PlazoHasta) = PlazoPresentacion.Calcular("200", p with { Inicio = ej.FechaInicio, Fin = ej.FechaFin });
        var q = new { empresaId, ejercicioId = ej.Id, desde = ej.FechaInicio, hasta = ej.FechaFin };
        var resultado = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition(SqlResultadoIs, q, tx, cancellationToken: ct));
        var retenciones = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition(SqlRetencionesIs, q, tx, cancellationToken: ct));
        var pagos = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(d.a_ingresar), 0) FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id AND m.codigo = '202'
            WHERE d.empresa_id = @empresaId AND d.ejercicio_id = @ejercicioId AND d.estado = 'PRESENTADA'
            """, q, tx, cancellationToken: ct));
        var datos = await SociedadesRepositorio.LeerAsync(cn, tx, empresaId, ej.Id, ct);
        var bins = await BinsDisponiblesAsync(cn, tx, empresaId, ej.FechaInicio, ct);
        CalculadorSociedades.Calcular200(new Entrada200(resultado, datos.Lista, datos.ReservaCapitalizacion, bins, datos.Incn, datos.Bonificaciones,
            datos.Deducciones, retenciones, pagos, new TipoGravamenIs(empresa.IsTipo, empresa.IsPorcentaje), ej.FechaInicio, ej.FechaFin), d);
        if (ej.FechaFin.DayNumber - ej.FechaInicio.DayNumber < 364)
            d.Avisos.Add("Periodo impositivo inferior al año: revisa los límites proporcionales (reserva de capitalización, compensación de bases negativas).");
    }

    private static async Task Calcular202Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa, PeriodoFiscal p,
        DeclaracionCalculada d, CancellationToken ct)
    {
        ExigeIs(empresa);
        // Periodo impositivo en curso el primer día del plazo (1 de abril, octubre o diciembre)
        var plazo = new DateOnly(p.Anio, p.Codigo switch { "1P" => 4, "2P" => 10, _ => 12 }, 1);
        var ej = await cn.QueryFirstOrDefaultAsync<EjercicioIsFila>(new CommandDefinition("""
            SELECT id, fecha_inicio, fecha_fin FROM core.ejercicio
            WHERE empresa_id = @empresaId AND @plazo BETWEEN fecha_inicio AND fecha_fin ORDER BY fecha_inicio LIMIT 1
            """, new { empresaId, plazo }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException($"No hay ningún ejercicio en curso el {plazo:dd/MM/yyyy}.");
        var finBase = plazo.AddDays(-1);
        if (finBase < ej.FechaInicio)
            throw new ReglaContableException("El periodo impositivo empieza el mismo mes del plazo: no hay pago fraccionado en este periodo.");
        d.FechaInicio = ej.FechaInicio; d.FechaFin = finBase; d.EjercicioId = ej.Id;
        var tipo = new TipoGravamenIs(empresa.IsTipo, empresa.IsPorcentaje);
        if (empresa.IsModalidad202 == "ART_40_3")
        {
            var q = new { empresaId, ejercicioId = ej.Id, desde = ej.FechaInicio, hasta = finBase };
            var baseAcum = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition(SqlResultadoIs, q, tx, cancellationToken: ct));
            var ret = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition(SqlRetencionesIs, q, tx, cancellationToken: ct));
            var anteriores = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                SELECT COALESCE(SUM(d.a_ingresar), 0) FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id AND m.codigo = '202'
                WHERE d.empresa_id = @empresaId AND d.ejercicio_id = @ejercicioId AND d.estado = 'PRESENTADA' AND d.fecha_fin < @hasta
                """, q, tx, cancellationToken: ct));
            var datos = await SociedadesRepositorio.LeerAsync(cn, tx, empresaId, ej.Id, ct);
            var bins = await BinsDisponiblesAsync(cn, tx, empresaId, ej.FechaInicio, ct);
            CalculadorSociedades.Calcular202(new Entrada202("ART_40_3", tipo, ej.FechaInicio.Year, 0, baseAcum, bins, datos.Bonificaciones,
                ret, anteriores), d);
        }
        else
        {
            // Último periodo impositivo cuyo plazo de declaración haya vencido el primer día del plazo del pago fraccionado
            var ultimo = await cn.QueryFirstOrDefaultAsync<decimal?>(new CommandDefinition("""
                SELECT COALESCE((SELECT importe FROM fiscal.declaracion_casilla WHERE declaracion_id = d.id AND casilla = 'CL'), 0)
                     - COALESCE((SELECT importe FROM fiscal.declaracion_casilla WHERE declaracion_id = d.id AND casilla = 'RET'), 0)
                FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id AND m.codigo = '200'
                WHERE d.empresa_id = @empresaId AND d.estado = 'PRESENTADA' AND (d.fecha_fin + interval '6 months 25 days')::date < @plazo
                ORDER BY d.fecha_fin DESC, d.numero_presentacion DESC LIMIT 1
                """, new { empresaId, plazo }, tx, cancellationToken: ct));
            if (ultimo is null) d.Avisos.Add("No hay ningún modelo 200 presentado cuyo plazo haya vencido: el pago fraccionado del art. 40.2 es cero.");
            CalculadorSociedades.Calcular202(new Entrada202("ART_40_2", tipo, ej.FechaInicio.Year, ultimo ?? 0, 0, 0, 0, 0, 0), d);
        }
    }

    // ---------------------------------------------------------------- 131
    private static async Task Calcular131Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa,
        PeriodoFiscal p, DeclaracionCalculada d, CancellationToken ct)
    {
        if (empresa.RegimenIrpf != "OBJETIVA")
            throw new ReglaContableException("El modelo 131 es para empresarios en estimación objetiva (módulos).",
                "Esta empresa no tiene configurado ese régimen de IRPF.");
        var modulos = await ModulosRepositorio.DelAnioAsync(cn, tx, empresaId, p.Anio, ct);
        // Volumen de ingresos del trimestre de las actividades agrícolas (las facturas de su actividad o, si todas lo son, todas)
        var agricolas = modulos.Where(m => m.Agricola).Select(m => m.ActividadId).ToList();
        var todasAgricolas = modulos.Count > 0 && modulos.All(m => m.Agricola);
        var ids = agricolas.Where(x => x is not null).Select(x => x!.Value).ToArray();
        var volumen = agricolas.Count == 0 ? 0m : await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(base_total), 0) FROM fiscal.factura
            WHERE empresa_id = @empresaId AND libro = 'EMITIDAS' AND estado = 'CONTABILIZADA' AND fecha_contable BETWEEN @inicio AND @fin
              AND (@todas OR actividad_id = ANY(@ids))
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin, todas = todasAgricolas, ids }, tx, cancellationToken: ct));
        var retenciones = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
            SELECT COALESCE(SUM(retencion_total), 0) FROM fiscal.factura
            WHERE empresa_id = @empresaId AND libro = 'EMITIDAS' AND estado = 'CONTABILIZADA' AND fecha_contable BETWEEN @inicio AND @fin
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct));
        CalculadorModulos.Calcular131(modulos, volumen, retenciones, d);
        if (agricolas.Count > 0 && !todasAgricolas && ids.Length == 0)
            d.Avisos.Add("Las actividades agrícolas no están enlazadas a una actividad de la empresa: su volumen de ingresos va a cero.");
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
            -- Pagos de trimestres anteriores: el resultado positivo de la última presentación de cada uno (también complementarias)
            SELECT COALESCE(SUM(GREATEST(d.resultado, 0)), 0) FROM fiscal.declaracion d
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

    // ---------------------------------------------------------------- 111 / 115
    private sealed class PercepcionFila
    {
        public long TerceroId { get; set; }
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Modelo { get; set; } = "";
        public decimal Base { get; set; }
        public decimal Retencion { get; set; }
    }

    private sealed class PercepcionAnualFila
    {
        public long TerceroId { get; set; }
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Pais { get; set; } = "";
        public string? Provincia { get; set; }
        public string TipoRetencion { get; set; } = "";
        public decimal Porcentaje { get; set; }
        public decimal Base { get; set; }
        public decimal Retencion { get; set; }
    }
    private sealed class TrabajoFila
    {
        public long TerceroId { get; set; }
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public decimal Dinerario { get; set; }
        public decimal Retencion { get; set; }
        public decimal Especie { get; set; }
        public decimal IngresoACuenta { get; set; }
    }

    private sealed class TrabajoAnualFila
    {
        public long TerceroId { get; set; }
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Pais { get; set; } = "";
        public string? Provincia { get; set; }
        public decimal Dinerario { get; set; }
        public decimal Retencion { get; set; }
        public decimal Especie { get; set; }
        public decimal IngresoACuenta { get; set; }
        public decimal IngresoRepercutido { get; set; }
        public decimal GastosDeducibles { get; set; }
        public short? AnioNacimiento { get; set; }
        public short SituacionFamiliar { get; set; }
        public string? NifConyuge { get; set; }
        public short Discapacidad { get; set; }
        public short Contrato { get; set; }
        public bool MovilidadGeografica { get; set; }
    }

    private sealed class TrimestralFila { public decimal? Importe { get; set; } public int Presentados { get; set; } }

    private static async Task CalcularResumenRetencionesAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, ModeloSoportado modelo,
        PeriodoFiscal p, DeclaracionCalculada d, CancellationToken ct)
    {
        var trimestral = modelo.Codigo == "190" ? "111" : "115";
        var filas = (await cn.QueryAsync<PercepcionAnualFila>(new CommandDefinition("""
            SELECT f.tercero_id, n.numero AS nif, t.nombre, pa.iso2 AS pais, pr.codigo AS provincia, tr.codigo AS tipo_retencion,
                   r.porcentaje, SUM(r.base) AS base, SUM(r.importe) AS retencion
            FROM fiscal.factura f
            JOIN fiscal.factura_retencion r ON r.factura_id = f.id
            JOIN cat.tipo_retencion_irpf tr ON tr.id = r.tipo_retencion_id
            JOIN conta.tercero t ON t.id = f.tercero_id
            JOIN core.nif n ON n.id = t.nif_id
            JOIN cat.pais pa ON pa.id = n.pais_id
            LEFT JOIN cat.provincia pr ON pr.id = n.provincia_id
            WHERE f.empresa_id = @empresaId AND f.libro = 'RECIBIDAS' AND f.estado = 'CONTABILIZADA'
              AND f.fecha_contable BETWEEN @inicio AND @fin AND tr.modelo_ingreso = @trimestral
            GROUP BY 1, 2, 3, 4, 5, 6, 7
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin, trimestral }, tx, cancellationToken: ct)))
            .Select(f => new PercepcionAnual(f.TerceroId, f.Nif, f.Nombre, f.Pais, f.Provincia, f.TipoRetencion, f.Porcentaje, f.Base, f.Retencion)).ToList();

        List<TrabajoAnual>? trabajo = null;
        if (modelo.Codigo == "190")
            trabajo = (await cn.QueryAsync<TrabajoAnualFila>(new CommandDefinition("""
                SELECT n.tercero_id, ni.numero AS nif, t.nombre, pa.iso2 AS pais, pr.codigo AS provincia,
                       SUM(n.bruto) AS dinerario, SUM(n.retencion) AS retencion,
                       SUM(n.especie + CASE WHEN n.ingreso_repercutido THEN 0 ELSE n.ingreso_a_cuenta END) AS especie,
                       SUM(n.ingreso_a_cuenta) AS ingreso_a_cuenta, SUM(CASE WHEN n.ingreso_repercutido THEN n.ingreso_a_cuenta ELSE 0 END) AS ingreso_repercutido,
                       SUM(n.ss_trabajador) AS gastos_deducibles,
                       e.anio_nacimiento, COALESCE(e.situacion_familiar, 3) AS situacion_familiar, e.nif_conyuge, COALESCE(e.discapacidad, 0) AS discapacidad,
                       COALESCE(e.contrato, 1) AS contrato, COALESCE(e.movilidad_geografica, false) AS movilidad_geografica
                FROM conta.nomina n
                JOIN conta.tercero t ON t.id = n.tercero_id
                JOIN core.nif ni ON ni.id = t.nif_id
                JOIN cat.pais pa ON pa.id = ni.pais_id
                LEFT JOIN cat.provincia pr ON pr.id = ni.provincia_id
                LEFT JOIN conta.empleado e ON e.tercero_id = n.tercero_id
                WHERE n.empresa_id = @empresaId AND n.estado = 'CONTABILIZADA' AND n.fecha BETWEEN @inicio AND @fin
                GROUP BY 1, 2, 3, 4, 5, 12, 13, 14, 15, 16, 17
                """, new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct)))
                .Select(x => new TrabajoAnual(x.TerceroId, x.Nif, x.Nombre, x.Pais, x.Provincia, x.Dinerario, x.Retencion, x.Especie, x.IngresoACuenta,
                    x.IngresoRepercutido, x.GastosDeducibles,
                    new DatosTrabajador(x.AnioNacimiento, x.SituacionFamiliar, x.NifConyuge, x.Discapacidad, x.Contrato, x.MovilidadGeografica))).ToList();

        var mid = await ModeloIdAsync(cn, tx, ModelosFiscales.Obtener(trimestral), ct);
        var t = await cn.QuerySingleAsync<TrimestralFila>(new CommandDefinition("""
            SELECT SUM(c.importe) AS importe, COUNT(*) AS presentados FROM fiscal.declaracion d
            LEFT JOIN fiscal.declaracion_casilla c ON c.declaracion_id = d.id AND c.casilla = @casilla
            WHERE d.empresa_id = @empresaId AND d.modelo_id = @mid AND d.anio = @anio AND d.estado = 'PRESENTADA'
              AND d.numero_presentacion = (SELECT MAX(x.numero_presentacion) FROM fiscal.declaracion x
                    WHERE x.empresa_id = d.empresa_id AND x.modelo_id = d.modelo_id AND x.anio = d.anio AND x.periodo = d.periodo AND x.estado = 'PRESENTADA')
            """, new { empresaId, mid, anio = (short)p.Anio, casilla = trimestral == "111" ? "28" : "03" }, tx, cancellationToken: ct));
        CalculadorResumenRetenciones.Calcular(modelo.Codigo, filas, t.Importe, t.Presentados, d, trabajo);
    }

    private static async Task CalcularRetencionesAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, ModeloSoportado modelo,
        PeriodoFiscal p, DeclaracionCalculada d, CancellationToken ct)
    {
        if (modelo.Codigo is "190" or "180")
        {
            await CalcularResumenRetencionesAsync(cn, tx, empresaId, modelo, p, d, ct);
            return;
        }
        // Retenciones practicadas en facturas recibidas, por fecha contable
        var filas = (await cn.QueryAsync<PercepcionFila>(new CommandDefinition("""
            SELECT f.tercero_id, n.numero AS nif, t.nombre, tr.modelo_ingreso AS modelo, SUM(r.base) AS base, SUM(r.importe) AS retencion
            FROM fiscal.factura f
            JOIN fiscal.factura_retencion r ON r.factura_id = f.id
            JOIN cat.tipo_retencion_irpf tr ON tr.id = r.tipo_retencion_id
            JOIN conta.tercero t ON t.id = f.tercero_id
            JOIN core.nif n ON n.id = t.nif_id
            WHERE f.empresa_id = @empresaId AND f.libro = 'RECIBIDAS' AND f.estado = 'CONTABILIZADA'
              AND f.fecha_contable BETWEEN @inicio AND @fin AND tr.modelo_ingreso IN ('111', '115')
            GROUP BY 1, 2, 3, 4
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct))).AsList();

        var cuentas = await ConsultasContables.CuentasDeRolAsync(cn, tx, empresaId, "RETENCION_PRACTICADA", ct);
        decimal? contable = null;
        if (cuentas.Length > 0)
            contable = await cn.ExecuteScalarAsync<decimal>(new CommandDefinition("""
                -- Retenciones practicadas en el periodo: abonos (y cargos de facturas rectificativas); no cuenta el pago a Hacienda
                SELECT COALESCE(SUM(CASE WHEN s.origen = 'FACTURA' THEN a.haber - a.debe ELSE a.haber END), 0)
                FROM conta.apunte a JOIN conta.asiento s ON s.id = a.asiento_id
                WHERE a.cuenta_id = ANY(@cuentas) AND s.estado = 'CONTABILIZADO' AND s.origen NOT IN ('REGULARIZACION', 'CIERRE', 'APERTURA')
                  AND s.fecha BETWEEN @inicio AND @fin
                """, new { cuentas, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct));

        // Nóminas del periodo (rendimientos del trabajo del 111)
        var trabajo = (await cn.QueryAsync<TrabajoFila>(new CommandDefinition("""
            SELECT n.tercero_id, ni.numero AS nif, t.nombre, SUM(n.bruto) AS dinerario, SUM(n.retencion) AS retencion,
                   SUM(n.especie + CASE WHEN n.ingreso_repercutido THEN 0 ELSE n.ingreso_a_cuenta END) AS especie,
                   SUM(n.ingreso_a_cuenta) AS ingreso_a_cuenta
            FROM conta.nomina n JOIN conta.tercero t ON t.id = n.tercero_id JOIN core.nif ni ON ni.id = t.nif_id
            WHERE n.empresa_id = @empresaId AND n.estado = 'CONTABILIZADA' AND n.fecha BETWEEN @inicio AND @fin
            GROUP BY 1, 2, 3
            """, new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct)))
            .Select(x => new PercepcionTrabajo(x.TerceroId, x.Nif, x.Nombre, x.Dinerario, x.Retencion, x.Especie, x.IngresoACuenta)).ToList();

        CalculadorRetenciones.Calcular(modelo.Codigo,
            filas.Where(f => f.Modelo == modelo.Codigo).Select(f => new PercepcionRetenida(f.TerceroId, f.Nif, f.Nombre, f.Base, f.Retencion)).ToList(),
            contable, filas.Sum(f => f.Retencion) + trabajo.Sum(t => t.Retencion + t.IngresoACuenta), d, modelo.Codigo == "111" ? trabajo : null);
    }

    // ---------------------------------------------------------------- 349
    private sealed class Intracom349Fila
    {
        public long TerceroId { get; set; }
        public string Nif { get; set; } = "";
        public string Pais { get; set; } = "";
        public bool PaisUe { get; set; }
        public string Nombre { get; set; } = "";
        public string Clave { get; set; } = "";
        public decimal Base { get; set; }
        public DateOnly? FechaOriginal { get; set; }
    }

    private const string Select349 = """
        SELECT f.tercero_id, n.numero AS nif, pa.iso2 AS pais, pa.es_ue AS pais_ue, t.nombre,
               COALESCE(f.clave_operacion_349, CASE f.libro WHEN 'EMITIDAS' THEN 'E' ELSE 'A' END) AS clave,
               SUM(fi.base) AS base, r.fecha_contable AS fecha_original
        FROM fiscal.factura f
        JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
        JOIN conta.tercero t ON t.id = f.tercero_id
        JOIN core.nif n ON n.id = t.nif_id
        JOIN cat.pais pa ON pa.id = n.pais_id
        LEFT JOIN fiscal.factura r ON r.id = f.factura_rectificada_id
        WHERE f.empresa_id = @empresaId AND f.estado = 'CONTABILIZADA' AND f.fecha_contable BETWEEN @inicio AND @fin
          AND fi.tipo_operacion IN ('INTRACOM_ENTREGA', 'INTRACOM_ADQUISICION')
        """;

    private static async Task Calcular349Async(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, EmpresaFila empresa, PeriodoFiscal p,
        DeclaracionCalculada d, CancellationToken ct)
    {
        if (empresa.ImpuestoIndirecto != "IVA") throw new ReglaContableException("El 349 es de empresas que tributan por IVA.");
        var filas = (await cn.QueryAsync<Intracom349Fila>(new CommandDefinition(Select349 + " GROUP BY 1, 2, 3, 4, 5, 6, 8",
            new { empresaId, inicio = p.Inicio, fin = p.Fin }, tx, cancellationToken: ct))).AsList();

        // Las rectificativas de facturas de periodos anteriores van como rectificaciones de aquel periodo
        var mensual = !p.EsTrimestral;
        PeriodoFiscal Original(DateOnly f) => PeriodoFiscal.Resolver(f.Year, mensual ? f.Month.ToString("00") : $"{(f.Month - 1) / 3 + 1}T");
        var operaciones = filas.Where(f => f.FechaOriginal is null || f.FechaOriginal >= p.Inicio)
            .Select(f => new OperacionIntracom(f.TerceroId, f.Nif, f.Pais, f.PaisUe, f.Nombre, f.Clave, f.Base)).ToList();
        var rectificaciones = new List<Rectificacion349>();
        foreach (var g in filas.Where(f => f.FechaOriginal < p.Inicio).GroupBy(f => (f.TerceroId, f.Clave, Periodo: Original(f.FechaOriginal!.Value))))
        {
            var po = g.Key.Periodo;
            var anterior = (await cn.QueryAsync<Intracom349Fila>(new CommandDefinition(Select349 +
                    " AND f.tercero_id = @tercero AND COALESCE(f.clave_operacion_349, CASE f.libro WHEN 'EMITIDAS' THEN 'E' ELSE 'A' END) = @clave" +
                    " GROUP BY 1, 2, 3, 4, 5, 6, 8",
                new { empresaId, inicio = po.Inicio, fin = po.Fin, tercero = g.Key.TerceroId, clave = g.Key.Clave }, tx, cancellationToken: ct)))
                .Sum(x => x.Base);
            var f0 = g.First();
            rectificaciones.Add(new Rectificacion349(Calculador349.NifComunitario(f0.Nif, f0.Pais), f0.Nombre, g.Key.Clave, po.Anio, po.Codigo,
                anterior, anterior + g.Sum(x => x.Base)));
        }
        Calculador349.Calcular(operaciones, rectificaciones, d);
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
              -- No van al 347 las operaciones del 349 (intracomunitarias), las importaciones, las exportaciones ni las no sujetas
              -- por las reglas de localización (se realizan fuera del territorio)
              AND NOT EXISTS (SELECT 1 FROM fiscal.factura_iva x WHERE x.factura_id = f.id
                              AND x.tipo_operacion IN ('INTRACOM_ENTREGA','INTRACOM_ADQUISICION','IMPORTACION','EXPORTACION','NO_SUJETA_LOCAL','NO_SUJETA_OSS'))
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
            SELECT id, estado, numero_presentacion FROM fiscal.declaracion
            WHERE empresa_id = @empresaId AND modelo_id = @mid AND anio = @anio AND periodo = @periodo AND estado <> 'ANULADA'
            ORDER BY numero_presentacion DESC LIMIT 1
            """, new { empresaId, mid, anio = (short)p.Anio, periodo = p.Codigo }, tx, cancellationToken: ct));
        var tipo = (cmd.TipoDeclaracion ?? "ORDINARIA").Trim().ToUpperInvariant();
        cmd.TipoDeclaracion = tipo;
        if (existente is { Estado: "PRESENTADA" } && tipo == "ORDINARIA")
            throw new ReglaContableException($"El modelo {modelo.Codigo} {p.Anio} {p.Codigo} ya está presentado.",
                "Para corregirlo guarda una declaración complementaria o sustitutiva.");
        var hayPresentada = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM fiscal.declaracion WHERE empresa_id = @empresaId AND modelo_id = @mid AND anio = @anio
                           AND periodo = @periodo AND estado = 'PRESENTADA')
            """, new { empresaId, mid, anio = (short)p.Anio, periodo = p.Codigo }, tx, cancellationToken: ct));
        if (tipo != "ORDINARIA" && !hayPresentada)
            throw new ReglaContableException("Solo se puede complementar o sustituir una declaración presentada.");
        if (tipo == "ORDINARIA" && hayPresentada)
            throw new ReglaContableException("El periodo ya tiene una declaración presentada: guarda una complementaria o sustitutiva.");

        var d = await CalcularInternoAsync(cn, tx, empresaId, cmd, ct);
        var datos = JsonSerializer.Serialize(new { avisos = d.Avisos, detalle = d.Detalle }, Json);
        var opciones = JsonSerializer.Serialize(new { cmd.SolicitaDevolucion }, Json);

        long id;
        short numero = 1;
        if (existente is { Estado: "PRESENTADA" })
        {
            numero = (short)(existente.NumeroPresentacion + 1);
            id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO fiscal.declaracion (empresa_id, modelo_id, anio, periodo, fecha_inicio, fecha_fin, numero_presentacion, tipo_declaracion,
                                                estado, resultado, a_ingresar, a_devolver, a_compensar, datos, opciones, calculada_en, creado_por)
                VALUES (@empresaId, @mid, @anio, @periodo, @inicio, @fin, @numero, @tipo, 'CALCULADA', @resultado, @aIngresar, @aDevolver, @aCompensar,
                        @datos::jsonb, @opciones::jsonb, now(), @usuarioId)
                RETURNING id
                """, new { empresaId, mid, anio = (short)p.Anio, periodo = p.Codigo, inicio = d.FechaInicio, fin = d.FechaFin, numero, tipo,
                    resultado = d.Resultado, aIngresar = d.AIngresar, aDevolver = d.ADevolver, aCompensar = d.ACompensar, datos, opciones, usuarioId },
                tx, cancellationToken: ct));
        }
        else if (existente is not null)
        {
            numero = existente.NumeroPresentacion;
            await cn.ExecuteAsync(new CommandDefinition("UPDATE fiscal.declaracion SET tipo_declaracion = @tipo WHERE id = @id",
                new { tipo, id = existente.Id }, tx, cancellationToken: ct));
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

        if (d.EjercicioId is { } ejIs)
            await cn.ExecuteAsync(new CommandDefinition("UPDATE fiscal.declaracion SET ejercicio_id = @ejIs, fecha_inicio = @ini, fecha_fin = @fin WHERE id = @id",
                new { ejIs, ini = d.FechaInicio, fin = d.FechaFin, id }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.declaracion_casilla (declaracion_id, casilla, importe, texto, orden) VALUES (@id, @clave, @importe, @texto, @orden)
            """, d.Casillas.Select((c, i) => new { id, clave = c.Clave, importe = c.Importe, texto = c.Descripcion, orden = (short)i }),
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return VistaDe(id, modelo, "CALCULADA", d) with { NumeroPresentacion = numero, TipoDeclaracion = tipo };
    }

    public async Task<IReadOnlyList<DeclaracionResumenDto>> ListarAsync(long empresaId, int? anio, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<DeclaracionResumenDto>(new CommandDefinition("""
            SELECT d.id, m.codigo AS modelo, m.nombre, d.anio, d.periodo, d.numero_presentacion, d.tipo_declaracion, d.estado, d.resultado,
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
               SELECT d.id, m.codigo, m.administracion, m.nombre, d.anio, d.periodo, d.fecha_inicio, d.fecha_fin, d.numero_presentacion,
                      d.tipo_declaracion, d.estado,
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
        if (f.Codigo == "200") p = p with { Inicio = f.FechaInicio, Fin = f.FechaFin };   // periodo impositivo (puede ser partido)
        var (pd, ph) = PlazoPresentacion.Calcular(f.Codigo, p);
        return new DeclaracionVista(f.Id, f.Codigo, f.Nombre, f.Estado, f.Anio, f.Periodo, f.FechaInicio, f.FechaFin, casillas,
            f.Resultado ?? 0, f.AIngresar ?? 0, f.ADevolver ?? 0, f.ACompensar ?? 0, avisos, detalle, pd, ph, f.NumeroPresentacion, f.TipoDeclaracion);
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
                new CalcularModeloCmd { Modelo = f.Codigo, Anio = f.Anio, Periodo = f.Periodo, SolicitaDevolucion = solicita,
                                        TipoDeclaracion = f.TipoDeclaracion }, ct);
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
