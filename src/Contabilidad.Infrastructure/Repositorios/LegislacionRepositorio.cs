using System.Text.Json.Nodes;
using Contabilidad.Domain;
using Contabilidad.Domain.Balances;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

/// <summary>
/// Legislación por fechas: parámetros de cálculo, versiones de los formularios de los modelos y diseños de fichero. El contexto se
/// guarda en memoria (se recarga al minuto o al cambiar algo) y se fija en cada petición para que los cálculos usen lo vigente.
/// </summary>
public interface ILegislacionRepositorio
{
    Task<ContextoLegislacion> ContextoAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroFiscalDto>> ParametrosAsync(CancellationToken ct);
    Task GuardarParametroAsync(GuardarParametroCmd cmd, CancellationToken ct);
    Task BorrarParametroAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<VersionFormularioDto>> FormulariosAsync(CancellationToken ct);
    /// <summary>JSON del formulario vigente en el año (la versión guardada o el del programa), para empezar una versión nueva.</summary>
    string DefinicionFormulario(string modelo, int anio);
    Task GuardarFormularioAsync(GuardarFormularioCmd cmd, CancellationToken ct);
    Task BorrarFormularioAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<DisenoResumenDto>> DisenosAsync(CancellationToken ct);
    /// <summary>JSON del diseño de fichero vigente en el año.</summary>
    string DisenoJson(string modelo, int anio);
    Task GuardarDisenoAsync(GuardarDisenoCmd cmd, CancellationToken ct);
    Task BorrarDisenoAsync(long id, CancellationToken ct);
    /// <summary>Balance o PyG del 200 vigente en el año, con las cuentas de cada partida.</summary>
    EstadoLegislacionDto Estado(string codigo, int anio);
    Task<IReadOnlyList<VersionFormularioDto>> EstadosAsync(CancellationToken ct);
    Task GuardarEstadoAsync(GuardarEstadoCmd cmd, CancellationToken ct);
}

public sealed class LegislacionRepositorio(IConexionFactory conexiones) : ILegislacionRepositorio
{
    private static readonly object Cerrojo = new();
    private static (ContextoLegislacion Contexto, DateTime Hasta)? cache;
    private static void Invalidar() { lock (Cerrojo) cache = null; }

    private static readonly string[] ModelosFormulario = ["303", "420", "111", "115", "130", "131", "309", "347", "349", "390", "425", "190", "180", "200", "202"];

    private sealed class FilaFormulario { public string Modelo { get; set; } = ""; public int DesdeAnio { get; set; } public string Definicion { get; set; } = ""; }
    private sealed class FilaDiseno { public long Id { get; set; } public string Modelo { get; set; } = ""; public DateOnly Desde { get; set; } public DateOnly? Hasta { get; set; } public string Definicion { get; set; } = ""; public string? Comentario { get; set; } }

