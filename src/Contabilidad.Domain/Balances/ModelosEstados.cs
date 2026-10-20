namespace Contabilidad.Domain.Balances;

/// <summary>
/// Una partida de un estado financiero.
/// <para><see cref="Cuentas"/>: prefijos de cuenta separados por espacios. Una cuenta va a la partida cuyo prefijo coincida con más
/// dígitos. Sufijo <c>:D</c> o <c>:A</c>: solo si el saldo es deudor o acreedor (cuentas corrientes que pueden ser activo o pasivo).</para>
/// <para><see cref="Suma"/>: identificadores de otras partidas que se suman (subtotales y totales).</para>
/// <para><see cref="Acreedora"/>: la partida muestra el saldo acreedor en positivo (patrimonio neto, pasivo, ingresos).
/// Las cuentas correctoras restan solas, por su saldo de signo contrario.</para>
/// </summary>
public sealed record PartidaModelo(string Id, string Texto, int Nivel, bool Acreedora, string? Cuentas = null, string? Suma = null)
{
    public bool EsTotal => Suma is not null;
}

/// <param name="Alcance">Prefijos de las cuentas que entran en el estado; el resto se ignora (p. ej. la PyG solo mira 6 y 7).</param>
public sealed record ModeloEstado(string Codigo, string Nombre, string[] Alcance, IReadOnlyList<PartidaModelo> Partidas);

