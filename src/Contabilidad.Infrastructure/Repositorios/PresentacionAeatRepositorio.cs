using System.Text.Json;
using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Domain.Fiscal.Presentacion;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface IPresentacionAeatRepositorio
{
    /// <summary>Fichero en el formato del diseño de registro de la AEAT de una declaración guardada.</summary>
    Task<(byte[] Contenido, string Nombre)> FicheroAsync(long empresaId, long declaracionId, FicheroAeatCmd cmd, string? nifDesarrollador,
        CancellationToken ct);
}

public sealed class PresentacionAeatRepositorio(IConexionFactory conexiones, IFiscalRepositorio fiscal) : IPresentacionAeatRepositorio
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class EmpresaFila
    {
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string RegimenIva { get; set; } = "";
        public string? Iae { get; set; }
        public string? Actividad { get; set; }
        public short? Seccion { get; set; }
    }

    private sealed class TerceroFila
    {
        public long Id { get; set; }
        public string? Pais { get; set; }
        public bool PaisUe { get; set; }
        public string? Provincia { get; set; }
        public string? CodigoPostal { get; set; }
    }

    public async Task<(byte[] Contenido, string Nombre)> FicheroAsync(long empresaId, long declaracionId, FicheroAeatCmd cmd,
        string? nifDesarrollador, CancellationToken ct)
    {
        var v = await fiscal.ObtenerAsync(empresaId, declaracionId, ct);
        if (v.Modelo is "420" or "425") return await FicheroAtcAsync(empresaId, v, cmd, ct);
        if (!FicheroModelo.Modelos.Contains(v.Modelo))
            throw new ReglaContableException($"El modelo {v.Modelo} no tiene fichero de presentación todavía.",
                $"Disponible para {string.Join(", ", FicheroModelo.Modelos)}.");
        if (v.Estado is not ("CALCULADA" or "PRESENTADA"))
            throw new ReglaContableException("Guarda (calcula) la declaración antes de descargar el fichero.");

        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await cn.QuerySingleAsync<EmpresaFila>(new CommandDefinition("""
            SELECT n.numero AS nif, e.nombre, e.regimen_iva,
                   (SELECT ep.codigo FROM core.empresa_actividad a JOIN cat.epigrafe_iae ep ON ep.id = a.epigrafe_id
                    WHERE a.empresa_id = e.id AND a.fecha_baja IS NULL ORDER BY a.es_principal DESC, a.id LIMIT 1) AS iae,
                   (SELECT COALESCE(a.nombre, ep.descripcion) FROM core.empresa_actividad a JOIN cat.epigrafe_iae ep ON ep.id = a.epigrafe_id
                    WHERE a.empresa_id = e.id AND a.fecha_baja IS NULL ORDER BY a.es_principal DESC, a.id LIMIT 1) AS actividad,
                   (SELECT ep.seccion FROM core.empresa_actividad a JOIN cat.epigrafe_iae ep ON ep.id = a.epigrafe_id
                    WHERE a.empresa_id = e.id AND a.fecha_baja IS NULL ORDER BY a.es_principal DESC, a.id LIMIT 1) AS seccion
            FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id WHERE e.id = @empresaId
            """, new { empresaId }, cancellationToken: ct));

        // Complementarias y sustitutivas llevan el justificante de la presentación que corrigen
        string? anterior = null;
        if (v.TipoDeclaracion != "ORDINARIA")
        {
            anterior = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("""
                SELECT d.justificante FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id
                WHERE d.empresa_id = @empresaId AND m.codigo = @modelo AND d.anio = @anio AND d.periodo = @periodo
                  AND d.estado = 'PRESENTADA' AND d.numero_presentacion < @numero
                ORDER BY d.numero_presentacion DESC LIMIT 1
                """, new { empresaId, modelo = v.Modelo, anio = (short)v.Anio, periodo = v.Periodo, numero = v.NumeroPresentacion }, cancellationToken: ct));
            if (string.IsNullOrWhiteSpace(anterior) || anterior.Trim().Length != 13)
                throw new ReglaContableException("Falta el justificante (13 dígitos) de la presentación anterior.",
                    "Anótalo al marcarla como presentada: la complementaria o sustitutiva tiene que llevarlo.");
        }

        if (v.Modelo == "303" && e.RegimenIva == "SIMPLIFICADO")
            throw new ReglaContableException("El fichero del 303 no incluye todavía la parte del régimen simplificado.",
                "Presenta el 303 rellenando el formulario de la Sede con las casillas calculadas.");
        var registros = await RegistrosAsync(cn, v, ct);
        var hay347 = await cn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id
                           WHERE d.empresa_id = @empresaId AND m.codigo = '347' AND d.anio = @anio AND d.estado <> 'ANULADA')
            """, new { empresaId, anio = (short)v.Anio }, cancellationToken: ct));

        // Actividad principal: página 4 del 303 de exonerados y datos estadísticos del 390
        if (cmd.Exonerado390 || v.Modelo == "390")
        {
            if (string.IsNullOrWhiteSpace(cmd.ActividadIae)) cmd.ActividadIae = e.Iae;
            if (string.IsNullOrWhiteSpace(cmd.ActividadClave)) cmd.ActividadClave = e.Seccion switch { 2 => "A02", 3 => "A03", _ => "A01" };
            if (string.IsNullOrWhiteSpace(cmd.ActividadNombre)) cmd.ActividadNombre = e.Actividad;
        }

        var d = new DeclaracionParaFichero(v.Id ?? 0, v.Modelo, v.Anio, v.Periodo, v.TipoDeclaracion, NifEspanol(e.Nif), e.Nombre,
            v.Casillas, v.Resultado, v.ADevolver, anterior?.Trim(), registros,
            CriterioCaja: e.RegimenIva == "CRITERIO_CAJA", RegimenSimplificado: e.RegimenIva == "SIMPLIFICADO", PresentaModelo347: hay347);
        return FicheroModelo.Generar(d, cmd, nifDesarrollador);
    }

    private sealed class DomicilioFila
    {
        public string Nif { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string? Direccion { get; set; }
        public string? CodigoPostal { get; set; }
        public string? Provincia { get; set; }
        public string? Telefono { get; set; }
        public string? Email { get; set; }
    }

    /// <summary>XML del 420 o del 425 para el programa de ayuda de la Agencia Tributaria Canaria.</summary>
    private async Task<(byte[] Contenido, string Nombre)> FicheroAtcAsync(long empresaId, DeclaracionVista v, FicheroAeatCmd cmd, CancellationToken ct)
    {
        if (v.Estado is not ("CALCULADA" or "PRESENTADA"))
            throw new ReglaContableException("Guarda (calcula) la declaración antes de descargar el fichero.");
        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await cn.QuerySingleAsync<DomicilioFila>(new CommandDefinition("""
            SELECT n.numero AS nif, e.nombre, n.direccion, n.codigo_postal, pr.codigo AS provincia, n.telefono, n.email
            FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id LEFT JOIN cat.provincia pr ON pr.id = n.provincia_id
            WHERE e.id = @empresaId
            """, new { empresaId }, cancellationToken: ct));
        string? anterior = null;
        if (v.TipoDeclaracion == "COMPLEMENTARIA")
            anterior = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("""
                SELECT d.justificante FROM fiscal.declaracion d JOIN fiscal.modelo_fiscal m ON m.id = d.modelo_id
                WHERE d.empresa_id = @empresaId AND m.codigo = @modelo AND d.anio = @anio AND d.periodo = @periodo
                  AND d.estado = 'PRESENTADA' AND d.numero_presentacion < @numero
                ORDER BY d.numero_presentacion DESC LIMIT 1
                """, new { empresaId, modelo = v.Modelo, anio = (short)v.Anio, periodo = v.Periodo, numero = v.NumeroPresentacion }, cancellationToken: ct));
        var cp = (e.CodigoPostal ?? "").Trim();
        var datos = new FicheroAtcDatos
        {
            Nif = NifEspanol(e.Nif), Nombre = e.Nombre, SiglasVia = string.IsNullOrWhiteSpace(cmd.SiglasVia) ? "CL" : cmd.SiglasVia.Trim(),
            NombreVia = e.Direccion ?? "", Provincia = e.Provincia ?? (cp.Length >= 2 ? cp[..2] : ""), Municipio = (cmd.CodigoMunicipio ?? "").Trim(),
            CodigoPostal = cp, Telefono = cmd.Telefono ?? e.Telefono, Email = cmd.Email ?? e.Email,
            FormaPago = string.IsNullOrWhiteSpace(cmd.FormaPagoAtc) ? "5" : cmd.FormaPagoAtc.Trim(), Iban = cmd.Iban, JustificanteAnterior = anterior,
        };
        if (datos.FormaPago is not ("1" or "2" or "3" or "4" or "5"))
            throw new ReglaContableException("Forma de pago no válida: 1 efectivo, 2 adeudo en cuenta, 3 pago fraccionado, 4 domiciliación o 5 pago telemático.");
        var doc = v.Modelo == "420"
            ? XmlAtc.Modelo420(v.Anio, v.Periodo, v.TipoDeclaracion, v.Casillas, v.Resultado, v.ADevolver > 0, datos)
            : XmlAtc.Modelo425(v.Anio, v.Casillas, DateOnly.FromDateTime(DateTime.Today), datos);
        return (XmlAtc.Bytes(doc), $"{v.Modelo}_{datos.Nif}_{v.Anio}_{v.Periodo}.xml");
    }

    private static string NifEspanol(string nif)
    {
        var n = new string(nif.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return n.StartsWith("ES", StringComparison.Ordinal) && n.Length == 11 ? n[2..] : n;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<RegistroAeat>>> RegistrosAsync(Npgsql.NpgsqlConnection cn,
        DeclaracionVista v, CancellationToken ct)
    {
        var r = new Dictionary<string, IReadOnlyList<RegistroAeat>>();
        if (v.Detalle is not JsonElement det || det.ValueKind == JsonValueKind.Null) return r;
        switch (v.Modelo)
        {
            case "347":
            {
                var filas = det.Deserialize<List<Fila347>>(Json) ?? [];
                var ids = filas.Select(f => f.TerceroId).Distinct().ToArray();
                var terceros = (await cn.QueryAsync<TerceroFila>(new CommandDefinition("""
                    SELECT t.id, p.iso2 AS pais, COALESCE(p.es_ue, false) AS pais_ue, pr.codigo AS provincia, n.codigo_postal
                    FROM conta.tercero t JOIN core.nif n ON n.id = t.nif_id
                    LEFT JOIN cat.pais p ON p.id = n.pais_id LEFT JOIN cat.provincia pr ON pr.id = n.provincia_id
                    WHERE t.id = ANY(@ids)
                    """, new { ids }, cancellationToken: ct))).ToDictionary(t => t.Id);
                var sinProvincia = new List<string>();
                var lista = new List<RegistroAeat>();
                foreach (var f in filas)
                {
                    terceros.TryGetValue(f.TerceroId, out var t);
                    var pais = (t?.Pais ?? "ES").ToUpperInvariant();
                    var provincia = pais != "ES" ? "99" : t?.Provincia ?? (t?.CodigoPostal is { Length: >= 2 } cp ? cp[..2] : null);
                    if (provincia is null) sinProvincia.Add(f.Nombre);
                    lista.Add(new RegistroAeat
                    {
                        ["nif"] = pais == "ES" ? NifEspanol(f.Nif) : "",
                        ["nifComunitario"] = pais != "ES" && t?.PaisUe == true ? Calculador349.NifComunitario(f.Nif, pais) : "",
                        ["nombre"] = f.Nombre, ["provincia"] = provincia, ["pais"] = pais == "ES" ? "" : pais, ["clave"] = f.Clave,
                        ["importe"] = f.Total, ["t1"] = f.T1, ["t2"] = f.T2, ["t3"] = f.T3, ["t4"] = f.T4,
                        ["metalico"] = 0m, ["ejercicioMetalico"] = "0000",
                    });
                }
                if (sinProvincia.Count > 0)
                    throw new ReglaContableException("Falta la provincia (código postal) de: " + string.Join(", ", sinProvincia.Distinct()) + ".",
                        "Complétala en la ficha del tercero y vuelve a calcular el 347.");
                r["registros"] = lista;
                break;
            }
            case "349":
            {
                var d = det.Deserialize<Detalle349>(Json);
                if (d is null) break;
                r["registros"] = d.Operaciones.Select(o => new RegistroAeat
                {
                    ["nif"] = o.NifComunitario, ["nombre"] = o.Nombre, ["clave"] = o.Clave, ["base"] = o.Base,
                }).ToList();
                r["rectificaciones"] = d.Rectificaciones.Select(o => new RegistroAeat
                {
                    ["nif"] = o.NifComunitario, ["nombre"] = o.Nombre, ["clave"] = o.Clave, ["ejercicio"] = o.Anio.ToString(),
                    ["periodo"] = o.Periodo, ["base"] = o.BaseRectificada, ["baseAnterior"] = o.BaseAnterior,
                }).ToList();
                if (d.Operaciones.Any(o => o.Base < 0))
                    throw new ReglaContableException("Hay operadores con base negativa: el 349 no los admite.",
                        "Indica la factura rectificada para declararla como rectificación.");
                break;
            }
            case "190":
            {
                var lineas = det.Deserialize<List<PerceptorResumen>>(Json) ?? [];
                r["registros"] = lineas.Select(l =>
                {
                    var reg = new RegistroAeat
                    {
                        ["nif"] = NifEspanol(l.Nif), ["nombre"] = l.Nombre, ["provincia"] = l.Provincia, ["clave"] = l.Clave,
                        ["subclave"] = l.Subclave ?? "00", ["signoBase"] = l.Base < 0 ? "N" : " ", ["base"] = Math.Abs(l.Base),
                        ["retencion"] = Math.Abs(l.Retencion),
                        ["signoEspecie"] = l.Especie < 0 ? "N" : " ", ["especie"] = Math.Abs(l.Especie),
                        ["ingresoCuenta"] = l.IngresoACuenta, ["ingresoCuentaRepercutido"] = l.IngresoRepercutido,
                    };
                    // Datos adicionales: solo para los trabajadores (clave A)
                    if (l.Datos is { } t)
                    {
                        reg["anioNacimiento"] = (decimal)(t.AnioNacimiento ?? 0);
                        reg["situacionFamiliar"] = (decimal)t.SituacionFamiliar;
                        reg["nifConyuge"] = t.NifConyuge ?? "";
                        reg["discapacidad"] = (decimal)t.Discapacidad;
                        reg["contrato"] = (decimal)t.Contrato;
                        reg["movilidad"] = t.MovilidadGeografica ? 1m : 0m;
                        reg["gastosDeducibles"] = l.GastosDeducibles;
                    }
                    return reg;
                }).ToList();
                break;
            }
        }
        return r;
    }
}
