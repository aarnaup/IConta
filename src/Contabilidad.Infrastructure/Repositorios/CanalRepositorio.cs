using Contabilidad.Domain;
using Contabilidad.Domain.Canales;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ICanalRepositorio
{
    Task<IReadOnlyList<CanalDto>> ListarAsync(long empresaId, CancellationToken ct);
    Task<ConfiguracionCanal> ObtenerConfiguracionAsync(long empresaId, long canalId, DateOnly fecha, CancellationToken ct);
    Task<long> CrearVersionAsync(long empresaId, long canalId, NuevaVersionCanalCmd cmd, CancellationToken ct);
}

public sealed class CanalRepositorio(IConexionFactory conexiones) : ICanalRepositorio
{
    private sealed class CanalFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public string Tipo { get; set; } = ""; }
    private sealed class LineaFila
    {
        public long CanalBaseId { get; set; }
        public string Modo { get; set; } = "";
        public decimal? Porcentaje { get; set; }
        public string? Expresion { get; set; }
        public short Decimales { get; set; }
        public short Orden { get; set; }
    }

    public async Task<IReadOnlyList<CanalDto>> ListarAsync(long empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);
        const string sql = "SELECT id, codigo, nombre, tipo FROM canal.canal WHERE empresa_id = @empresaId AND activo ORDER BY tipo, codigo";
        return (await cn.QueryAsync<CanalDto>(new CommandDefinition(sql, new { empresaId }, cancellationToken: ct))).AsList();
    }

    public async Task<ConfiguracionCanal> ObtenerConfiguracionAsync(long empresaId, long canalId, DateOnly fecha, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct);

        var canal = await cn.QuerySingleOrDefaultAsync<CanalFila>(new CommandDefinition(
            "SELECT id, codigo, tipo FROM canal.canal WHERE id = @canalId AND empresa_id = @empresaId AND activo",
            new { canalId, empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException($"El canal {canalId} no existe en la empresa.");

        if (canal.Tipo == "BASE")
            return new ConfiguracionCanal(canal.Id, canal.Codigo, TipoCanal.Base, null, []);

        // Versión de fórmula vigente: la última con vigente_desde <= fecha
        var versionId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT canal.version_vigente(@canalId, @fecha)", new { canalId, fecha }, cancellationToken: ct));
        if (versionId is null)
            return new ConfiguracionCanal(canal.Id, canal.Codigo, TipoCanal.Derivado, null, []);

        var filas = await cn.QueryAsync<LineaFila>(new CommandDefinition("""
            SELECT canal_base_id, modo, porcentaje, expresion, decimales, orden
            FROM canal.canal_version_linea WHERE canal_version_id = @versionId ORDER BY orden
            """, new { versionId }, cancellationToken: ct));

        var lineas = filas.Select(f => new LineaCanal(
            f.CanalBaseId,
            f.Modo switch { "PORCENTAJE" => ModoReparto.Porcentaje, "FORMULA" => ModoReparto.Formula, _ => ModoReparto.Resto },
            f.Porcentaje, f.Expresion, f.Decimales, f.Orden)).ToList();

        return new ConfiguracionCanal(canal.Id, canal.Codigo, TipoCanal.Derivado, versionId, lineas);
    }

    /// <summary>Crea una NUEVA versión (las anteriores no se tocan): se aplicará a asientos con fecha >= vigenteDesde.</summary>
    public async Task<long> CrearVersionAsync(long empresaId, long canalId, NuevaVersionCanalCmd cmd, CancellationToken ct)
    {
        if (cmd.Lineas.Count == 0) throw new ReglaContableException("La versión necesita al menos una línea.");
        foreach (var l in cmd.Lineas)
        {
            var modo = l.Modo.ToUpperInvariant();
            if (modo is not ("PORCENTAJE" or "FORMULA" or "RESTO"))
                throw new ReglaContableException($"Modo de reparto no válido: {l.Modo}");
            if (modo == "FORMULA")   // valida sintaxis probando con un importe de ejemplo
                ExpresionEvaluador.Evaluar(l.Expresion ?? "", new Dictionary<string, decimal> { ["IMPORTE"] = 100, ["DEBE"] = 100, ["HABER"] = 0 });
        }

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var tipo = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT tipo FROM canal.canal WHERE id = @canalId AND empresa_id = @empresaId", new { canalId, empresaId }, tx, cancellationToken: ct));
        if (tipo != "DERIVADO") throw new ReglaContableException("Solo los canales derivados tienen fórmula.");

        var ids = cmd.Lineas.Select(l => l.CanalBaseId).Distinct().ToArray();
        var validos = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM canal.canal WHERE empresa_id = @empresaId AND tipo = 'BASE' AND id = ANY(@ids)",
            new { empresaId, ids }, tx, cancellationToken: ct));
        if (validos != ids.Length) throw new ReglaContableException("Alguna línea apunta a un canal base inexistente.");

        var versionId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO canal.canal_version (canal_id, vigente_desde, descripcion)
            VALUES (@canalId, @vigenteDesde, @descripcion) RETURNING id
            """, new { canalId, vigenteDesde = cmd.VigenteDesde, descripcion = cmd.Descripcion }, tx, cancellationToken: ct));

        short orden = 0;
        foreach (var l in cmd.Lineas)
        {
            orden++;
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO canal.canal_version_linea (canal_version_id, orden, canal_base_id, modo, porcentaje, expresion, decimales)
                VALUES (@versionId, @orden, @canalBaseId, @modo, @porcentaje, @expresion, @decimales)
                """, new
            {
                versionId, orden, canalBaseId = l.CanalBaseId, modo = l.Modo.ToUpperInvariant(),
                porcentaje = l.Porcentaje, expresion = l.Expresion, decimales = l.Decimales
            }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return versionId;
    }
}
