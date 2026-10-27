using System.IO.Compression;
using System.Text.RegularExpressions;
using Contabilidad.Domain;
using Contabilidad.Domain.Importacion.A3Eco;
using Dapper;
using Npgsql;

namespace Contabilidad.Infrastructure.Repositorios;

/// <summary>
/// Importa la contabilidad de a3ECO en una empresa: subcuentas, clientes y proveedores (con su NIF), el asiento de apertura de cada
/// año (los saldos iniciales que guarda a3) y todos sus asientos. Cada año va a un ejercicio natural vacío (se crea si no existe).
/// Los asientos que en a3 no cuadran se importan como borrador para revisarlos.
/// </summary>
public interface IImportacionA3Repositorio
{
    Task<AnalisisA3Dto> AnalizarAsync(long empresaId, ImportarA3Cmd cmd, CancellationToken ct);
    Task<ImportacionA3Dto> ImportarAsync(long empresaId, long usuarioId, ImportarA3Cmd cmd, CancellationToken ct);
}

public sealed partial class ImportacionA3Repositorio(IConexionFactory conexiones, IAsientoRepositorio asientos) : IImportacionA3Repositorio
{
    private const long MaxFichero = 200L * 1024 * 1024;
    private const long MaxTotal = 1024L * 1024 * 1024;

