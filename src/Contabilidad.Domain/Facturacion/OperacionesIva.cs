namespace Contabilidad.Domain.Facturacion;

/// <summary>
/// Tipos de operación de una línea de IVA/IGIC y su tratamiento:
/// <list type="bullet">
/// <item>Emitidas: INTERIOR, EXENTA, NO_SUJETA, INTRACOM_ENTREGA (exenta art. 25), EXPORTACION (exenta art. 21) e
/// INVERSION_SUJETO_PASIVO (la cuota la declara el cliente): las tres últimas sin cuota.</item>
/// <item>Recibidas: INTERIOR, EXENTA, NO_SUJETA, IMPORTACION (IVA liquidado en aduana, DUA), e INTRACOM_ADQUISICION e
/// INVERSION_SUJETO_PASIVO, que se <b>autorrepercuten</b>: la empresa devenga la cuota (477) y se la deduce (472) según su
/// regla de deducción; el proveedor cobra solo la base.</item>
/// </list>
/// </summary>
public static class OperacionesIva
{
    public static readonly IReadOnlySet<string> Emitidas =
        new HashSet<string> { "INTERIOR", "EXENTA", "NO_SUJETA", "INTRACOM_ENTREGA", "EXPORTACION", "INVERSION_SUJETO_PASIVO" };
    public static readonly IReadOnlySet<string> Recibidas =
        new HashSet<string> { "INTERIOR", "EXENTA", "NO_SUJETA", "IMPORTACION", "INTRACOM_ADQUISICION", "INVERSION_SUJETO_PASIVO" };

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
    }

    /// <summary>La empresa declara la cuota como devengada y como soportada (no la paga al proveedor).</summary>
    public static bool Autorrepercutida(LibroFactura libro, string tipo)
        => libro == LibroFactura.Recibidas && tipo is "INTRACOM_ADQUISICION" or "INVERSION_SUJETO_PASIVO";

    /// <summary>La línea lleva cuota: interiores, importaciones y autorrepercutidas.</summary>
    public static bool LlevaCuota(LibroFactura libro, string tipo)
        => tipo is "INTERIOR" || (libro == LibroFactura.Recibidas && tipo is "IMPORTACION" or "INTRACOM_ADQUISICION" or "INVERSION_SUJETO_PASIVO");
}