/// <summary>
/// Modelos de cuentas anuales en formato PYMES/abreviado (RD 1515/2007), válidos para el PGC de PYMES y para el normal
/// en sus modelos abreviados. La numeración y la asignación de cuentas siguen las del modelo oficial; conviene contrastarlas
/// antes de depositar las cuentas.
/// </summary>
public static class ModelosEstados
{
    public static readonly ModeloEstado Balance = new("BALANCE", "Balance de situación", [""],
    [
        // ---------------------------------------------------------------- ACTIVO
        new("A", "A) ACTIVO NO CORRIENTE", 0, false, Suma: "AI AII AIII AIV AV AVI"),
        new("AI", "I. Inmovilizado intangible", 1, false, "20 280 290"),
        new("AII", "II. Inmovilizado material", 1, false, "21 23 281 291"),
        new("AIII", "III. Inversiones inmobiliarias", 1, false, "22 282 292"),
        new("AIV", "IV. Inversiones en empresas del grupo y asociadas a largo plazo", 1, false,
            "2403 2404 2413 2414 2423 2424 2493 2494 293 2943 2944 2953 2954"),
        new("AV", "V. Inversiones financieras a largo plazo", 1, false,
            "2405 2415 2425 2495 250 251 252 253 254 255 257 258 259 26 2935 2945 2955 296 297 298"),
        new("AVI", "VI. Activos por impuesto diferido", 1, false, "474"),
        new("B", "B) ACTIVO CORRIENTE", 0, false, Suma: "BI BII BIII BIV BV BVI"),
        new("BI", "I. Existencias", 1, false, "30 31 32 33 34 35 36 39 407"),
        new("BII", "II. Deudores comerciales y otras cuentas a cobrar", 1, false, Suma: "BII1 BII2 BII3"),
        new("BII1", "1. Clientes por ventas y prestaciones de servicios", 2, false, "430 431 432 433 434 435 436 437 490 493"),
        new("BII2", "2. Accionistas (socios) por desembolsos exigidos", 2, false, "558"),
        new("BII3", "3. Otros deudores", 2, false, "44 460 470 471 472 473 544 555:D"),
        new("BIII", "III. Inversiones en empresas del grupo y asociadas a corto plazo", 1, false,
            "5303 5304 5313 5314 5323 5324 5333 5334 5343 5344 5353 5354 5393 5394 5523:D 5524:D 593 5943 5944 5953 5954"),
        new("BIV", "IV. Inversiones financieras a corto plazo", 1, false,
            "5305 5315 5325 5335 5345 5355 5395 540 541 542 543 545 546 547 548 549 550:D 551:D 552:D 5525:D 553:D 554:D " +
            "5590 5593 565 566 5935 5945 5955 596 597 598"),
        new("BV", "V. Periodificaciones a corto plazo", 1, false, "480 567"),
        new("BVI", "VI. Efectivo y otros activos líquidos equivalentes", 1, false, "57"),
        new("TA", "TOTAL ACTIVO (A + B)", 0, false, Suma: "A B"),

        // ---------------------------------------------------------------- PATRIMONIO NETO Y PASIVO
        new("PA", "A) PATRIMONIO NETO", 0, true, Suma: "PA1 PA2 PA3"),
        new("PA1", "A-1) Fondos propios", 1, true, Suma: "PA1I PA1II PA1III PA1IV PA1V PA1VI PA1VII PA1VIII PA1IX"),
        new("PA1I", "I. Capital", 2, true, Suma: "PA1I1 PA1I2"),
        new("PA1I1", "1. Capital escriturado", 3, true, "100 101 102"),
        new("PA1I2", "2. (Capital no exigido)", 3, true, "1030 1040"),
        new("PA1II", "II. Prima de emisión", 2, true, "110"),
        new("PA1III", "III. Reservas", 2, true, "112 113 114 115 119"),
        new("PA1IV", "IV. (Acciones y participaciones en patrimonio propias)", 2, true, "108 109"),
        new("PA1V", "V. Resultados de ejercicios anteriores", 2, true, "120 121"),
        new("PA1VI", "VI. Otras aportaciones de socios", 2, true, "118"),
        // Antes de regularizar, el resultado del ejercicio está en los grupos 6 y 7
        new("PA1VII", "VII. Resultado del ejercicio", 2, true, "129 6 7"),
        new("PA1VIII", "VIII. (Dividendo a cuenta)", 2, true, "557"),
        new("PA1IX", "IX. Otros instrumentos de patrimonio neto", 2, true, "111"),
        // Los grupos 8 y 9 (PGC normal) se trasladan al subgrupo 13 al cerrar; mientras tanto se muestran aquí
        new("PA2", "A-2) Ajustes en patrimonio neto", 1, true, "133 134 135 136 137 8 9"),
        new("PA3", "A-3) Subvenciones, donaciones y legados recibidos", 1, true, "130 131 132"),
        new("PB", "B) PASIVO NO CORRIENTE", 0, true, Suma: "PBI PBII PBIII PBIV PBV PBVI"),
        new("PBI", "I. Provisiones a largo plazo", 1, true, "14"),
        new("PBII", "II. Deudas a largo plazo", 1, true, Suma: "PBII1 PBII2 PBII3"),
        new("PBII1", "1. Deudas con entidades de crédito", 2, true, "1605 170"),
        new("PBII2", "2. Acreedores por arrendamiento financiero", 2, true, "1625 174"),
        new("PBII3", "3. Otras deudas a largo plazo", 2, true, "1615 1635 171 172 173 175 176 177 178 179 180 185 189"),
        new("PBIII", "III. Deudas con empresas del grupo y asociadas a largo plazo", 1, true, "1603 1604 1613 1614 1623 1624 1633 1634"),
        new("PBIV", "IV. Pasivos por impuesto diferido", 1, true, "479"),
        new("PBV", "V. Periodificaciones a largo plazo", 1, true, "181"),
        new("PBVI", "VI. Deuda con características especiales a largo plazo", 1, true, "15"),
        new("PC", "C) PASIVO CORRIENTE", 0, true, Suma: "PCI PCII PCIII PCIV PCV PCVI"),
        new("PCI", "I. Provisiones a corto plazo", 1, true, "499 529"),
        new("PCII", "II. Deudas a corto plazo", 1, true, Suma: "PCII1 PCII2 PCII3"),
        new("PCII1", "1. Deudas con entidades de crédito", 2, true, "5105 520 527"),
        new("PCII2", "2. Acreedores por arrendamiento financiero", 2, true, "5125 524"),
        new("PCII3", "3. Otras deudas a corto plazo", 2, true,
            "1034 1044 190 192 194 195 197 199 500 501 505 506 509 5115 5135 5145 521 522 523 525 526 528 " +
            "550:A 551:A 552:A 5525:A 553:A 554:A 555:A 5565 5566 5595 5598 560 561 569"),
        new("PCIII", "III. Deudas con empresas del grupo y asociadas a corto plazo", 1, true,
            "5103 5104 5113 5114 5123 5124 5133 5134 5143 5144 5523:A 5524:A 5563 5564"),
        new("PCIV", "IV. Acreedores comerciales y otras cuentas a pagar", 1, true, Suma: "PCIV1 PCIV2"),
        new("PCIV1", "1. Proveedores", 2, true, "400 401 403 404 405 406"),
        new("PCIV2", "2. Otros acreedores", 2, true, "41 438 465 466 475 476 477"),
        new("PCV", "V. Periodificaciones a corto plazo", 1, true, "485 568"),
        new("PCVI", "VI. Deuda con características especiales a corto plazo", 1, true, "502 507"),
        new("TP", "TOTAL PATRIMONIO NETO Y PASIVO (A + B + C)", 0, true, Suma: "PA PB PC"),
    ]);