    private sealed class CuentaFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public bool EsImputable { get; set; } public string Naturaleza { get; set; } = ""; }
    private sealed class EjercicioFila { public long Id { get; set; } public string Codigo { get; set; } = ""; public DateOnly FechaInicio { get; set; } public DateOnly FechaFin { get; set; } public string Estado { get; set; } = ""; public int Asientos { get; set; } }
    private sealed class TerceroFila { public long Id { get; set; } public string Numero { get; set; } = ""; public long? CuentaClienteId { get; set; } public long? CuentaProveedorId { get; set; } }

    private sealed record TerceroPlan(TerceroA3 Datos, List<string> Cuentas);

    private sealed class AnioPlan
    {
        public required AnioA3 A3 { get; init; }
        public EjercicioFila? Ejercicio { get; set; }
        public string? Motivo { get; set; }
        public List<(string Codigo, long Saldo)> Apertura { get; } = [];
    }

    private sealed class Plan
    {
        public required DatosA3 Datos { get; init; }
        public required int Digitos { get; init; }
        public required Dictionary<string, string?> Codigos { get; init; }            // subcuenta a3 → código en IConta (null: no cabe)
        public required Dictionary<string, string> Nombres { get; init; }             // código IConta → nombre en a3
        public required Dictionary<string, CuentaFila> Existentes { get; init; }
        public required List<TerceroPlan> Terceros { get; init; }
        public required HashSet<string> NifsExistentes { get; init; }
        public required List<AnioPlan> Anios { get; init; }
        public required List<string> Avisos { get; init; }
    }

    // ------------------------------------------------------------------------------------------------ análisis

    public async Task<AnalisisA3Dto> AnalizarAsync(long empresaId, ImportarA3Cmd cmd, CancellationToken ct)
    {
        var ficheros = Descomprimir(cmd.Contenido);
        await using var cn = await conexiones.AbrirAsync(ct);
        var p = await PlanificarAsync(cn, null, empresaId, ficheros, cmd.Carpeta, null, ct);

        var anios = p.Anios.Select(a =>
        {
            var subcuentas = a.A3.Cuentas.Where(c => c.EsSubcuenta).ToList();
            var descuadres = a.A3.Descuadres();
            return new AnioA3Dto(a.A3.Anio, a.Ejercicio?.Codigo, a.Ejercicio is null && a.Motivo is null,
                subcuentas.Count, subcuentas.Count(c => p.Codigos.GetValueOrDefault(c.Codigo) is { } k && !p.Existentes.ContainsKey(k)),
                a.A3.Asientos.Count, a.A3.Asientos.Sum(x => x.Apuntes.Count),
                a.A3.Asientos.Where(x => !x.Cuadra).Select(x => x.Numero).ToList(),
                a.Apertura.Where(x => x.Saldo > 0).Sum(x => x.Saldo) / 100m, -a.Apertura.Where(x => x.Saldo < 0).Sum(x => x.Saldo) / 100m,
                descuadres.Count == 0, descuadres.Take(20).ToList(), a.Motivo is null, a.Motivo);
        }).ToList();

        var empresas = LectorA3Eco.Empresas(ficheros);
        var carpetas = LectorA3Eco.Carpetas(ficheros);
        return new AnalisisA3Dto(p.Datos.Carpeta,
            carpetas.Select(c => empresas.FirstOrDefault(e => e.Carpeta == c) is { } e ? new EmpresaA3Dto(c, e.Nombre, e.Nif) : new EmpresaA3Dto(c, "", "")).ToList(),
            p.Datos.Empresa is { } em ? new EmpresaA3Dto(em.Carpeta, em.Nombre, em.Nif) : null,
            p.Datos.DigitosCuenta, p.Digitos, p.Terceros.Count, p.Terceros.Count(t => !p.NifsExistentes.Contains(t.Datos.Nif)),
            anios, p.Avisos);
    }

    // ------------------------------------------------------------------------------------------------ importación

    public async Task<ImportacionA3Dto> ImportarAsync(long empresaId, long usuarioId, ImportarA3Cmd cmd, CancellationToken ct)
    {
        if (cmd.Anios is not { Count: > 0 }) throw new ReglaContableException("Elige al menos un año para importar.");
        var ficheros = Descomprimir(cmd.Contenido);

        await using var cn = await conexiones.AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Una importación a la vez por empresa
        await cn.ExecuteAsync(new CommandDefinition("SELECT 1 FROM core.empresa WHERE id = @empresaId FOR UPDATE",
            new { empresaId }, tx, cancellationToken: ct));

        var p = await PlanificarAsync(cn, tx, empresaId, ficheros, cmd.Carpeta, cmd.Anios, ct);
        var faltan = cmd.Anios.Except(p.Anios.Select(a => a.A3.Anio)).ToList();
        if (faltan.Count > 0) throw new ReglaContableException($"En los ficheros no está el año {string.Join(", ", faltan)}.");
        if (p.Anios.FirstOrDefault(a => a.Motivo is not null) is { } mal)
            throw new ReglaContableException($"El año {mal.A3.Anio} no se puede importar.", mal.Motivo);

        // 1) Subcuentas que no existen (las de todos los años elegidos, con el nombre de a3)
        var necesarias = p.Anios.SelectMany(a => a.A3.Cuentas.Where(c => c.EsSubcuenta).Select(c => c.Codigo))
            .Select(c => p.Codigos.GetValueOrDefault(c)).OfType<string>().Distinct().Where(c => !p.Existentes.ContainsKey(c)).Order(StringComparer.Ordinal).ToList();
        foreach (var codigo in necesarias)
        {
            var padre = p.Existentes.GetValueOrDefault(codigo[..3]);
            var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO conta.cuenta (empresa_id, codigo, nombre, nivel, padre_id, naturaleza, es_imputable)
                VALUES (@empresaId, @codigo, @nombre, 4, @padreId, @naturaleza, true) RETURNING id
                """, new
            {
                empresaId, codigo, nombre = Nombre(p.Nombres.GetValueOrDefault(codigo), codigo), padreId = padre?.Id,
                naturaleza = padre?.Naturaleza ?? "MIXTA"
            }, tx, cancellationToken: ct));
            p.Existentes[codigo] = new CuentaFila { Id = id, Codigo = codigo, EsImputable = true, Naturaleza = padre?.Naturaleza ?? "MIXTA" };
        }

        // 2) Clientes, proveedores y acreedores
        var (terceroDeCuenta, creados, actualizados) = await TercerosAsync(cn, tx, empresaId, p, ct);

        // 3) Apertura y asientos de cada año
        var canalId = await cn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT id FROM canal.canal WHERE empresa_id = @empresaId AND tipo = 'BASE' AND activo ORDER BY (codigo = '1') DESC, codigo LIMIT 1
            """, new { empresaId }, tx, cancellationToken: ct)) ?? throw new ReglaContableException("La empresa no tiene canales.");
        var diarioId = await ConsultasContables.DiarioAsync(cn, tx, empresaId, null, ct);

        var resultado = new List<AnioImportadoA3Dto>();
        foreach (var a in p.Anios.OrderBy(x => x.A3.Anio))
        {
            var creado = a.Ejercicio is null;
            var ejercicioId = a.Ejercicio?.Id ?? await EmpresaAdminRepositorio.CrearEjercicioEnTransaccionAsync(cn, tx, empresaId,
                new CrearEjercicioCmd { Codigo = a.A3.Anio.ToString(), FechaInicio = a.A3.Inicio, FechaFin = a.A3.Fin }, ct);

            ApunteCmd Apunte(string codigoA3, long debe, long haber, string? concepto)
            {
                var codigo = p.Codigos[codigoA3]!;
                return new ApunteCmd
                {
                    CuentaId = p.Existentes[codigo].Id, TerceroId = terceroDeCuenta.TryGetValue(codigo, out var t) ? t : null, CanalId = canalId,
                    Debe = debe / 100m, Haber = haber / 100m, Concepto = string.IsNullOrWhiteSpace(concepto) ? null : concepto
                };
            }

            var borradores = 0;
            if (a.Apertura.Count > 0)
            {
                await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO core.periodo (ejercicio_id, numero, tipo, fecha_inicio, fecha_fin) VALUES (@ejercicioId, 0, 'APERTURA', @inicio, @inicio)
                    ON CONFLICT (ejercicio_id, numero) DO UPDATE SET estado = 'ABIERTO'
                    """, new { ejercicioId, inicio = a.A3.Inicio }, tx, cancellationToken: ct));
                var concepto = $"Apertura del ejercicio {a.A3.Anio} (a3ECO)";
                var lineas = a.Apertura.Select(x => Apunte(x.Codigo, Math.Max(x.Saldo, 0), Math.Max(-x.Saldo, 0), concepto)).ToList();
                var cuadra = a.Apertura.Sum(x => x.Saldo) == 0;
                if (!cuadra) borradores++;
                await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
                    new CrearAsientoCmd { Fecha = a.A3.Inicio, DiarioId = diarioId, Concepto = concepto, Borrador = !cuadra || lineas.Count < 2, Apuntes = lineas },
                    "APERTURA", null, $"a3 apertura {a.A3.Anio}", ct, tipoPeriodo: "APERTURA");
            }

            foreach (var x in a.A3.Asientos)
            {
                var borrador = !x.Cuadra || x.Apuntes.Count < 2;
                if (borrador) borradores++;
                // El concepto del asiento: el de la línea del cliente o proveedor (la factura) o, si no hay, el primero
                var concepto = x.Apuntes.Where(l => l.Concepto.Length > 0).OrderBy(l => l.Cuenta[..2] is "40" or "41" or "43" or "44" ? 0 : 1)
                    .Select(l => l.Concepto).FirstOrDefault() ?? $"Asiento {x.Numero} de a3ECO";
                var creadoAs = await asientos.CrearEnTransaccionAsync(cn, tx, empresaId, ejercicioId, usuarioId,
                    new CrearAsientoCmd
                    {
                        Fecha = x.Fecha, DiarioId = diarioId, Concepto = concepto, Borrador = borrador,
                        Apuntes = x.Apuntes.Select(l => Apunte(l.Cuenta, l.Debe ? l.Importe : 0, l.Debe ? 0 : l.Importe, l.Concepto)).ToList()
                    },
                    "IMPORTACION", null, $"a3 {a.A3.Anio}/{x.Numero}", ct);

                // Número de documento de cada apunte (factura, recibo…)
                var docs = x.Apuntes.Select((l, i) => (Linea: (short)(i + 1), l.Documento)).Where(d => d.Documento.Length > 0).ToList();
                if (docs.Count > 0)
                    await cn.ExecuteAsync(new CommandDefinition(
                        "UPDATE conta.apunte SET documento = @documento WHERE asiento_id = @asientoId AND linea = @linea",
                        docs.Select(d => new { documento = d.Documento, asientoId = creadoAs.Id, linea = d.Linea }), tx, cancellationToken: ct));
            }
            resultado.Add(new AnioImportadoA3Dto(a.A3.Anio, ejercicioId, creado, a.A3.Asientos.Count, borradores, a.Apertura.Count > 0));
        }

        await tx.CommitAsync(ct);
        return new ImportacionA3Dto(necesarias.Count, creados, actualizados, resultado, p.Avisos);
    }

    // ------------------------------------------------------------------------------------------------ planificación

    private async Task<Plan> PlanificarAsync(NpgsqlConnection cn, NpgsqlTransaction? tx, long empresaId, Dictionary<string, byte[]> ficheros,
        string? carpeta, IReadOnlyCollection<int>? soloAnios, CancellationToken ct)
    {
        var digitos = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT digitos_cuenta FROM core.empresa WHERE id = @empresaId", new { empresaId }, tx, cancellationToken: ct))
            ?? throw new ReglaContableException("La empresa no existe.");

        var datos = LectorA3Eco.Leer(ficheros, carpeta, DateOnly.FromDateTime(DateTime.Today));
        var avisos = new List<string>(datos.Avisos);
        var elegidos = datos.Anios.Where(a => soloAnios is null || soloAnios.Contains(a.Anio)).ToList();

        // Códigos de IConta de todas las subcuentas de a3 y nombres (los del año más reciente)
        var codigos = new Dictionary<string, string?>();
        var nombres = new Dictionary<string, string>();
        foreach (var c in datos.Anios.SelectMany(a => a.Cuentas).Where(c => c.EsSubcuenta)
                     .Concat(datos.Anios.SelectMany(a => a.Asientos).SelectMany(x => x.Apuntes).Select(x => new CuentaA3(x.Cuenta, true, "", null, 0, 0, [], []))))
        {
            var k = codigos.TryGetValue(c.Codigo, out var ya) ? ya : codigos[c.Codigo] = LectorA3Eco.Convertir(c.Codigo, datos.DigitosCuenta, digitos);
            if (k is not null && c.Nombre.Length > 0) nombres[k] = c.Nombre;
        }
        var noCaben = codigos.Where(x => x.Value is null).Select(x => x.Key).ToList();
        if (noCaben.Count > 0)
            avisos.Add($"{noCaben.Count} subcuenta(s) de a3 no caben en los {digitos} dígitos de la empresa y no se importan: " +
                       string.Join(", ", noCaben.Take(10).Select(c => datos.Anios.SelectMany(x => x.Cuentas).FirstOrDefault(x => x.Codigo == c) is { Nombre.Length: > 0 } n
                           ? $"{c} ({n.Nombre})" : c)) + (noCaben.Count > 10 ? "…" : ""));
        if (datos.Empresa is { } em)
        {
            var nifEmpresa = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT n.numero FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id WHERE e.id = @empresaId", new { empresaId }, tx, cancellationToken: ct));
            if (nifEmpresa is not null && em.Nif.Length > 0 && !string.Equals(nifEmpresa, em.Nif, StringComparison.OrdinalIgnoreCase))
                avisos.Add($"El NIF de la empresa en a3 ({em.Nif}, {em.Nombre}) no es el de esta empresa ({nifEmpresa}).");
        }

        var existentes = (await cn.QueryAsync<CuentaFila>(new CommandDefinition(
            "SELECT id, codigo, es_imputable, naturaleza FROM conta.cuenta WHERE empresa_id = @empresaId",
            new { empresaId }, tx, cancellationToken: ct))).ToDictionary(c => c.Codigo);

        // Terceros: subcuentas de clientes (43, 44), proveedores (40) y acreedores (41) enlazadas a TCLIPRO, agrupadas por NIF
        var porCodigo = datos.Terceros.GroupBy(t => t.Codigo).ToDictionary(g => g.Key, g => g.First());
        var terceros = new Dictionary<string, TerceroPlan>();
        var sinNif = 0;
        foreach (var c in datos.Anios.OrderByDescending(a => a.Anio).SelectMany(a => a.Cuentas).Where(c => c.EsSubcuenta && c.Tercero is not null))
        {
            if (codigos.GetValueOrDefault(c.Codigo) is not { } k || k[..2] is not ("40" or "41" or "43" or "44")) continue;
            if (!porCodigo.TryGetValue(c.Tercero!.Value, out var t) || t.Nif.Length == 0) { sinNif++; continue; }
            if (!terceros.TryGetValue(t.Nif, out var plan)) terceros[t.Nif] = plan = new TerceroPlan(t, []);
            if (!plan.Cuentas.Contains(k)) plan.Cuentas.Add(k);
        }
        if (sinNif > 0) avisos.Add($"{sinNif} subcuenta(s) de clientes o proveedores no tienen NIF en a3: se importan sin ficha de tercero.");
        var nifs = (await cn.QueryAsync<string>(new CommandDefinition(
            "SELECT n.numero FROM conta.tercero t JOIN core.nif n ON n.id = t.nif_id WHERE t.empresa_id = @empresaId",
            new { empresaId }, tx, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Ejercicios
        var ejercicios = (await cn.QueryAsync<EjercicioFila>(new CommandDefinition("""
            SELECT e.id, e.codigo, e.fecha_inicio, e.fecha_fin, e.estado,
                   (SELECT count(*) FROM conta.asiento a WHERE a.ejercicio_id = e.id AND a.estado <> 'ANULADO')::int AS asientos
            FROM core.ejercicio e WHERE e.empresa_id = @empresaId
            """, new { empresaId }, tx, cancellationToken: ct))).AsList();

        var anios = new List<AnioPlan>();
        foreach (var a in elegidos)
        {
            var plan = new AnioPlan { A3 = a };
            foreach (var c in a.Cuentas.Where(c => c.EsSubcuenta && c.AperturaDebe != c.AperturaHaber))
                plan.Apertura.Add((c.Codigo, c.AperturaDebe - c.AperturaHaber));

            var solapa = ejercicios.Where(e => e.FechaInicio <= a.Fin && e.FechaFin >= a.Inicio).ToList();
            var usadas = Usadas(a);
            if (solapa.Count == 1 && solapa[0].FechaInicio == a.Inicio && solapa[0].FechaFin == a.Fin) plan.Ejercicio = solapa[0];
            else if (solapa.Count > 0)
                plan.Motivo = $"Ya hay un ejercicio ({string.Join(", ", solapa.Select(e => e.Codigo))}) que no coincide con el año natural {a.Anio}.";

            if (plan.Motivo is null && plan.Ejercicio is { } ej)
            {
                if (ej.Estado != "ABIERTO") plan.Motivo = $"El ejercicio {ej.Codigo} está {ej.Estado.ToLowerInvariant()}.";
                else if (ej.Asientos > 0) plan.Motivo = $"El ejercicio {ej.Codigo} ya tiene {ej.Asientos} asiento(s): solo se importa en un ejercicio vacío.";
            }
            if (plan.Motivo is null && a.Asientos.Count == 0 && plan.Apertura.Count == 0) plan.Motivo = "No tiene asientos ni saldos de apertura.";
            if (plan.Motivo is null && usadas.FirstOrDefault(c => codigos.GetValueOrDefault(c) is null) is { } nc)
                plan.Motivo = $"La subcuenta {nc} de a3 no cabe en los {digitos} dígitos de la empresa.";
            if (plan.Motivo is null && usadas.Select(c => codigos[c]!).FirstOrDefault(k => existentes.TryGetValue(k, out var e) && !e.EsImputable) is { } ni)
                plan.Motivo = $"La cuenta {ni} existe en la empresa pero no admite apuntes.";
            if (a.Descuadres() is { Count: > 0 } descuadres)
                avisos.Add($"{a.Anio}: las sumas de {descuadres.Count} subcuenta(s) no coinciden con las que guarda a3 (puede faltar algún fichero de asientos).");
            anios.Add(plan);
        }

        return new Plan
        {
            Datos = datos, Digitos = digitos, Codigos = codigos, Nombres = nombres, Existentes = existentes,
            Terceros = terceros.Values.ToList(), NifsExistentes = nifs, Anios = anios, Avisos = avisos
        };
    }

    /// <summary>Subcuentas de a3 con apuntes o saldo de apertura en el año.</summary>
    private static HashSet<string> Usadas(AnioA3 a)
        => a.Asientos.SelectMany(x => x.Apuntes).Select(x => x.Cuenta)
            .Concat(a.Cuentas.Where(c => c.EsSubcuenta && c.AperturaDebe != c.AperturaHaber).Select(c => c.Codigo)).ToHashSet();

    // ------------------------------------------------------------------------------------------------ terceros

    [GeneratedRegex(@"^([0-9XYZKLM][0-9]{7}[A-Z]|[A-HJNPQRSUVW][0-9]{7}[0-9A-J])$")]
    private static partial Regex NifEspanol();

    private static async Task<(Dictionary<string, long> TerceroDeCuenta, int Creados, int Actualizados)> TercerosAsync(
        NpgsqlConnection cn, NpgsqlTransaction tx, long empresaId, Plan p, CancellationToken ct)
    {
        var deCuenta = new Dictionary<string, long>();
        int creados = 0, actualizados = 0;
        var paises = (await cn.QueryAsync<string>(new CommandDefinition("SELECT iso2 FROM cat.pais", transaction: tx, cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var t in p.Terceros)
        {
            var d = t.Datos;
            var numero = d.Nif.StartsWith("ES", StringComparison.Ordinal) && NifEspanol().IsMatch(d.Nif[2..]) ? d.Nif[2..] : d.Nif;
            string pais = "ES", tipo;
            if (NifEspanol().IsMatch(numero)) tipo = numero[0] is 'X' or 'Y' or 'Z' ? "NIE" : char.IsAsciiDigit(numero[0]) || numero[0] is 'K' or 'L' or 'M' ? "NIF" : "CIF";
            else if (numero.Length > 2 && (numero[..2] == "EL" ? "GR" : numero[..2]) is var iso && paises.Contains(iso) && iso != "ES")
            { pais = iso; tipo = "VIES"; }                              // NIF-IVA europeo (Grecia usa el prefijo EL)
            else tipo = "OTRO";
            var fisica = tipo is "NIF" or "NIE";
            if (numero.Length > 20) numero = numero[..20];

            var nifId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.nif (pais_id, tipo_documento, numero, razon_social, es_persona_fisica, direccion, codigo_postal, poblacion, provincia_id, telefono)
                SELECT p.id, @tipo, @numero, @nombre, @fisica, @direccion, @cp, @poblacion,
                       (SELECT pr.id FROM cat.provincia pr WHERE pr.pais_id = p.id AND pr.codigo = left(@cp, 2)), @telefono
                FROM cat.pais p WHERE p.iso2 = @pais
                ON CONFLICT (pais_id, numero) DO UPDATE SET
                    direccion = COALESCE(core.nif.direccion, EXCLUDED.direccion),
                    codigo_postal = COALESCE(core.nif.codigo_postal, EXCLUDED.codigo_postal),
                    poblacion = COALESCE(core.nif.poblacion, EXCLUDED.poblacion),
                    provincia_id = COALESCE(core.nif.provincia_id, EXCLUDED.provincia_id),
                    telefono = COALESCE(core.nif.telefono, EXCLUDED.telefono)
                RETURNING id
                """, new
            {
                tipo, numero, nombre = Nombre(d.Nombre, numero), fisica, direccion = d.Direccion, cp = pais == "ES" ? d.CodigoPostal : null,
                poblacion = d.Poblacion, telefono = d.Telefono, pais
            }, tx, cancellationToken: ct));

            var cuentas = t.Cuentas.Select(k => (Codigo: k, p.Existentes[k].Id)).ToList();
            var cliente = cuentas.FirstOrDefault(c => c.Codigo[..2] is "43" or "44");
            var proveedor = cuentas.FirstOrDefault(c => c.Codigo[..2] is "40" or "41");
            var existente = await cn.QuerySingleOrDefaultAsync<TerceroFila>(new CommandDefinition(
                "SELECT id, cuenta_cliente_id, cuenta_proveedor_id FROM conta.tercero WHERE empresa_id = @empresaId AND nif_id = @nifId",
                new { empresaId, nifId }, tx, cancellationToken: ct));

            long id;
            if (existente is not null)
            {
                id = existente.Id;
                await cn.ExecuteAsync(new CommandDefinition("""
                    UPDATE conta.tercero SET
                        cuenta_cliente_id = COALESCE(cuenta_cliente_id, @cliente), es_cliente = es_cliente OR @esCliente,
                        cuenta_proveedor_id = COALESCE(cuenta_proveedor_id, @proveedor), es_proveedor = es_proveedor OR @esProveedor,
                        es_acreedor = es_acreedor OR @esAcreedor
                    WHERE id = @id
                    """, new
                {
                    id, cliente = cliente.Codigo is null ? (long?)null : cliente.Id, esCliente = cliente.Codigo is not null,
                    proveedor = proveedor.Codigo is null ? (long?)null : proveedor.Id,
                    esProveedor = proveedor.Codigo?.StartsWith("40") == true, esAcreedor = proveedor.Codigo?.StartsWith("41") == true
                }, tx, cancellationToken: ct));
                actualizados++;
            }
            else
            {
                var codigo = cuentas[0].Codigo;
                if (await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT EXISTS (SELECT 1 FROM conta.tercero WHERE empresa_id = @empresaId AND codigo = @codigo)",
                        new { empresaId, codigo }, tx, cancellationToken: ct)))
                    codigo = ("A3" + numero)[..Math.Min(15, numero.Length + 2)];
                id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO conta.tercero (empresa_id, nif_id, codigo, nombre, es_cliente, es_proveedor, es_acreedor,
                                               cuenta_cliente_id, cuenta_proveedor_id, direccion, telefono)
                    VALUES (@empresaId, @nifId, @codigo, @nombre, @esCliente, @esProveedor, @esAcreedor, @cliente, @proveedor, @direccion, @telefono)
                    RETURNING id
                    """, new
                {
                    empresaId, nifId, codigo, nombre = Nombre(d.Nombre, numero), esCliente = cliente.Codigo is not null,
                    esProveedor = proveedor.Codigo?.StartsWith("40") == true, esAcreedor = proveedor.Codigo?.StartsWith("41") == true,
                    cliente = cliente.Codigo is null ? (long?)null : cliente.Id, proveedor = proveedor.Codigo is null ? (long?)null : proveedor.Id,
                    direccion = d.Direccion, telefono = d.Telefono
                }, tx, cancellationToken: ct));
                creados++;
            }
            foreach (var c in cuentas) deCuenta[c.Codigo] = id;
        }

        if (deCuenta.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE conta.cuenta SET requiere_tercero = true WHERE empresa_id = @empresaId AND codigo = ANY(@codigos)",
                new { empresaId, codigos = deCuenta.Keys.ToArray() }, tx, cancellationToken: ct));
        return (deCuenta, creados, actualizados);
    }

    // ------------------------------------------------------------------------------------------------ utilidades

    private static string Nombre(string? nombre, string alternativo) => string.IsNullOrWhiteSpace(nombre) ? alternativo : nombre.Trim();

    /// <summary>Ficheros .DAT del zip (los índices, PDF y demás no hacen falta), con límites para no agotar la memoria.</summary>
    private static Dictionary<string, byte[]> Descomprimir(string base64)
    {
        byte[] zip;
        try { zip = Convert.FromBase64String(base64 ?? ""); }
        catch (FormatException) { throw new ReglaContableException("El contenido del fichero no es válido."); }
        if (zip.Length == 0) throw new ReglaContableException("Elige el fichero .zip con los datos de a3ECO.");

        var r = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            long total = 0;
            foreach (var e in z.Entries)
            {
                if (!e.Name.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Length > MaxFichero || (total += e.Length) > MaxTotal)
                    throw new ReglaContableException("Los ficheros de a3ECO son demasiado grandes para importarlos de una vez.");
                using var s = e.Open();
                using var ms = new MemoryStream((int)e.Length);
                s.CopyTo(ms);
                if (ms.Length != e.Length) throw new ReglaContableException($"El fichero {e.FullName} del zip está dañado.");
                r[e.FullName.Replace('\\', '/')] = ms.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            throw new ReglaContableException("El fichero no es un .zip válido.", "Comprime la carpeta en formato zip (no 7z ni rar).");
        }
        return r;
    }
}
