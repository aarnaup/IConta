"""Genera DisenosAeat.cs (diseños de registro de la AEAT) a partir de los datos de OCA l10n-spain."""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from oca import load

# (módulo OCA, config principal, modelo, desde, hasta, nombre C#)
MODELOS = [
    ("l10n_es_aeat_mod303", "aeat_mod303_2024_10_main_export_config", "303", "2025-01-01", "2025-12-31", "M303_2025"),
    ("l10n_es_aeat_mod303", "aeat_mod303_2026_02_main_export_config", "303", "2026-01-01", None, "M303_2026"),
    ("l10n_es_aeat_mod111", "aeat_mod111_main_export_config", "111", "2016-01-01", None, "M111"),
    ("l10n_es_aeat_mod115", "aeat_mod115_main_export_config", "115", "2016-01-01", None, "M115"),
    ("l10n_es_aeat_mod130", "aeat_mod130_export_config", "130", "2015-01-01", None, "M130"),
    ("l10n_es_aeat_mod347", "aeat_mod347_main_export_config", "347", "2016-01-01", None, "M347"),
    ("l10n_es_aeat_mod349", "aeat_mod349_main_export_config", "349", "2016-01-01", None, "M349"),
    ("l10n_es_aeat_mod190", "aeat_mod190_2025_main_export_config", "190", "2025-01-01", None, "M190"),
    ("l10n_es_aeat_mod390", "aeat_mod390_2025_main_export_config", "390", "2025-01-01", None, "M390"),
]

