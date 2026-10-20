using Contabilidad.Domain;
using Contabilidad.Domain.Fiscal;
using Contabilidad.Infrastructure.Fiscal;
using Dapper;

namespace Contabilidad.Infrastructure.Repositorios;

public interface ILibrosRegistroRepositorio
{
    Task<LibroRegistroDto> ObtenerAsync(long empresaId, string libro, int anio, string periodo, CancellationToken ct);
    /// <summary>Los dos libros en el formato XLSX de la AEAT; devuelve el contenido y el nombre de fichero.</summary>
    Task<(byte[] Contenido, string Nombre)> FormatoAeatAsync(long empresaId, int anio, string periodo, CancellationToken ct);
}

/// <summary>
/// Libros registro de IVA/IGIC (RD 1624/1992, arts. 63 y 64): una línea por factura contabilizada y tipo impositivo.
/// El periodo se selecciona por fecha contable, igual que el 303/420, para que los totales se puedan contrastar con la declaración.
/// </summary>
public sealed class LibrosRegistroRepositorio(IConexionFactory conexiones) : ILibrosRegistroRepositorio
{
    private sealed class CobroLibroFila
    {
        public long FacturaId { get; set; }
        public DateOnly Fecha { get; set; }
        public decimal Importe { get; set; }
        public string? Forma { get; set; }
        public string? Iban { get; set; }
    }