    public async Task<ContextoLegislacion> ContextoAsync(CancellationToken ct)
    {
        lock (Cerrojo) if (cache is { } c && c.Hasta > DateTime.UtcNow) return c.Contexto;
        ContextoLegislacion contexto;
        try { contexto = await CargarAsync(ct); }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            contexto = new ContextoLegislacion([], [], []);      // sin el script 21: valores del programa
        }
        lock (Cerrojo) cache = (contexto, DateTime.UtcNow.AddMinutes(1));
        return contexto;
    }

    private async Task<ContextoLegislacion> CargarAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var parametros = (await cn.QueryAsync<ParametroFiscalDto>(new CommandDefinition(
            "SELECT id, clave, desde, valor, descripcion FROM cat.parametro_fiscal", cancellationToken: ct)))
            .Select(p => new ParametroFiscal(p.Clave, p.Desde, p.Valor, p.Descripcion)).ToList();
        var formularios = (await cn.QueryAsync<FilaFormulario>(new CommandDefinition(
            "SELECT modelo, desde_anio, definicion::text AS definicion FROM fiscal.formulario_version", cancellationToken: ct)))
            .Select(f => new VersionFormulario(f.Modelo, f.DesdeAnio, f.Definicion)).ToList();
        var disenos = new List<DisenoAeat>();
        foreach (var d in await cn.QueryAsync<FilaDiseno>(new CommandDefinition(
                     "SELECT id, modelo, desde, hasta, definicion::text AS definicion FROM fiscal.diseno_version", cancellationToken: ct)))
        {
            try { disenos.Add(Legislacion.ImportarDiseno(d.Modelo, d.Desde, d.Hasta, d.Definicion)); }
            catch (ReglaContableException) { /* uno dañado no impide usar el resto: se ve al editarlo */ }
        }
        return new ContextoLegislacion(parametros, formularios, disenos);
    }

    // ------------------------------------------------------------------------------------------------ parámetros
    public async Task<IReadOnlyList<ParametroFiscalDto>> ParametrosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<ParametroFiscalDto>(new CommandDefinition(
            "SELECT id, clave, desde, valor, descripcion FROM cat.parametro_fiscal ORDER BY clave, desde", cancellationToken: ct))).AsList();
    }

    public async Task GuardarParametroAsync(GuardarParametroCmd cmd, CancellationToken ct)
    {
        var clave = (cmd.Clave ?? "").Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(clave, "^[A-Z0-9_]{2,40}$"))
            throw new ReglaContableException("La clave solo admite mayúsculas, números y guiones bajos (hasta 40).");
        if (cmd.Desde.Year < 1990) throw new ReglaContableException("Indica la fecha desde la que rige.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var descripcion = (cmd.Descripcion ?? "").Trim();
        if (descripcion.Length == 0)
            descripcion = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT descripcion FROM cat.parametro_fiscal WHERE clave = @clave ORDER BY desde DESC LIMIT 1", new { clave }, cancellationToken: ct))
                ?? Legislacion.Defecto.FirstOrDefault(p => p.Clave == clave)?.Descripcion ?? "";
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cat.parametro_fiscal (clave, desde, valor, descripcion) VALUES (@clave, @desde, @valor, @descripcion)
            ON CONFLICT (clave, desde) DO UPDATE SET valor = EXCLUDED.valor, descripcion = EXCLUDED.descripcion
            """, new { clave, desde = cmd.Desde, valor = cmd.Valor, descripcion }, cancellationToken: ct));
        Invalidar();
    }

    public async Task BorrarParametroAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        if (await cn.ExecuteAsync(new CommandDefinition("DELETE FROM cat.parametro_fiscal WHERE id = @id", new { id }, cancellationToken: ct)) == 0)
            throw new ReglaContableException("El parámetro no existe.");
        Invalidar();
    }

    // ------------------------------------------------------------------------------------------------ formularios
    public async Task<IReadOnlyList<VersionFormularioDto>> FormulariosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<VersionFormularioDto>(new CommandDefinition(
            "SELECT id, modelo, desde_anio, comentario, creado_en FROM fiscal.formulario_version WHERE modelo ~ '^[0-9]+$' ORDER BY modelo, desde_anio",
            cancellationToken: ct))).AsList();
    }

    public string DefinicionFormulario(string modelo, int anio)
    {
        Comprobar(modelo);
        return Legislacion.Formulario(modelo, anio)?.Definicion
               ?? Legislacion.Exportar(FormulariosFiscales.DelPrograma(modelo, anio));
    }

    public async Task GuardarFormularioAsync(GuardarFormularioCmd cmd, CancellationToken ct)
    {
        Comprobar(cmd.Modelo);
        if (cmd.DesdeAnio is < 2000 or > 2100) throw new ReglaContableException("Indica el año desde el que rige la versión.");
        try
        {
            if (JsonNode.Parse(cmd.Definicion) is not JsonObject raiz) throw new ReglaContableException("La definición debe ser un objeto JSON.");
            void Tipo<T>(JsonNode? n, string que, string como) where T : JsonNode
            { if (n is not null and not T) throw new ReglaContableException($"«{que}» debe ser {como}."); }
            Tipo<JsonObject>(raiz["renombrar"], "renombrar", "un objeto {casilla vieja: casilla nueva}");
            Tipo<JsonValue>(raiz["resultado"], "resultado", "el número de una casilla");
            Tipo<JsonArray>(raiz["apartados"], "apartados", "una lista");
            Tipo<JsonArray>(raiz["totales"], "totales", "una lista");
            foreach (var a in raiz["apartados"] as JsonArray ?? [])
            {
                if (a is not JsonObject ao || ao["filas"] is not JsonArray filas) throw new ReglaContableException("Cada apartado necesita «titulo» y «filas».");
                foreach (var f in filas)
                    if (f is not JsonObject fo || fo["casillas"] is not JsonArray) throw new ReglaContableException("Cada fila necesita «texto» y «casillas».");
            }
            Legislacion.Aplicar(FormulariosFiscales.DelPrograma(cmd.Modelo, cmd.DesdeAnio), cmd.Definicion);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { throw new ReglaContableException("La definición no es un JSON válido de formulario.", ex.Message); }
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.formulario_version (modelo, desde_anio, definicion, comentario) VALUES (@modelo, @desde, @def::jsonb, @comentario)
            ON CONFLICT (modelo, desde_anio) DO UPDATE SET definicion = EXCLUDED.definicion, comentario = EXCLUDED.comentario, creado_en = now()
            """, new { modelo = cmd.Modelo, desde = (short)cmd.DesdeAnio, def = cmd.Definicion, comentario = cmd.Comentario }, cancellationToken: ct));
        Invalidar();
    }

    public async Task BorrarFormularioAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        if (await cn.ExecuteAsync(new CommandDefinition("DELETE FROM fiscal.formulario_version WHERE id = @id", new { id }, cancellationToken: ct)) == 0)
            throw new ReglaContableException("La versión no existe.");
        Invalidar();
    }

    // ------------------------------------------------------------------------------------------------ diseños de fichero
    public async Task<IReadOnlyList<DisenoResumenDto>> DisenosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        var guardados = (await cn.QueryAsync<FilaDiseno>(new CommandDefinition(
            "SELECT id, modelo, desde, hasta, definicion::text AS definicion, comentario FROM fiscal.diseno_version", cancellationToken: ct))).AsList();
        var lista = FicheroModelo.DelPrograma.Select(d => new DisenoResumenDto(null, d.Modelo, d.Desde, d.Hasta, "Programa", d.Longitud, null)).ToList();
        foreach (var g in guardados)
        {
            int largo;
            try { largo = Legislacion.ImportarDiseno(g.Modelo, g.Desde, g.Hasta, g.Definicion).Longitud; } catch (ReglaContableException) { largo = 0; }
            lista.Add(new DisenoResumenDto(g.Id, g.Modelo, g.Desde, g.Hasta, "Guardado", largo, g.Comentario));
        }
        return lista.OrderBy(d => d.Modelo, StringComparer.Ordinal).ThenBy(d => d.Desde).ToList();
    }

    public string DisenoJson(string modelo, int anio) => Legislacion.ExportarDiseno(FicheroModelo.Diseno(modelo, anio));

    public async Task GuardarDisenoAsync(GuardarDisenoCmd cmd, CancellationToken ct)
    {
        if (!FicheroModelo.Modelos.Contains(cmd.Modelo))
            throw new ReglaContableException($"El modelo {cmd.Modelo} no tiene fichero.", string.Join(", ", FicheroModelo.Modelos));
        if (cmd.Hasta is { } h && h < cmd.Desde) throw new ReglaContableException("«Hasta» no puede ser anterior a «desde».");
        Legislacion.ImportarDiseno(cmd.Modelo, cmd.Desde, cmd.Hasta, cmd.Definicion);       // valida
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.diseno_version (modelo, desde, hasta, definicion, comentario) VALUES (@modelo, @desde, @hasta, @def::jsonb, @comentario)
            ON CONFLICT (modelo, desde) DO UPDATE SET hasta = EXCLUDED.hasta, definicion = EXCLUDED.definicion, comentario = EXCLUDED.comentario, creado_en = now()
            """, new { modelo = cmd.Modelo, desde = cmd.Desde, hasta = cmd.Hasta, def = cmd.Definicion, comentario = cmd.Comentario }, cancellationToken: ct));
        Invalidar();
    }

    public async Task BorrarDisenoAsync(long id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        if (await cn.ExecuteAsync(new CommandDefinition("DELETE FROM fiscal.diseno_version WHERE id = @id", new { id }, cancellationToken: ct)) == 0)
            throw new ReglaContableException("El diseño no existe.");
        Invalidar();
    }

    // ------------------------------------------------------------------------------------------------ balance y PyG del 200
    public EstadoLegislacionDto Estado(string codigo, int anio)
    {
        var m = Estados200.Modelo(codigo, anio);
        var textos = Estados200.Descripciones(anio);
        return new EstadoLegislacionDto
        {
            Codigo = codigo, Anio = anio, Version = Legislacion.Formulario(codigo, anio)?.DesdeAnio,
            Partidas = m.Partidas.Select(p => new PartidaEstadoDto
            {
                Id = p.Id, Descripcion = textos.GetValueOrDefault(p.Id), Acreedora = p.Acreedora,
                Suma = p.Suma?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                Cuentas = ReglaCuenta.Lista(p.Cuentas).Select(r => new CuentaPartidaDto
                    { Cuenta = r.Prefijo, Concepto = r.Concepto.ToString(), Resta = r.Resta, Calculo = r.Calculo(p.Acreedora) }).ToList(),
            }).ToList(),
        };
    }

    public async Task<IReadOnlyList<VersionFormularioDto>> EstadosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        return (await cn.QueryAsync<VersionFormularioDto>(new CommandDefinition(
            "SELECT id, modelo, desde_anio, comentario, creado_en FROM fiscal.formulario_version WHERE modelo = ANY(@c) ORDER BY modelo, desde_anio",
            new { c = Estados200.Codigos }, cancellationToken: ct))).AsList();
    }

    public async Task GuardarEstadoAsync(GuardarEstadoCmd cmd, CancellationToken ct)
    {
        var baseM = Estados200.DelPrograma(cmd.Codigo);
        if (cmd.DesdeAnio is < 2000 or > 2100) throw new ReglaContableException("Indica el año desde el que rige la versión.");
        if (cmd.Partidas.Count == 0) throw new ReglaContableException("El estado no tiene partidas.");
        var partidas = cmd.Partidas.Select(p =>
        {
            var id = (p.Id ?? "").Trim();
            if (id.Length == 0) throw new ReglaContableException("Hay una partida sin casilla.");
            if (p.Suma is { Count: > 0 }) return new PartidaModelo(id, id, 1, p.Acreedora, Suma: string.Join(' ', p.Suma.Select(x => x.Trim())));
            var reglas = p.Cuentas.Where(c => !string.IsNullOrWhiteSpace(c.Cuenta)).Select(c =>
            {
                if (!Enum.TryParse<ConceptoCuenta>(c.Concepto, out var concepto))
                    throw new ReglaContableException($"Concepto no válido en la partida {id}: {c.Concepto}.");
                var cuenta = c.Cuenta.Trim();
                if (!cuenta.All(char.IsAsciiDigit)) throw new ReglaContableException($"La cuenta «{cuenta}» de la partida {id} solo puede tener números.");
                return new ReglaCuenta(cuenta, concepto, c.Resta).ToString();
            });
            return new PartidaModelo(id, id, 2, p.Acreedora, string.Join(' ', reglas));
        }).ToList();
        var m = baseM with { Partidas = partidas };
        Estados200.Validar(m);
        await using var cn = await conexiones.AbrirAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO fiscal.formulario_version (modelo, desde_anio, definicion, comentario) VALUES (@modelo, @desde, @def::jsonb, @comentario)
            ON CONFLICT (modelo, desde_anio) DO UPDATE SET definicion = EXCLUDED.definicion, comentario = EXCLUDED.comentario, creado_en = now()
            """, new { modelo = cmd.Codigo, desde = (short)cmd.DesdeAnio, def = Estados200.Exportar(m), comentario = cmd.Comentario }, cancellationToken: ct));
        Invalidar();
    }

    private static void Comprobar(string modelo)
    {
        if (!ModelosFormulario.Contains(modelo)) throw new ReglaContableException($"Modelo no válido: {modelo}.");
    }
}
