using Contabilidad.Domain;
using Contabilidad.Domain.Cierre;
using Contabilidad.Domain.Inmovilizado;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IInmovilizadoRepositorio
{
    Task<IReadOnlyList<CategoriaInmovilizadoDto>> ListarCategoriasAsync(long empresaId, CancellationToken ct);
    Task<CategoriaInmovilizadoDto> CrearCategoriaAsync(long empresaId, CrearCategoriaCmd cmd, CancellationToken ct);

    Task<IReadOnlyList<InmovilizadoResumenDto>> ListarAsync(long empresaId, string? estado, CancellationToken ct);
    Task<FichaInmovilizadoDto> FichaAsync(long empresaId, long inmovilizadoId, CancellationToken ct);
    Task<FichaInmovilizadoDto> AltaAsync(long empresaId, long ejercicioId, long usuarioId, AltaInmovilizadoCmd cmd, CancellationToken ct);

    Task<AmortizacionVista> PrevisualizarAmortizacionAsync(long empresaId, long ejercicioId, DateOnly? hasta, CancellationToken ct);
    Task<AmortizacionVista> AmortizarAsync(long empresaId, long ejercicioId, long usuarioId, DateOnly? hasta, CancellationToken ct);
    Task<IReadOnlyList<EjecucionAmortizacionDto>> ListarEjecucionesAsync(long empresaId, long ejercicioId, CancellationToken ct);
    Task AnularAmortizacionAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct);

    Task<BajaRealizadaDto> BajaAsync(long empresaId, long ejercicioId, long usuarioId, long inmovilizadoId, BajaInmovilizadoCmd cmd, CancellationToken ct);
    Task AnularBajaAsync(long empresaId, long ejercicioId, long inmovilizadoId, CancellationToken ct);
}

/// <summary>
/// Inmovilizado: fichas, amortización lineal por días y bajas. La amortización de un periodo se contabiliza en un solo asiento
/// (gasto 68x contra amortización acumulada 28x, cada elemento en su canal) y queda registrada en inmov.amortizacion.
/// </summary>
public sealed class InmovilizadoRepositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : IInmovilizadoRepositorio
{
    private static readonly string[] Tipos = ["MATERIAL", "INTANGIBLE", "INVERSION_INMOBILIARIA"];

    private sealed class EjercicioFila
    {
        public long Id { get; set; }
        public string Codigo { get; set; } = "";
        public DateOnly FechaInicio { get; set; }
        public DateOnly FechaFin { get; set; }
        public string Estado { get; set; } = "";
    }

    /// <summary>Elemento con lo necesario para amortizarlo o darlo de baja.</summary>
    internal sealed class ElementoFila
    {
        public long Id { get; set; }
        public string Codigo { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public string Estado { get; set; } = "";
        public long CanalId { get; set; }
        public string Canal { get; set; } = "";
        public string TipoCategoria { get; set; } = "";
        public DateOnly FechaInicioAmortizacion { get; set; }
        public decimal ValorAdquisicion { get; set; }
        public decimal ValorResidual { get; set; }
        public string Metodo { get; set; } = "";
        public int? VidaUtilMeses { get; set; }
        public decimal AmortizacionPrevia { get; set; }
        public decimal Registrado { get; set; }
        public DateOnly? UltimoHasta { get; set; }
        public long? AsientoBajaId { get; set; }
        public long CuentaInmovilizadoId { get; set; }
        public long? CuentaAmortAcumuladaId { get; set; }
        public long? CuentaGastoAmortId { get; set; }

        public FichaAmortizacion Ficha => new(ValorAdquisicion, ValorResidual, FechaInicioAmortizacion, VidaUtilMeses, Metodo, AmortizacionPrevia);
        public decimal YaAmortizado => AmortizacionPrevia + Registrado;
    }

    private const string SelectElemento = """
        SELECT i.id, i.codigo, i.descripcion, i.estado, i.canal_id, k.codigo AS canal, c.tipo AS tipo_categoria,
               i.fecha_inicio_amortizacion, i.valor_adquisicion, i.valor_residual, i.metodo, i.vida_util_meses, i.amortizacion_previa,
               COALESCE((SELECT SUM(a.importe) FROM inmov.amortizacion a WHERE a.inmovilizado_id = i.id AND a.base_calculo = 'CONTABLE'), 0) AS registrado,
               (SELECT MAX(a.fecha_hasta) FROM inmov.amortizacion a WHERE a.inmovilizado_id = i.id AND a.base_calculo = 'CONTABLE') AS ultimo_hasta,
               i.asiento_baja_id, c.cuenta_inmovilizado_id, c.cuenta_amort_acumulada_id, c.cuenta_gasto_amort_id
        FROM inmov.inmovilizado i
        JOIN inmov.categoria c ON c.id = i.categoria_id
        JOIN canal.canal k ON k.id = i.canal_id
        """;

