using System.Globalization;

namespace Contabilidad.Domain.Fiscal;

/// <summary>Columna del formato AEAT: las que tienen grupo comparten la cabecera de la fila 6 y llevan su título en la fila 7.</summary>
public sealed record ColumnaAeat(string? Grupo, string Titulo, bool Importe = false);

/// <summary>
/// Libros registro de IVA en el formato de la AEAT (hojas «EXPEDIDAS» y «RECIBIDAS» del modelo publicado por la Agencia
/// para la llevanza y la aportación de los libros, Orden HAP/1626/2016). Aquí solo se decide qué valor va en cada columna;
/// la hoja de cálculo la escribe la capa de infraestructura. Fechas como texto dd/mm/aaaa y los importes como números.
/// </summary>
public static class FormatoAeat
{
    public const int FilaGrupos = 6, FilaTitulos = 7, PrimeraFilaDatos = 8;

    private const string Autoliq = "Autoliquidación", Actividad = "Actividad";

    public static readonly IReadOnlyList<ColumnaAeat> Expedidas =
    [
        new(Autoliq, "Ejercicio"), new(Autoliq, "Periodo"),
        new(Actividad, "Código"), new(Actividad, "Tipo"), new(Actividad, "Grupo o Epígrafe del IAE"),
        new(null, "Tipo de factura"), new(null, "Concepto de Ingreso"), new(null, "Ingreso Computable", true),
        new(null, "Fecha Expedición"), new(null, "Fecha Operación"),
        new("Identificación de la Factura", "Serie"), new("Identificación de la Factura", "Número"), new("Identificación de la Factura", "Número-Final"),
        new("NIF Destinatario", "Tipo"), new("NIF Destinatario", "Código País"), new("NIF Destinatario", "Identificación"),
        new(null, "Nombre Destinatario"), new(null, "Clave de Operación"), new(null, "Calificación de la operación"), new(null, "Operación Exenta"),
        new(null, "Total Factura", true), new(null, "Base Imponible", true), new(null, "Tipo de IVA", true), new(null, "Cuota IVA Repercutida", true),
        new(null, "Tipo de Recargo Eq.", true), new(null, "Cuota Recargo Eq.", true),
        new(Cobro, "Fecha"), new(Cobro, "Importe", true), new(Cobro, "Medio Utilizado"), new(Cobro, "Identificación Medio Utilizado"),
        new(null, "Tipo de Retención del IRPF", true), new(null, "Importe Retenido del IRPF", true),
    ];

    public static readonly IReadOnlyList<ColumnaAeat> Recibidas =
    [
        new(Autoliq, "Ejercicio"), new(Autoliq, "Periodo"),
        new(Actividad, "Código"), new(Actividad, "Tipo"), new(Actividad, "Grupo o Epígrafe del IAE"),
        new(null, "Tipo de factura"), new(null, "Concepto de Gasto"), new(null, "Gasto Deducible", true),
        new(null, "Fecha Expedición"), new(null, "Fecha Operación"),
        new("Identificación Factura del Expedidor", "(Serie-Número)"), new("Identificación Factura del Expedidor", "Número-Final"),
        new(null, "Fecha Recepción"), new(null, "Número Recepción"), new(null, "Número Recepción Final"),
        new("NIF Expedidor", "Tipo"), new("NIF Expedidor", "Código País"), new("NIF Expedidor", "Identificación"),
        new(null, "Nombre Expedidor"), new(null, "Clave de Operación"), new(null, "Bien de Inversión"), new(null, "Inversión del Sujeto Pasivo"),
        new(null, "Deducible en Periodo Posterior"),
        new("Periodo Deducción", "Ejercicio"), new("Periodo Deducción", "Periodo"),
        new(null, "Total Factura", true), new(null, "Base Imponible", true), new(null, "Tipo de IVA", true), new(null, "Cuota IVA Soportado", true),
        new(null, "Cuota Deducible", true), new(null, "Tipo de Recargo Eq.", true), new(null, "Cuota Recargo Eq.", true),
        new(Pago, "Fecha"), new(Pago, "Importe", true), new(Pago, "Medio Utilizado"), new(Pago, "Identificación Medio Utilizado"),
        new(null, "Tipo de Retención del IRPF", true), new(null, "Importe Retenido del IRPF", true), new(null, "Registro Acuerdo Facturación"),
    ];

    private const string Cobro = "Cobro (Operación Criterio de Caja)", Pago = "Pago (Operación Criterio de Caja)";

