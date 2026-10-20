namespace Contabilidad.Domain.Facturacion;

/// <summary>
/// Tipos de operación de una línea de IVA/IGIC y su tratamiento:
/// <list type="bullet">
/// <item>Emitidas: INTERIOR, EXENTA, NO_SUJETA (arts. 7 y 14 LIVA), NO_SUJETA_LOCAL (no sujeta por las reglas de localización,
/// arts. 68 a 70: servicios a empresarios de fuera de la UE, bienes que están fuera…), NO_SUJETA_OSS (ventas a distancia y servicios a
/// particulares de la UE que se declaran en la ventanilla única), INTRACOM_ENTREGA (exenta art. 25), EXPORTACION (exenta art. 21) e
/// INVERSION_SUJETO_PASIVO (la cuota la declara el cliente): las tres últimas sin cuota.</item>
/// <item>Recibidas: INTERIOR, EXENTA, NO_SUJETA, IMPORTACION (IVA liquidado en aduana, DUA), e INTRACOM_ADQUISICION e
/// INVERSION_SUJETO_PASIVO, que se <b>autorrepercuten</b>: la empresa devenga la cuota (477) y se la deduce (472) según su
/// regla de deducción; el proveedor cobra solo la base.</item>
/// <item>COMPENSACION_REAGP: compra a un agricultor, ganadero o pescador del régimen especial (o, en la empresa acogida, su venta a un
/// empresario): no lleva IVA sino la compensación a tanto alzado (12 % agrícolas y forestales, 10,5 % ganaderas y pesqueras), que
/// el comprador paga y se deduce (casilla 42 del 303) y el agricultor cobra como más ingreso.</item>
/// </list>
/// </summary>
public static class OperacionesIva
{
    public static readonly IReadOnlySet<string> Emitidas =
        new HashSet<string> { "INTERIOR", "EXENTA", "NO_SUJETA", "NO_SUJETA_LOCAL", "NO_SUJETA_OSS", "INTRACOM_ENTREGA", "EXPORTACION", "INVERSION_SUJETO_PASIVO",
                              "COMPENSACION_REAGP" };
    public static readonly IReadOnlySet<string> Recibidas =
        new HashSet<string> { "INTERIOR", "EXENTA", "NO_SUJETA", "IMPORTACION", "INTRACOM_ADQUISICION", "INVERSION_SUJETO_PASIVO", "COMPENSACION_REAGP" };

    /// <summary>Comprueba la operación para el libro y el impuesto (en el IGIC no hay operaciones intracomunitarias).</summary>
    public static void Validar(LibroFactura libro, string tipo, string impuesto)
    {
        var validas = libro == LibroFactura.Emitidas ? Emitidas : Recibidas;
        if (!validas.Contains(tipo))
            throw new ReglaContableException($"El tipo de operación '{tipo}' no es válido en facturas {(libro == LibroFactura.Emitidas ? "emitidas" : "recibidas")}.",
                $"Valores: {string.Join(", ", validas)}.");
        if (impuesto == "IGIC" && tipo.StartsWith("INTRACOM", StringComparison.Ordinal))
            throw new ReglaContableException("Canarias no forma parte del territorio IVA de la UE: no hay operaciones intracomunitarias en el IGIC.",
                "Las compras a la Península o a la UE son importaciones y las ventas, exportaciones.");
        if (impuesto == "IGIC" && tipo == "COMPENSACION_REAGP")
            throw new ReglaContableException("La compensación del régimen especial de la agricultura, ganadería y pesca es del IVA.");
    }

    /// <summary>No sujeta: por los arts. 7 y 14 o por las reglas de localización (también las de la ventanilla única).</summary>
    public static bool NoSujeta(string tipo) => tipo is "NO_SUJETA" or "NO_SUJETA_LOCAL" or "NO_SUJETA_OSS";

    /// <summary>La empresa declara la cuota como devengada y como soportada (no la paga al proveedor).</summary>
    public static bool Autorrepercutida(LibroFactura libro, string tipo)
        => libro == LibroFactura.Recibidas && tipo is "INTRACOM_ADQUISICION" or "INVERSION_SUJETO_PASIVO";

    /// <summary>La línea lleva cuota: interiores, importaciones y autorrepercutidas.</summary>
    public static bool LlevaCuota(LibroFactura libro, string tipo)
        => tipo is "INTERIOR" or "COMPENSACION_REAGP" || (libro == LibroFactura.Recibidas && tipo is "IMPORTACION" or "INTRACOM_ADQUISICION" or "INVERSION_SUJETO_PASIVO");
}