    public static readonly ModeloEstado PerdidasGanancias = new("PYG", "Cuenta de pérdidas y ganancias", ["6", "7"],
    [
        new("1", "1. Importe neto de la cifra de negocios", 1, true, "700 701 702 703 704 705 706 708 709"),
        new("2", "2. Variación de existencias de productos terminados y en curso de fabricación", 1, true, "6930 71 7930"),
        new("3", "3. Trabajos realizados por la empresa para su activo", 1, true, "73"),
        new("4", "4. Aprovisionamientos", 1, true, "600 601 602 606 607 608 609 61 6931 6932 6933 7931 7932 7933"),
        new("5", "5. Otros ingresos de explotación", 1, true, "740 747 75"),
        new("6", "6. Gastos de personal", 1, true, "64 7950 7957"),
        new("7", "7. Otros gastos de explotación", 1, true, "62 631 634 636 639 65 694 695 794 7954"),
        new("8", "8. Amortización del inmovilizado", 1, true, "68"),
        new("9", "9. Imputación de subvenciones de inmovilizado no financiero y otras", 1, true, "746"),
        new("10", "10. Excesos de provisiones", 1, true, "7951 7952 7955 7956"),
        new("11", "11. Deterioro y resultado por enajenaciones del inmovilizado", 1, true, "670 671 672 690 691 692 770 771 772 790 791 792"),
        new("12", "12. Otros resultados", 1, true, "678 774 778"),
        new("RA", "A) RESULTADO DE EXPLOTACIÓN (1 a 12)", 0, true, Suma: "1 2 3 4 5 6 7 8 9 10 11 12"),
        new("13", "13. Ingresos financieros", 1, true, "760 761 762 767 769"),
        new("14", "14. Gastos financieros", 1, true, "660 661 662 664 665 669"),
        new("15", "15. Variación de valor razonable en instrumentos financieros", 1, true, "663 763"),
        new("16", "16. Diferencias de cambio", 1, true, "668 768"),
        new("17", "17. Deterioro y resultado por enajenaciones de instrumentos financieros", 1, true,
            "666 667 673 675 696 697 698 699 766 773 775 796 797 798 799"),
        new("RB", "B) RESULTADO FINANCIERO (13 a 17)", 0, true, Suma: "13 14 15 16 17"),
        new("RC", "C) RESULTADO ANTES DE IMPUESTOS (A + B)", 0, true, Suma: "RA RB"),
        new("18", "18. Impuestos sobre beneficios", 1, true, "630 633 638"),
        new("RD", "D) RESULTADO DEL EJERCICIO (C + 18)", 0, true, Suma: "RC 18"),
    ]);
}