    /// <summary>Filas de datos de la hoja; <paramref name="periodo"/> es el pedido (mensual, trimestral o anual).</summary>
    public static IReadOnlyList<object?[]> Filas(string libro, int anio, string periodo, IEnumerable<LineaLibroDto> lineas)
    {
        var emitidas = libro == "EMITIDAS";
        var filas = new List<object?[]>();
        foreach (var l in lineas)
        {
            var (tipoId, pais, ident) = Identificacion(l);
            var per = Periodo(periodo, l.FechaContable);
            var fechaOp = l.FechaOperacion is { } fo && fo != l.FechaExpedicion ? Fecha(fo) : null;
            object?[] f;
            if (emitidas)
            {
                f =
                [
                    anio.ToString(CultureInfo.InvariantCulture), per, null, null, null, TipoFactura(l.TipoFactura, emitidas), null, null,
                    Fecha(l.FechaExpedicion), fechaOp, Corta(l.Serie, 20), Corta(l.Numero, 20), null,
                    tipoId, pais, ident, Corta(l.Nombre, 40), null, Calificacion(l.TipoOperacion), Exencion(l.TipoOperacion),
                    l.PrimeraLinea ? l.TotalFactura : null, l.Base, l.Porcentaje, l.Cuota,
                    Recargo(l.PorcentajeRecargo, l.CuotaRecargo), Recargo(l.PorcentajeRecargo, l.CuotaRecargo) is null ? null : l.CuotaRecargo,
                    null, null, null, null,
                    null, l.PrimeraLinea && l.Retencion is { } r && r != 0 ? r : null,
                ];
            }
            else
            {
                var serieNumero = Corta(string.Concat(l.Serie ?? "", l.Numero), 40);
                f =
                [
                    anio.ToString(CultureInfo.InvariantCulture), per, null, null, null, TipoFactura(l.TipoFactura, emitidas), null, null,
                    Fecha(l.FechaExpedicion), fechaOp, serieNumero, null,
                    Fecha(l.FechaContable), l.NumeroRegistro?.ToString(CultureInfo.InvariantCulture), null,
                    tipoId, pais, ident, Corta(l.Nombre, 40), null,
                    l.BienInversion ? "S" : "N", l.TipoOperacion == "INVERSION_SUJETO_PASIVO" ? "S" : "N", "N",
                    l.FechaContable.Year.ToString(CultureInfo.InvariantCulture), per,
                    l.PrimeraLinea ? l.TotalFactura : null, l.Base, l.Porcentaje, l.Cuota, l.CuotaDeducible,
                    Recargo(l.PorcentajeRecargo, l.CuotaRecargo), Recargo(l.PorcentajeRecargo, l.CuotaRecargo) is null ? null : l.CuotaRecargo,
                    null, null, null, null,
                    null, l.PrimeraLinea && l.Retencion is { } r && r != 0 ? r : null, null,
                ];
            }
            // Criterio de caja: el primer cobro/pago en la línea; los siguientes, en filas que repiten la identificación de la factura
            var col = emitidas ? 26 : 32;
            for (var i = 0; i < l.Cobros.Count; i++)
            {
                var c = l.Cobros[i];
                var destino = f;
                if (i > 0)
                {
                    destino = new object?[f.Length];
                    Array.Copy(f, destino, emitidas ? 20 : 25);
                }
                destino[col] = Fecha(c.Fecha);
                destino[col + 1] = c.Importe;
                destino[col + 2] = c.Medio;
                destino[col + 3] = c.Cuenta;
                if (i > 0) filas.Add(destino);
                if (i == 0) filas.Add(f);
            }
            if (l.Cobros.Count == 0) filas.Add(f);
        }
        return filas;
    }

    /// <summary>Periodo de liquidación de la línea: el mes si se pide un mes; si no, el trimestre de la fecha contable.</summary>
    public static string Periodo(string periodoPedido, DateOnly fecha)
        => periodoPedido.Length == 2 && char.IsAsciiDigit(periodoPedido[0]) && char.IsAsciiDigit(periodoPedido[1]) && periodoPedido != "0A"
            ? fecha.Month.ToString("00", CultureInfo.InvariantCulture)
            : $"{(fecha.Month - 1) / 3 + 1}T";

    /// <summary>F1 factura, F2 simplificada, R1 rectificativa, F5 importación (DUA, solo en recibidas).</summary>
    public static string TipoFactura(string tipo, bool emitidas) => tipo switch
    {
        "SIMPLIFICADA" => "F2",
        "RECTIFICATIVA" => "R1",
        "IMPORTACION" when !emitidas => "F5",
        _ => "F1",
    };

    /// <summary>S1 sujeta y no exenta, S2 con inversión del sujeto pasivo, N1 no sujeta (arts. 7, 14 y otros), N2 no sujeta por
    /// reglas de localización; las exentas van en «Operación Exenta».</summary>
    public static string? Calificacion(string tipoOperacion) => tipoOperacion switch
    {
        "INVERSION_SUJETO_PASIVO" => "S2",
        "NO_SUJETA" => "N1",
        "NO_SUJETA_LOCAL" or "NO_SUJETA_OSS" => "N2",
        "EXENTA" or "EXPORTACION" or "INTRACOM_ENTREGA" => null,
        _ => "S1",
    };

    /// <summary>E1 art. 20 LIVA, E2 art. 21 (exportaciones), E5 art. 25 (entregas intracomunitarias).</summary>
    public static string? Exencion(string tipoOperacion) => tipoOperacion switch
    {
        "EXENTA" => "E1",
        "EXPORTACION" => "E2",
        "INTRACOM_ENTREGA" => "E5",
        _ => null,
    };

    /// <summary>
    /// Españoles: solo el NIF (tipo y país en blanco). Extranjeros: 02 NIF-IVA (comunitarios), 03 pasaporte,
    /// 04 documento oficial del país de residencia.
    /// </summary>
    public static (string? Tipo, string? Pais, string Identificacion) Identificacion(LineaLibroDto l)
    {
        var pais = string.IsNullOrWhiteSpace(l.PaisIso2) ? "ES" : l.PaisIso2.Trim().ToUpperInvariant();
        if (pais == "ES") return (null, null, l.Nif);
        var tipo = l.TipoDocumento switch
        {
            "PASAPORTE" => "03",
            "VIES" => "02",
            _ => l.PaisUe ? "02" : "04",
        };
        return (tipo, pais, l.Nif);
    }

    private static decimal? Recargo(decimal porcentaje, decimal cuota) => porcentaje != 0 || cuota != 0 ? porcentaje : null;
    private static string Fecha(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static string? Corta(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