# Expresiones con lógica o nombres de OCA -> clave nuestra
MAPA = {
    "${object.year}": "D:ejercicio", "${object.report_id.year}": "D:ejercicio",
    "${object.period_type}": "D:periodo",
    "${object.company_vat}": "D:nif", "${object.report_id.company_vat}": "D:nif",
    "${object.company_id.name}": "D:nombre",
    "${object.company_id.name if object.company_vat[0].isdigit() else ''}": "D:nombrePersonaFisica",
    "${object.result_type}": "D:tipo", "${object.tipo_declaracion}": "D:tipo",
    "${object.devolucion_mensual}": "D:devolucionMensual", "${object.is_voluntary_sii}": "D:siiVoluntario",
    "${0 if object.period_type not in ('4T', '12') else int(object.exonerated_390)}": "D:exonerado390",
    "${0 if (object.period_type not in ('4T', '12') or object.exonerated_390 == '2') else (1 if object.has_operation_volume else 2)}": "D:volumenOperaciones",
    "${'' if object.year == 2026 and object.period_type == '01' else (0 if object.period_type in ('1T','2T','3T','4T') else (1 if object.deduct_payment_petrol_delivery else 2))}": "D:pagoCuentaCarburantes",
    "${object.total_devengado}": "C:27", "${object.total_deducir}": "C:45",
    "${object.porcentaje_atribuible_estado}": "C:65", "${object.atribuible_estado}": "C:66",
    "${object.potential_cuota_compensar}": "C:110", "${object.cuota_compensar}": "C:78",
    "${object.remaining_cuota_compensar}": "C:87", "${object.regularizacion_anual}": "C:68",
    "${object.previous_result}": "C:70", "${object.resultado_liquidacion}": "C:71", "${object.result}": "C:19",
    "${object.result_type == 'N'}": "D:sinActividad",
    "${object.statement_type == 'C'}": "D:complementaria", "${object.statement_type == 'S'}": "D:sustitutiva",
    "${object.previous_number}": "D:justificanteAnterior",
    "${object.previous_number if object.statement_type in ('C', 'S') else ''}": "D:justificanteAnterior",
    "${object.previous_number if object.statement_type == 'C' else ''}": "D:justificanteAnterior",
    "${False}": None, "${object.colegio_concertado}": None,
    "${object.has_347}": "D:presenta347",
    "${object.main_activity_code.code}": "D:actividad1Clave", "${object.main_activity_iae or ''}": "D:actividad1Iae",
    "${object.other_first_activity_code.code}": "D:actividad2Clave", "${object.other_first_activity_iae or ''}": "D:actividad2Iae",
    "${object.other_second_activity_code.code}": "D:actividad3Clave", "${object.other_second_activity_iae or ''}": "D:actividad3Iae",
    "${object.other_third_activity_code.code}": "D:actividad4Clave", "${object.other_third_activity_iae or ''}": "D:actividad4Iae",
    "${object.other_fourth_activity_code.code}": "D:actividad5Clave", "${object.other_fourth_activity_iae or ''}": "D:actividad5Iae",
    "${object.other_fifth_activity_code.code}": "D:actividad6Clave", "${object.other_fifth_activity_iae or ''}": "D:actividad6Iae",
    "${object.result_type == 'X' and object.partner_bank_id.bank_bic or ''}": "D:bicExtranjero",
    "${object.result_type in ['U', 'X', 'D'] and object.partner_bank_id and object.partner_bank_id.acc_number.replace(' ', '') or ''}": "D:iban",
    "${object.result_type == 'X' and object.partner_bank_id.bank_id.name or ''}": "D:bancoExtranjero",
    "${object.result_type == 'X' and object.partner_bank_id.bank_id.street or ''}": None,
    "${object.result_type == 'X' and object.partner_bank_id.bank_id.city or ''}": None,
    "${object.result_type == 'X' and object.partner_bank_id.bank_id.country.code or ''}": None,
    "${object.result_type in ['X', 'D'] and int(object.marca_sepa) or 0}": "D:marcaSepa",
    '${object.partner_bank_id and object.partner_bank_id.acc_number and object.partner_bank_id.acc_number.replace(" ", "") or ""}': "D:iban",
    '${object.partner_bank_id.sanitized_acc_number or ""}': "D:iban",
    # 347 / 349 / 190: cabecera
    "${object.support_type}": "D:soporte", "${object.contact_phone}": "D:telefono", "${object.contact_name}": "D:contacto",
    "${object.contact_email}": "D:email", "${object.name}": "D:numeroDeclaracion",
    "${' S' if object.statement_type == 'S' else object.statement_type.replace('N', ' ')}": "D:complementaria347",
    "${object.statement_type.replace('N', ' ')}": "D:complementaria349",
    "${object.total_partner_records}": "D:numeroRegistros", "${object.total_amount}": "D:importeTotal",
    "${object.total_real_estate_records}": "D:numeroInmuebles", "${object.total_real_estate_amount}": "D:importeInmuebles",
    "${object.representative_vat}": "D:nifRepresentante",
    "${object.total_partner_records_amount}": "C:02", "${object.total_partner_refunds}": "C:03",
    "${object.total_partner_refunds_amount}": "C:04",
    # Registros de detalle
    "${object.partner_vat}": "I:nif", "${object.partner_id.name}": "I:nombre", "${object.representante_legal_vat}": "I:nifRepresentante",
    "${object.partner_state_code}": "I:provincia", "${object.codigo_provincia}": "I:provincia",
    "${object.partner_country_code if object.partner_state_code == '99' else ''}": "I:pais",
    "${object.operation_key}": "I:clave", "${object.amount}": "I:importe",
    "${object.insurance_operation}": None, "${object.bussiness_real_estate_rent}": None,
    "${object.cash_amount}": "I:metalico", "${object.real_estate_transmissions_amount}": None,
    "${object.origin_year or object.report_id.year if object.cash_amount else '0000'}": "I:ejercicioMetalico",
    "${object.first_quarter}": "I:t1", "${object.second_quarter}": "I:t2", "${object.third_quarter}": "I:t3", "${object.fourth_quarter}": "I:t4",
    "${object.first_quarter_real_estate_transmission}": None, "${object.second_quarter_real_estate_transmission}": None,
    "${object.third_quarter_real_estate_transmission}": None, "${object.fourth_quarter_real_estate_transmission}": None,
    "${object.community_vat}": "I:nifComunitario", "${object.cash_basis_operation}": "I:criterioCaja",
    "${object.tax_person_operation}": "I:inversionSujetoPasivo", "${object.related_goods_operation}": None,
    "${object.bdns_number if object.bdns_number else '000000' if object.report_id.year > 2024 else '      '}": "D:bdns",
    "${object.total_operation_amount}": "I:base", "${object.total_origin_amount}": "I:baseAnterior",
    "${object.aeat_perception_key_id.code}": "I:clave", "${object.aeat_perception_subkey_id.name or '00'}": "I:subclave",
    "${'N' if object.percepciones_dinerarias < 0 else ' '}": "I:signoBase",
    "${object.percepciones_dinerarias}": "I:base", "${object.retenciones_dinerarias}": "I:retencion",
    "${'N' if object.percepciones_en_especie < 0 else ' '}": "I:signoEspecie",
    "${object.percepciones_en_especie}": "I:especie",
    "${object.ingresos_a_cuenta_efectuados}": "I:ingresoCuenta", "${object.ingresos_a_cuenta_repercutidos}": "I:ingresoCuentaRepercutido",
    "${object.a_nacimiento}": "I:anioNacimiento", "${object.situacion_familiar}": "I:situacionFamiliar",
    "${object.nif_conyuge}": "I:nifConyuge", "${object.discapacidad}": "I:discapacidad",
    "${object.contrato_o_relacion}": "I:contrato", "${object.movilidad_geografica}": "I:movilidad",
    "${object.gastos_deducibles}": "I:gastosDeducibles",
    "${'N' if object.percepciones_dinerarias_incap < 0 else ' '}": None,
    "${'0' if object.report_id.year > 2023 else ''}": "D:cero",
    # 390: datos estadísticos y representantes
    "${object.main_activity}": "D:actividad1Nombre", "${object.main_activity_code_id.code}": "D:actividad1Clave",
    "${object.main_activity_iae}": "D:actividad1Iae",
    "${object.previous_number if object.statement_type in ('S', 'SF') else ''}": "D:justificanteAnterior",
}
# Expresiones que se resuelven siempre igual
FIJOS = {
    "${object.other_first_activity_code_id.code or '0'}": "0", "${object.other_second_activity_code_id.code or '0'}": "0",
    "${object.other_third_activity_code_id.code or '0'}": "0", "${object.other_fourth_activity_code_id.code or '0'}": "0",
    "${object.other_fifth_activity_code_id.code or '0'}": "0",
    "${object._get_formatted_date(object.first_representative_date) or '00000000'}": "00000000",
    "${object._get_formatted_date(object.second_representative_date) or '00000000'}": "00000000",
    "${object._get_formatted_date(object.third_representative_date) or '00000000'}": "00000000",
}
# 390: casillas que OCA deja a cero y aquí se calculan (salvo apartados que no se gestionan)
SECCIONES_SIN_CALCULO_390 = ("8. Tributación", "13. Reg. Deducc", "12. Prorratas - 3", "12. Prorratas - 4", "12. Prorratas - 5",
                             "6. Operaciones Reg. Simplificado", "Agricultura", "agric.")