    public async Task<LibroRegistroDto> ObtenerAsync(long empresaId, string libro, int anio, string periodo, CancellationToken ct)
    {
        libro = (libro ?? "").Trim().ToUpperInvariant();
        if (!LibrosRegistro.Libros.Contains(libro)) throw new ReglaContableException("Libro no válido: EMITIDAS o RECIBIDAS.");
        if (anio is < 2000 or > 2100) throw new ReglaContableException("El año no es válido.");
        var p = PeriodoFiscal.Resolver(anio, (periodo ?? "").Trim().ToUpperInvariant());

        await using var cn = await conexiones.AbrirAsync(ct);
        var impuesto = await cn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT impuesto_indirecto FROM core.empresa WHERE id = @empresaId", new { empresaId }, cancellationToken: ct))
            ?? throw new ReglaContableException("La empresa no existe.");

        var lineas = (await cn.QueryAsync<LineaLibroDto>(new CommandDefinition("""
            SELECT f.id AS factura_id, f.numero_registro, f.fecha_expedicion, f.fecha_operacion, f.fecha_contable, f.serie, f.numero,
                   f.tipo_factura, n.numero AS nif, t.nombre, pa.iso2 AS pais_iso2, pa.es_ue AS pais_ue, n.tipo_documento, fi.tipo_operacion, fi.base, fi.porcentaje, fi.cuota, fi.porcentaje_recargo,
                   fi.cuota_recargo, fi.cuota_deducible, fi.es_bien_inversion AS bien_inversion,
                   CASE WHEN row_number() OVER w = 1 THEN f.retencion_total END AS retencion,
                   CASE WHEN row_number() OVER w = 1 THEN f.total END AS total_factura,
                   (row_number() OVER w = 1) AS primera_linea,
                   NULLIF(COALESCE(r.serie, '') || COALESCE(r.numero, ''), '') AS factura_rectificada,
                   s.numero AS asiento_numero, f.descripcion, f.criterio_caja
            FROM fiscal.factura f
            JOIN fiscal.factura_iva fi ON fi.factura_id = f.id
            JOIN conta.tercero t ON t.id = f.tercero_id
            JOIN core.nif n ON n.id = t.nif_id
            JOIN cat.pais pa ON pa.id = n.pais_id
            LEFT JOIN fiscal.factura r ON r.id = f.factura_rectificada_id
            LEFT JOIN conta.asiento s ON s.id = f.asiento_id
            WHERE f.empresa_id = @empresaId AND f.libro = @libro AND f.estado = 'CONTABILIZADA'
              AND f.fecha_contable BETWEEN @desde AND @hasta
            WINDOW w AS (PARTITION BY f.id ORDER BY fi.porcentaje DESC, fi.id)
            ORDER BY CASE WHEN @libro = 'RECIBIDAS' THEN f.anio_registro END, CASE WHEN @libro = 'RECIBIDAS' THEN f.numero_registro END,
                     f.fecha_expedicion, COALESCE(f.serie, ''), f.numero, f.id, fi.porcentaje DESC, fi.id
            """, new { empresaId, libro, desde = p.Inicio, hasta = p.Fin }, cancellationToken: ct))).AsList();

        // Criterio de caja: cobros y pagos de cada factura (los recibos devueltos restan)
        var caja = lineas.Where(l => l.CriterioCaja && l.PrimeraLinea).Select(l => l.FacturaId).ToArray();
        if (caja.Length > 0)
        {
            var cobros = (await cn.QueryAsync<CobroLibroFila>(new CommandDefinition("""
                SELECT v.factura_id, cp.fecha, cp.importe, fp.tipo AS forma, cb.iban
                FROM tes.cobro_pago cp JOIN tes.vencimiento v ON v.id = cp.vencimiento_id
                LEFT JOIN core.forma_pago fp ON fp.id = v.forma_pago_id
                LEFT JOIN tes.movimiento_bancario m ON m.id = cp.movimiento_id
                LEFT JOIN tes.cuenta_bancaria cb ON cb.id = m.cuenta_bancaria_id
                WHERE v.factura_id = ANY(@caja)
                UNION ALL
                SELECT v.factura_id, rv.fecha_devolucion, -cp.importe, 'RECIBO', NULL
                FROM tes.remesa_vencimiento rv JOIN tes.cobro_pago cp ON cp.id = rv.cobro_pago_id JOIN tes.vencimiento v ON v.id = cp.vencimiento_id
                WHERE rv.estado = 'DEVUELTO' AND v.factura_id = ANY(@caja)
                ORDER BY 2
                """, new { caja }, cancellationToken: ct))).ToLookup(c => c.FacturaId);
            foreach (var l in lineas.Where(l => l.CriterioCaja && l.PrimeraLinea))
                l.Cobros = cobros[l.FacturaId].Select(c => new CobroLibroDto(c.Fecha, c.Importe,
                    c.Forma switch { "TRANSFERENCIA" => "01", "CHEQUE" or "PAGARE" => "02", "RECIBO" => "05", _ => "04" }, c.Iban)).ToList();
        }

        var avisos = new List<string>();
        var borradores = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM fiscal.factura
            WHERE empresa_id = @empresaId AND libro = @libro AND estado = 'BORRADOR' AND fecha_contable BETWEEN @desde AND @hasta
            """, new { empresaId, libro, desde = p.Inicio, hasta = p.Fin }, cancellationToken: ct));
        if (borradores > 0)
            avisos.Add($"Hay {borradores} factura(s) en borrador con fecha contable en el periodo: no aparecen en el libro hasta que se aprueben.");
        var otros = lineas.Where(l => l.TipoOperacion != "INTERIOR").Select(l => LibrosRegistro.Texto(l.TipoOperacion)).Distinct().ToList();
        if (otros.Count > 0)
            avisos.Add($"Incluye operaciones {string.Join(", ", otros).ToLowerInvariant()}: revisa que el tipo de operación de cada línea es el correcto.");

        var facturas = lineas.Where(l => l.PrimeraLinea).ToList();
        return new LibroRegistroDto(libro, impuesto, p.Anio, p.Codigo, p.Inicio, p.Fin, facturas.Count, lineas, LibrosRegistro.Totales(lineas),
            lineas.Sum(l => l.Base), lineas.Sum(l => l.Cuota), lineas.Sum(l => l.CuotaRecargo), lineas.Sum(l => l.CuotaDeducible),
            facturas.Sum(l => l.Retencion ?? 0), facturas.Sum(l => l.TotalFactura ?? 0), avisos);
    }

    public async Task<(byte[] Contenido, string Nombre)> FormatoAeatAsync(long empresaId, int anio, string periodo, CancellationToken ct)
    {
        var exp = await ObtenerAsync(empresaId, "EMITIDAS", anio, periodo, ct);
        if (exp.Impuesto != "IVA")
            throw new ReglaContableException("El formato de la AEAT es para los libros de IVA; para el IGIC descarga el CSV.");
        var rec = await ObtenerAsync(empresaId, "RECIBIDAS", anio, periodo, ct);

        await using var cn = await conexiones.AbrirAsync(ct);
        var e = await cn.QuerySingleAsync<(string Nombre, string Nif)>(new CommandDefinition(
            "SELECT e.nombre, n.numero FROM core.empresa e JOIN core.nif n ON n.id = e.nif_id WHERE e.id = @empresaId",
            new { empresaId }, cancellationToken: ct));
        var contenido = LibrosAeatXlsx.Generar(e.Nif, e.Nombre, exp.Anio,
            FormatoAeat.Filas("EMITIDAS", exp.Anio, exp.Periodo, exp.Lineas), FormatoAeat.Filas("RECIBIDAS", rec.Anio, rec.Periodo, rec.Lineas));
        return (contenido, $"librosiva_{e.Nif}_{exp.Anio}_{exp.Periodo}.xlsx");
    }
}