    // =====================================================================
    // Categorías
    // =====================================================================
    public async Task<IReadOnlyList<CategoriaInmovilizadoDto>> ListarCategoriasAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<CategoriaInmovilizadoDto>(new CommandDefinition(SqlCategorias + " WHERE c.empresa_id = @empresaId ORDER BY c.id",
            new { empresaId }, cancellationToken: ct))).AsList();
    }

    private const string SqlCategorias = """
        SELECT c.id, c.nombre, c.tipo, c.cuenta_inmovilizado_id, ci.codigo AS cuenta_inmovilizado, c.cuenta_amort_acumulada_id,
               ca.codigo AS cuenta_amort_acumulada, c.cuenta_gasto_amort_id, cg.codigo AS cuenta_gasto_amort, c.metodo_defecto, c.vida_util_meses_defecto
        FROM inmov.categoria c
        LEFT JOIN conta.cuenta ci ON ci.id = c.cuenta_inmovilizado_id
        LEFT JOIN conta.cuenta ca ON ca.id = c.cuenta_amort_acumulada_id
        LEFT JOIN conta.cuenta cg ON cg.id = c.cuenta_gasto_amort_id
        """;

    public async Task<CategoriaInmovilizadoDto> CrearCategoriaAsync(long empresaId, CrearCategoriaCmd cmd, CancellationToken ct)
    {
        var nombre = (cmd.Nombre ?? "").Trim();
        if (nombre.Length is 0 or > 150) throw new ReglaContableException("El nombre es obligatorio (máximo 150 caracteres).");
        if (!Tipos.Contains(cmd.Tipo)) throw new ReglaContableException("Tipo no válido: MATERIAL, INTANGIBLE o INVERSION_INMOBILIARIA.");
        if (!CalculadorAmortizacion.Metodos.Contains(cmd.MetodoDefecto)) throw new ReglaContableException("Método no válido: LINEAL o NO_AMORTIZA.");
        var amortiza = cmd.MetodoDefecto == "LINEAL";
        if (amortiza && (cmd.CuentaAmortAcumuladaId is null || cmd.CuentaGastoAmortId is null))
            throw new ReglaContableException("Una categoría que se amortiza necesita cuenta de amortización acumulada (28x) y de gasto (68x).");
        if (cmd.VidaUtilMesesDefecto is <= 0 or > 1200) throw new ReglaContableException("La vida útil debe estar entre 1 y 1.200 meses.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await ComprobarCuentaAsync(cn, null, empresaId, cmd.CuentaInmovilizadoId, ["20", "21", "22"], "de inmovilizado (20, 21 o 22)", ct);
        if (cmd.CuentaAmortAcumuladaId is { } a) await ComprobarCuentaAsync(cn, null, empresaId, a, ["28"], "de amortización acumulada (28)", ct);
        if (cmd.CuentaGastoAmortId is { } g) await ComprobarCuentaAsync(cn, null, empresaId, g, ["68"], "de gasto por amortización (68)", ct);

        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO inmov.categoria (empresa_id, nombre, tipo, cuenta_inmovilizado_id, cuenta_amort_acumulada_id, cuenta_gasto_amort_id,
                                         metodo_defecto, vida_util_meses_defecto)
            VALUES (@empresaId, @nombre, @tipo, @ci, @ca, @cg, @metodo, @meses) RETURNING id
            """, new { empresaId, nombre, tipo = cmd.Tipo, ci = cmd.CuentaInmovilizadoId, ca = cmd.CuentaAmortAcumuladaId, cg = cmd.CuentaGastoAmortId,
                       metodo = cmd.MetodoDefecto, meses = cmd.VidaUtilMesesDefecto }, cancellationToken: ct));
        return await cn.QuerySingleAsync<CategoriaInmovilizadoDto>(new CommandDefinition(SqlCategorias + " WHERE c.id = @id", new { id }, cancellationToken: ct));
    }

    private static async Task ComprobarCuentaAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long cuentaId, string[] prefijos,
        string que, CancellationToken ct)
    {
        var codigo = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT codigo FROM conta.cuenta WHERE id = @cuentaId AND empresa_id = @empresaId AND es_imputable AND activa",
            new { cuentaId, empresaId }, tx, cancellationToken: ct));
        if (codigo is null || !prefijos.Any(p => codigo.StartsWith(p, StringComparison.Ordinal)))
            throw new ReglaContableException($"La cuenta {que} no es válida.");
    }

    // =====================================================================
    // Fichas
    // =====================================================================
    private const string SqlResumen = """
        SELECT i.id, i.codigo, i.descripcion, c.nombre AS categoria, k.codigo AS canal, i.fecha_adquisicion, i.fecha_inicio_amortizacion,
               i.valor_adquisicion, i.valor_residual, i.metodo, i.vida_util_meses, i.amortizacion_previa, i.estado, i.fecha_baja,
               i.amortizacion_previa + COALESCE((SELECT SUM(a.importe) FROM inmov.amortizacion a
                                                 WHERE a.inmovilizado_id = i.id AND a.base_calculo = 'CONTABLE'), 0) AS amortizado,
               (SELECT MAX(a.fecha_hasta) FROM inmov.amortizacion a WHERE a.inmovilizado_id = i.id AND a.base_calculo = 'CONTABLE') AS amortizado_hasta
        FROM inmov.inmovilizado i
        JOIN inmov.categoria c ON c.id = i.categoria_id
        JOIN canal.canal k ON k.id = i.canal_id
        """;

    public async Task<IReadOnlyList<InmovilizadoResumenDto>> ListarAsync(long empresaId, string? estado, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<InmovilizadoResumenDto>(new CommandDefinition(SqlResumen + """
             WHERE i.empresa_id = @empresaId AND (@estado::text IS NULL OR i.estado = @estado)
            ORDER BY i.codigo
            """, new { empresaId, estado }, cancellationToken: ct))).AsList();
    }

    public async Task<FichaInmovilizadoDto> FichaAsync(long empresaId, long inmovilizadoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return await FichaAsync(cn, empresaId, inmovilizadoId, ct);
    }

    private static async Task<FichaInmovilizadoDto> FichaAsync(NpgsqlConnection cn, long empresaId, long inmovilizadoId, CancellationToken ct)
    {
        var r = await cn.QuerySingleOrDefaultAsync<InmovilizadoResumenDto>(new CommandDefinition(SqlResumen + " WHERE i.id = @inmovilizadoId AND i.empresa_id = @empresaId",
            new { inmovilizadoId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("El elemento de inmovilizado no existe.");

        // Los tramos del cuadro siguen los ejercicios de la empresa (mes en que empiezan)
        var mes = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT EXTRACT(MONTH FROM fecha_inicio)::int FROM core.ejercicio WHERE empresa_id = @empresaId ORDER BY fecha_inicio DESC LIMIT 1",
            new { empresaId }, cancellationToken: ct)) ?? 1;
        var ficha = new FichaAmortizacion(r.ValorAdquisicion, r.ValorResidual, r.FechaInicioAmortizacion, r.VidaUtilMeses, r.Metodo, r.AmortizacionPrevia);

        var movimientos = (await cn.QueryAsync<MovimientoAmortizacionDto>(new CommandDefinition("""
            SELECT a.fecha_desde AS desde, a.fecha_hasta AS hasta, a.importe, a.acumulada, a.asiento_id, s.numero AS asiento_numero, e.codigo AS ejercicio
            FROM inmov.amortizacion a
            LEFT JOIN conta.asiento s ON s.id = a.asiento_id
            LEFT JOIN core.ejercicio e ON e.id = a.ejercicio_id
            WHERE a.inmovilizado_id = @inmovilizadoId AND a.base_calculo = 'CONTABLE'
            ORDER BY a.fecha_hasta
            """, new { inmovilizadoId }, cancellationToken: ct))).AsList();
        return new FichaInmovilizadoDto(r, ficha.Fin, CalculadorAmortizacion.Cuadro(ficha, mes), movimientos);
    }

    public async Task<FichaInmovilizadoDto> AltaAsync(long empresaId, long ejercicioId, long usuarioId, AltaInmovilizadoCmd cmd, CancellationToken ct)
    {
        var descripcion = (cmd.Descripcion ?? "").Trim();
        if (descripcion.Length is 0 or > 250) throw new ReglaContableException("La descripción es obligatoria (máximo 250 caracteres).");
        if (cmd.ValorAdquisicion <= 0) throw new ReglaContableException("El valor de adquisición debe ser mayor que cero.");
        if (cmd.ValorResidual < 0 || cmd.ValorResidual >= cmd.ValorAdquisicion)
            throw new ReglaContableException("El valor residual debe estar entre cero y el valor de adquisición.");
        var inicio = cmd.FechaInicioAmortizacion ?? cmd.FechaAdquisicion;
        if (inicio < cmd.FechaAdquisicion) throw new ReglaContableException("La amortización no puede empezar antes de la adquisición.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var cat = await cn.QuerySingleOrDefaultAsync<CategoriaInmovilizadoDto>(new CommandDefinition(SqlCategorias + " WHERE c.id = @id AND c.empresa_id = @empresaId",
            new { id = cmd.CategoriaId, empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La categoría no existe en la empresa.");
        if (cat.CuentaInmovilizadoId is null) throw new ReglaContableException("La categoría no tiene cuenta de inmovilizado.");
        var metodo = string.IsNullOrWhiteSpace(cmd.Metodo) ? cat.MetodoDefecto : cmd.Metodo.Trim().ToUpperInvariant();
        if (!CalculadorAmortizacion.Metodos.Contains(metodo)) throw new ReglaContableException("Método no válido: LINEAL o NO_AMORTIZA.");
        var meses = cmd.VidaUtilMeses ?? cat.VidaUtilMesesDefecto;
        if (metodo == "LINEAL")
        {
            if (meses is null or <= 0 or > 1200) throw new ReglaContableException("Indica la vida útil en meses (entre 1 y 1.200).");
            if (cat.CuentaAmortAcumuladaId is null || cat.CuentaGastoAmortId is null)
                throw new ReglaContableException("La categoría no tiene cuentas de amortización: complétala o elige «no se amortiza».");
        }
        var ficha = new FichaAmortizacion(cmd.ValorAdquisicion, cmd.ValorResidual, inicio, meses, metodo);
        if (cmd.AmortizacionPrevia < 0 || cmd.AmortizacionPrevia > ficha.BaseAmortizable)
            throw new ReglaContableException("La amortización previa no puede superar la base amortizable.");

        var canalOk = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM canal.canal WHERE id = @canalId AND empresa_id = @empresaId AND activo)",
            new { canalId = cmd.CanalId, empresaId }, tx, cancellationToken: ct));
        if (!canalOk) throw new ReglaContableException("El canal no pertenece a la empresa.");
        if (cmd.FacturaId is { } fid && !await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM fiscal.factura WHERE id = @fid AND empresa_id = @empresaId)", new { fid, empresaId }, tx, cancellationToken: ct)))
            throw new ReglaContableException("La factura no pertenece a la empresa.");
        if (cmd.ProveedorId is { } pid && !await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM conta.tercero WHERE id = @pid AND empresa_id = @empresaId)", new { pid, empresaId }, tx, cancellationToken: ct)))
            throw new ReglaContableException("El proveedor no pertenece a la empresa.");

        // Código: el indicado o el siguiente INM-nnnn
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"inmovilizado|{empresaId}" }, tx, cancellationToken: ct));
        var codigo = (cmd.Codigo ?? "").Trim();
        if (codigo.Length == 0)
        {
            var n = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COALESCE(MAX(substring(codigo FROM 5)::int), 0) + 1 FROM inmov.inmovilizado
                WHERE empresa_id = @empresaId AND codigo ~ '^INM-[0-9]+$'
                """, new { empresaId }, tx, cancellationToken: ct));
            codigo = $"INM-{n:0000}";
        }
        if (codigo.Length > 20) throw new ReglaContableException("El código no puede superar los 20 caracteres.");

        long? asientoAlta = null;
        if (cmd.CuentaContrapartidaId is { } contra)
        {
            await ComprobarCuentaAsync(cn, tx, empresaId, contra, ["1", "4", "5"], "de contrapartida (proveedor de inmovilizado, banco…)", ct);
            var concepto = $"Alta de inmovilizado {codigo} {descripcion}";
            if (concepto.Length > 250) concepto = concepto[..250];
            var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, new CrearAsientoCmd
            {
                Fecha = cmd.FechaAdquisicion, DiarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct), Concepto = concepto,
                Apuntes =
                [
                    new() { CuentaId = cat.CuentaInmovilizadoId.Value, CanalId = cmd.CanalId, Debe = cmd.ValorAdquisicion, Concepto = concepto },
                    new() { CuentaId = contra, CanalId = cmd.CanalId, Haber = cmd.ValorAdquisicion, Concepto = concepto }
                ]
            }, "MANUAL", null, codigo, ct);
            asientoAlta = a.Id;
        }

        long id;
        try
        {
            id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO inmov.inmovilizado (empresa_id, categoria_id, canal_id, codigo, descripcion, factura_id, proveedor_id, fecha_adquisicion,
                    fecha_inicio_amortizacion, valor_adquisicion, valor_residual, metodo, vida_util_meses, amortizacion_previa, asiento_alta_id)
                VALUES (@empresaId, @categoriaId, @canalId, @codigo, @descripcion, @facturaId, @proveedorId, @adquisicion, @inicio, @valor, @residual,
                        @metodo, @meses, @previa, @asientoAlta)
                RETURNING id
                """, new
            {
                empresaId, categoriaId = cmd.CategoriaId, canalId = cmd.CanalId, codigo, descripcion, facturaId = cmd.FacturaId, proveedorId = cmd.ProveedorId,
                adquisicion = cmd.FechaAdquisicion, inicio, valor = cmd.ValorAdquisicion, residual = cmd.ValorResidual, metodo,
                meses = metodo == "LINEAL" ? meses : null, previa = cmd.AmortizacionPrevia, asientoAlta
            }, tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaContableException($"Ya existe un elemento con el código {codigo}.");
        }
        await tx.CommitAsync(ct);
        return await FichaAsync(cn, empresaId, id, ct);
    }

    // =====================================================================
    // Amortización
    // =====================================================================
    public async Task<AmortizacionVista> PrevisualizarAmortizacionAsync(long empresaId, long ejercicioId, DateOnly? hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await CalcularAmortizacionAsync(cn, null, empresaId, ejercicioId, hasta, ct)).Vista;
    }

    public async Task<AmortizacionVista> AmortizarAsync(long empresaId, long ejercicioId, long usuarioId, DateOnly? hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"inmovilizado|{empresaId}" }, tx, cancellationToken: ct));

        var c = await CalcularAmortizacionAsync(cn, tx, empresaId, ejercicioId, hasta, ct);
        if (c.Lineas.Count == 0) throw new ReglaContableException($"No hay amortización pendiente hasta el {c.Vista.Hasta:dd/MM/yyyy}.");

        var apuntes = c.Lineas.SelectMany(l => new[]
        {
            new ApunteCmd { CuentaId = l.Elemento.CuentaGastoAmortId!.Value, CanalId = l.Elemento.CanalId, Debe = l.Importe, Concepto = $"{c.Vista.Concepto} · {l.Elemento.Codigo}" },
            new ApunteCmd { CuentaId = l.Elemento.CuentaAmortAcumuladaId!.Value, CanalId = l.Elemento.CanalId, Haber = l.Importe, Concepto = $"{c.Vista.Concepto} · {l.Elemento.Codigo}" }
        }).ToList();
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
            new CrearAsientoCmd { Fecha = c.Vista.Hasta, DiarioId = c.Vista.DiarioId, Concepto = c.Vista.Concepto, Apuntes = apuntes },
            "AMORTIZACION", null, $"AMORT {c.Vista.Hasta:yyyy-MM-dd}", ct);

        await RegistrarAsync(cn, tx, ejercicioId, a.Id, c.Vista.Hasta, c.Lineas.Select(l => (l.Elemento, l.Importe)), ct);
        await tx.CommitAsync(ct);
        return c.Vista with { AsientoId = a.Id, AsientoNumero = a.Numero };
    }

    private sealed record LineaCalculada(ElementoFila Elemento, decimal Importe);
    private sealed record CalculoAmortizacion(AmortizacionVista Vista, IReadOnlyList<LineaCalculada> Lineas);

    private static async Task<CalculoAmortizacion> CalcularAmortizacionAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId,
        DateOnly? hastaPedido, CancellationToken ct)
    {
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        var hasta = hastaPedido ?? ej.FechaFin;
        if (hasta < ej.FechaInicio || hasta > ej.FechaFin)
            throw new ReglaContableException($"La fecha debe estar dentro del ejercicio ({ej.FechaInicio:dd/MM/yyyy} – {ej.FechaFin:dd/MM/yyyy}).");

        var elementos = await PendientesAsync(cn, tx, empresaId, hasta, ct);
        var avisos = new List<string>();
        var sinCuentas = elementos.Where(e => e.Elemento.CuentaGastoAmortId is null || e.Elemento.CuentaAmortAcumuladaId is null).Select(e => e.Elemento.Codigo).ToList();
        if (sinCuentas.Count > 0)
            throw new ReglaContableException($"Elementos cuya categoría no tiene cuentas de amortización: {string.Join(", ", sinCuentas)}.");
        var atrasados = elementos.Where(e => (e.Elemento.UltimoHasta ?? e.Elemento.FechaInicioAmortizacion.AddDays(-1)) < ej.FechaInicio.AddDays(-1))
                                 .Select(e => e.Elemento.Codigo).ToList();
        if (atrasados.Count > 0)
            avisos.Add($"Incluye amortización de ejercicios anteriores que no se había contabilizado ({string.Join(", ", atrasados)}).");

        var diarioId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM conta.diario WHERE empresa_id = @empresaId AND activo AND tipo = 'AMORTIZACION' ORDER BY id LIMIT 1",
            new { empresaId }, tx, cancellationToken: ct)) ?? await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct);
        var concepto = $"Amortización del inmovilizado hasta {hasta:dd/MM/yyyy}";
        var lineas = elementos.Select(e => new LineaAmortizacionDto(e.Elemento.Id, e.Elemento.Codigo, e.Elemento.Descripcion, e.Elemento.Canal, e.Importe,
            e.Elemento.YaAmortizado + e.Importe, e.Elemento.ValorAdquisicion - e.Elemento.YaAmortizado - e.Importe)).ToList();
        return new CalculoAmortizacion(new AmortizacionVista(hasta, diarioId, concepto, lineas, lineas.Sum(l => l.Importe), avisos, null, null),
            elementos.Select(e => new LineaCalculada(e.Elemento, e.Importe)).ToList());
    }

    /// <summary>Elementos en alta con amortización pendiente hasta la fecha (también lo usa el cierre del ejercicio para avisar).</summary>
    internal static async Task<List<(ElementoFila Elemento, decimal Importe)>> PendientesAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId,
        DateOnly hasta, CancellationToken ct)
    {
        var filas = await cn.QueryAsync<ElementoFila>(new CommandDefinition(SelectElemento + """
             WHERE i.empresa_id = @empresaId AND i.estado = 'ALTA' AND i.metodo = 'LINEAL' AND i.fecha_inicio_amortizacion <= @hasta
            ORDER BY i.codigo
            """, new { empresaId, hasta }, tx, cancellationToken: ct));
        return filas.Select(e => (e, CalculadorAmortizacion.Pendiente(e.Ficha, hasta, e.YaAmortizado))).Where(x => x.Item2 > 0).ToList();
    }

    private static Task RegistrarAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long ejercicioId, long asientoId, DateOnly hasta,
        IEnumerable<(ElementoFila Elemento, decimal Importe)> lineas, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO inmov.amortizacion (inmovilizado_id, ejercicio_id, periodo_id, base_calculo, importe, acumulada, asiento_id, fecha_desde, fecha_hasta)
            VALUES (@id, @ejercicioId,
                    (SELECT id FROM core.periodo WHERE ejercicio_id = @ejercicioId AND tipo = 'NORMAL' AND @hasta BETWEEN fecha_inicio AND fecha_fin),
                    'CONTABLE', @importe, @acumulada, @asientoId, @desde, @hasta)
            """, lineas.Select(l => new
        {
            id = l.Elemento.Id, ejercicioId, hasta, importe = l.Importe, acumulada = l.Elemento.YaAmortizado + l.Importe, asientoId,
            desde = l.Elemento.UltimoHasta?.AddDays(1) ?? l.Elemento.FechaInicioAmortizacion
        }), tx, cancellationToken: ct));

    public async Task<IReadOnlyList<EjecucionAmortizacionDto>> ListarEjecucionesAsync(long empresaId, long ejercicioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<EjecucionAmortizacionDto>(new CommandDefinition("""
            SELECT s.id AS asiento_id, s.numero, s.fecha, s.concepto, count(a.id)::int AS elementos, COALESCE(SUM(a.importe), 0) AS total
            FROM conta.asiento s LEFT JOIN inmov.amortizacion a ON a.asiento_id = s.id
            WHERE s.empresa_id = @empresaId AND s.ejercicio_id = @ejercicioId AND s.origen = 'AMORTIZACION' AND s.estado = 'CONTABILIZADO'
            GROUP BY s.id ORDER BY s.fecha, s.id
            """, new { empresaId, ejercicioId }, cancellationToken: ct))).AsList();
    }

    public async Task AnularAmortizacionAsync(long empresaId, long ejercicioId, long asientoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"inmovilizado|{empresaId}" }, tx, cancellationToken: ct));
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");

        var existe = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM conta.asiento WHERE id = @asientoId AND empresa_id = @empresaId AND ejercicio_id = @ejercicioId
                           AND origen = 'AMORTIZACION' AND estado = 'CONTABILIZADO')
            """, new { asientoId, empresaId, ejercicioId }, tx, cancellationToken: ct));
        if (!existe) throw new ReglaContableException("No hay una amortización contabilizada con ese asiento en este ejercicio.");

        // Solo la última de cada elemento: si hay amortizaciones o bajas posteriores, se anulan antes
        var posteriores = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM inmov.amortizacion yo
            JOIN inmov.inmovilizado i ON i.id = yo.inmovilizado_id
            WHERE yo.asiento_id = @asientoId
              AND (i.estado <> 'ALTA' OR EXISTS (SELECT 1 FROM inmov.amortizacion a WHERE a.inmovilizado_id = yo.inmovilizado_id AND a.fecha_hasta > yo.fecha_hasta))
            """, new { asientoId }, tx, cancellationToken: ct));
        if (posteriores > 0)
            throw new ReglaContableException("Hay amortizaciones o bajas posteriores de esos elementos: anúlalas antes, empezando por la más reciente.");

        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM inmov.amortizacion WHERE asiento_id = @asientoId", new { asientoId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'ANULADO' WHERE id = @asientoId", new { asientoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // =====================================================================
    // Bajas y ventas
    // =====================================================================
    public async Task<BajaRealizadaDto> BajaAsync(long empresaId, long ejercicioId, long usuarioId, long inmovilizadoId, BajaInmovilizadoCmd cmd,
        CancellationToken ct)
    {
        var tipo = (cmd.Tipo ?? "").Trim().ToUpperInvariant();
        if (tipo is not ("BAJA" or "VENTA")) throw new ReglaContableException("Tipo no válido: BAJA o VENTA.");
        if (tipo == "VENTA" && cmd.ImporteVenta <= 0) throw new ReglaContableException("Indica el precio de venta.");
        if (tipo == "BAJA" && cmd.ImporteVenta != 0) throw new ReglaContableException("Una baja sin venta no lleva importe.");

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@clave))",
            new { clave = $"inmovilizado|{empresaId}" }, tx, cancellationToken: ct));

        var e = await ElementoAsync(cn, tx, empresaId, inmovilizadoId, ct);
        if (e.Estado != "ALTA") throw new ReglaContableException("El elemento ya está dado de baja.");
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (cmd.Fecha < ej.FechaInicio || cmd.Fecha > ej.FechaFin) throw new ReglaContableException("La fecha de baja debe estar dentro del ejercicio seleccionado.");
        if (e.UltimoHasta is { } u && cmd.Fecha < u)
            throw new ReglaContableException($"El elemento ya está amortizado hasta el {u:dd/MM/yyyy}: la baja no puede ser anterior.");
        if (cmd.CuentaCobroId is { } cobro) await ComprobarCuentaAsync(cn, tx, empresaId, cobro, ["4", "5"], "de cobro (543, 57…)", ct);

        // Resultado: 671/771 (material), 670/770 (intangible), 672/772 (inversiones inmobiliarias)
        var (pp, bb) = e.TipoCategoria switch { "INTANGIBLE" => ("6700", "7700"), "INVERSION_INMOBILIARIA" => ("6720", "7720"), _ => ("6710", "7710") };
        var perdidas = await CuentaPgcAsync(cn, tx, empresaId, pp, ct);
        var beneficios = await CuentaPgcAsync(cn, tx, empresaId, bb, ct);

        var pendiente = CalculadorAmortizacion.Pendiente(e.Ficha, cmd.Fecha, e.YaAmortizado);
        var (apuntes, resultado) = GeneradorBaja.Generar(e.ValorAdquisicion, e.YaAmortizado, pendiente, cmd.ImporteVenta,
            new CuentasInmovilizado(e.CuentaInmovilizadoId, e.CuentaAmortAcumuladaId, e.CuentaGastoAmortId),
            cmd.CuentaCobroId, perdidas, beneficios, e.CanalId);

        var concepto = $"{(tipo == "VENTA" ? "Venta" : "Baja")} de inmovilizado {e.Codigo} {e.Descripcion}";
        if (concepto.Length > 250) concepto = concepto[..250];
        var a = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId, new CrearAsientoCmd
        {
            Fecha = cmd.Fecha, DiarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct), Concepto = concepto,
            Apuntes = ConsultasContables.AApuntes(apuntes, concepto)
        }, "MANUAL", null, $"BAJA {e.Codigo}", ct);

        if (pendiente > 0) await RegistrarAsync(cn, tx, ejercicioId, a.Id, cmd.Fecha, [(e, pendiente)], ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE inmov.inmovilizado SET estado = @estado, fecha_baja = @fecha, asiento_baja_id = @asientoId, importe_venta = @importe WHERE id = @id
            """, new { estado = tipo == "VENTA" ? "VENDIDO" : "BAJA", fecha = cmd.Fecha, asientoId = a.Id, importe = tipo == "VENTA" ? cmd.ImporteVenta : (decimal?)null,
                       id = e.Id }, tx, cancellationToken: ct));

        var cuentas = (await cn.QueryAsync<(long Id, string Codigo, string Nombre)>(new CommandDefinition(
            "SELECT id, codigo, nombre FROM conta.cuenta WHERE id = ANY(@ids)", new { ids = apuntes.Select(x => x.CuentaId).Distinct().ToArray() },
            tx, cancellationToken: ct))).ToDictionary(x => x.Id);
        await tx.CommitAsync(ct);
        return new BajaRealizadaDto(a.Id, a.Numero, resultado, apuntes.Select(x => new ApuntePrevioDto(x.CuentaId, cuentas[x.CuentaId].Codigo,
            cuentas[x.CuentaId].Nombre, x.CanalId, x.Debe, x.Haber, concepto)).ToList());
    }

    public async Task AnularBajaAsync(long empresaId, long ejercicioId, long inmovilizadoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var e = await ElementoAsync(cn, tx, empresaId, inmovilizadoId, ct);
        if (e.Estado == "ALTA" || e.AsientoBajaId is null) throw new ReglaContableException("El elemento no está dado de baja.");
        var ej = await EjercicioAsync(cn, tx, empresaId, ejercicioId, ct);
        if (ej.Estado != "ABIERTO") throw new ReglaContableException("El ejercicio está cerrado.");
        var deEsteEjercicio = await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM conta.asiento WHERE id = @id AND ejercicio_id = @ejercicioId)", new { id = e.AsientoBajaId, ejercicioId }, tx, cancellationToken: ct));
        if (!deEsteEjercicio) throw new ReglaContableException("La baja está contabilizada en otro ejercicio: selecciónalo para anularla.");
        if (await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (SELECT 1 FROM inmov.bien_inversion_regularizacion r JOIN inmov.bien_inversion b ON b.id = r.bien_inversion_id
                               WHERE b.inmovilizado_id = @id AND r.tipo = 'TRANSMISION')
                """, new { id = e.Id }, tx, cancellationToken: ct)))
            throw new ReglaContableException("La venta tiene contabilizada la regularización del bien de inversión: anúlala antes (Bienes de inversión).");

        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM inmov.amortizacion WHERE asiento_id = @id", new { id = e.AsientoBajaId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("UPDATE conta.asiento SET estado = 'ANULADO' WHERE id = @id", new { id = e.AsientoBajaId }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE inmov.inmovilizado SET estado = 'ALTA', fecha_baja = NULL, asiento_baja_id = NULL, importe_venta = NULL WHERE id = @id
            """, new { id = e.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // =====================================================================
    // Consultas comunes
    // =====================================================================
    private static async Task<EjercicioFila> EjercicioAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, long ejercicioId, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<EjercicioFila>(new CommandDefinition(
               "SELECT id, codigo, fecha_inicio, fecha_fin, estado FROM core.ejercicio WHERE id = @ejercicioId AND empresa_id = @empresaId",
               new { ejercicioId, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El ejercicio no existe en la empresa.");

    private static async Task<ElementoFila> ElementoAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, long id, CancellationToken ct)
        => await cn.QuerySingleOrDefaultAsync<ElementoFila>(new CommandDefinition(SelectElemento + " WHERE i.id = @id AND i.empresa_id = @empresaId FOR UPDATE OF i",
               new { id, empresaId }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException("El elemento de inmovilizado no existe.");

    /// <summary>Subcuenta del PGC (la crea con el nombre del plan modelo si la empresa no la tiene).</summary>
    private static async Task<long> CuentaPgcAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, string codigo, CancellationToken ct)
        => await cn.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT inmov.asegurar_cuenta(@empresaId, @codigo)",
               new { empresaId, codigo }, tx, cancellationToken: ct))
           ?? throw new ReglaContableException($"La empresa no tiene la cuenta {codigo} y no se ha podido crear.");
}