# En los registros de detalle del 349 rectificado, «year/period» son los del periodo que se corrige
MAPA_RECT = {"${object.year}": "I:ejercicio", "${object.period_type}": "I:periodo"}
COLECCIONES = {"object.partner_record_ids": "R:registros", "object.real_estate_record_ids": "R:inmuebles",
               "object.partner_refund_ids": "R:rectificaciones"}
CONDICIONES = {"True": None, "False": "D:nunca", "object.period_type in ('4T', '12') and object.exonerated_390 == '1'": "D:pagina4"}

def clave(expr, cid):
    if expr is None: return None
    if cid.endswith("partner_refund_export_config") and expr in MAPA_RECT: return MAPA_RECT[expr]
    if expr in MAPA: return MAPA[expr]
    m = re.fullmatch(r"\$\{(?:sum\()?object\.tax_line_ids\.filtered\(lambda r: r\.field_number\s*==\s*(\d+)\)(?:\.amount|\.mapped\('amount'\)\))\}", expr)
    if m: return f"C:{int(m.group(1)):02d}"
    m = re.fullmatch(r"\$\{object\.casilla_(\d+)\}", expr)
    if m: return f"C:{int(m.group(1)):02d}"
    m = re.fullmatch(r"\$\{object\.\w+\}", expr)
    if m and ("190" in cid or "real_state" in cid or "mod390" in cid): return None   # datos personales del perceptor que no gestionamos: van a cero o en blanco
    raise SystemExit(f"Sin traducir en {cid}: {expr!r}")

def cs(s): return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

SUSTITUCIONES = [   # (texto en el nombre del campo, clave): datos que OCA deja fijos y aquí se rellenan
    ("Versión del", "D:versionPrograma"), ("NIF Empresa Desarrollo", "D:nifDesarrollador"),
    ("criterio de Caja (art. 163", "D:criterioCaja"), ("destinatario de operaciones acogidas al régimen especial del criterio de caja", "D:destinatarioCaja"),
    ("exclusivamente en Régimen Simplificado", "D:regimenSimplificado"),
]

def campo(l, cid):
    for texto, k in SUSTITUCIONES:
        if texto.lower() in l["name"].lower() and l["type"] == "string":
            l = dict(l, expr="${__" + k + "}", fixed=None)
            MAPA["${__" + k + "}"] = k
    t = {"string": "Alfanumerico", "alphabetic": "Alfabetico", "float": "Numerico", "integer": "Numerico", "boolean": "Booleano"}[l["type"]]
    args = [cs(l["name"][:120]), f"TipoCampoAeat.{t}", str(l["size"])]
    named = []
    if l["type"] == "float" and l["dec"]: named.append(f"Decimales: {l['dec']}")
    sign = True if l["sign"] is None else l["sign"]
    if t == "Numerico" and sign:
        named.append("Signo: true")
        if l["pos"] and l["pos"] != "0": named.append(f"Positivo: '{l['pos']}'")
        if l["neg"] and l["neg"] != "N": named.append(f"Negativo: '{l['neg']}'")
    if t == "Booleano":
        if l["yes"] and l["yes"] != "X": named.append(f"Si: '{l['yes']}'")
        if l["no"] and l["no"] != " ": named.append(f"No: '{l['no']}'")
    if l["align"] == "right" and t in ("Alfanumerico", "Alfabetico"): named.append("Derecha: true")
    e = l["expr"]
    if e in FIJOS: e = FIJOS[e]
    if "190" in cid and "derivadas de incapacidad" in l["name"] and "no derivadas" not in l["name"]:
        e = None                                 # OCA repite aquí las expresiones de la especie sin incapacidad
    if "mod390" in cid and l["type"] == "float" and (l["fixed"] in ("0", None)) and not e \
            and not any(x in l["name"] for x in SECCIONES_SIN_CALCULO_390):
        m390 = re.search(r"\[(\d+)\]\s*\.?$", l["name"].strip())
        if m390: e = "${object.casilla_" + m390.group(1) + "}"
    if "mod390" in cid and l["type"] == "string" and re.search(r"Prorratas - ([12]) - Tipo de prorrata", l["name"]):
        e = "${__D:prorrata" + re.search(r"Prorratas - ([12])", l["name"]).group(1) + "Tipo}"
        MAPA[e] = e[4:-1]
    if e and ("\\r\\n" in e):
        return f"new CampoAeat({args[0]}, TipoCampoAeat.Alfanumerico, 2, Literal: \"\\r\\n\")"
    m0 = re.match(r"\$\{'(\d{5})' if object\.year > 2024", e or "")
    if m0:                                       # diseño de 2025: rama «year > 2024», un tipo fijo
        e = m0.group(1)
    if e and re.fullmatch(r"[^$]*", e):          # expresión sin ${}: es un valor fijo
        named.append(f"Fijo: {cs(e)}")
    elif e and not e.startswith("${"):           # plantilla con ${…} dentro de texto
        plantilla = re.sub(r"\$\{([^}]*)\}", lambda m: "{" + (clave("${" + m.group(1) + "}", cid) or "") + "}", e)
        named.append(f"Plantilla: {cs(plantilla)}")
    elif e:
        k = clave(e, cid)
        if k: named.append(f"Clave: {cs(k)}")
    elif l["fixed"]:
        named.append(f"Fijo: {cs(l['fixed'])}")
    return f"new CampoAeat({', '.join(args + named)})"

def bloque(lines, cid, ind, configs):
    out = []
    for l in lines.get(cid, []):
        if l["type"] == "subconfig":
            if not l["sub"]: continue
            cond = CONDICIONES.get(l["cond"], "?") if l["cond"] else None
            if cond == "?": raise SystemExit(f"Condición sin traducir: {l['cond']!r}")
            col = COLECCIONES.get(l["rep"]) if l["rep"] else None
            if l["rep"] and not col: raise SystemExit(f"Repetición sin traducir: {l['rep']!r}")
            extra = (f", Condicion: {cs(cond)}" if cond else "") + (f", Coleccion: {cs(col)}" if col else "")
            out.append(f"{ind}new BloqueAeat({cs(l['name'][:120])}, [\n{bloque(lines, l['sub'], ind + '    ', configs)}\n{ind}]{extra}),")
        elif l["size"] == 0:
            continue                                  # páginas omitidas (tamaño 0)
        else:
            out.append(f"{ind}{campo(l, cid)},")
    return "\n".join(out)

cab = '''// <auto-generated>
// Diseños de registro de la AEAT para la presentación por fichero. Generado a partir de las definiciones publicadas por la
// comunidad OCA (github.com/OCA/l10n-spain, módulos l10n_es_aeat_mod*, rama 17.0), que reproducen los diseños oficiales de la
// Agencia Tributaria. Las expresiones se han traducido a las claves de esta aplicación («C:nn» casilla, «D:x» dato de la
// declaración, «I:x» dato de cada registro repetido). No editar a mano: regenerar.
// </auto-generated>
namespace Contabilidad.Domain.Fiscal.Presentacion;

public static class DisenosAeat
{
'''
partes = [cab]
nombres = []
for mod, main, modelo, desde, hasta, nombre in MODELOS:
    configs, lines = load(mod)
    d = desde.split("-"); h = hasta.split("-") if hasta else None
    hasta_cs = f"new DateOnly({int(h[0])}, {int(h[1])}, {int(h[2])})" if h else "null"
    partes.append(f"    public static readonly DisenoAeat {nombre} = new(\"{modelo}\", new DateOnly({int(d[0])}, {int(d[1])}, {int(d[2])}), {hasta_cs}, [\n")
    partes.append(bloque(lines, main, "        ", configs) + "\n    ]);\n\n")
    nombres.append(nombre)
partes.append(f"    public static readonly IReadOnlyList<DisenoAeat> Todos = [{', '.join(nombres)}];\n}}\n")
open(sys.argv[1], "w").write("".join(partes))
print("ok", nombres)
