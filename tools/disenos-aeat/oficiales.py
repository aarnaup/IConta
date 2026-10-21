"""Diseños de registro de los modelos 131, 200, 202 y 309 a partir de los ficheros oficiales de la AEAT.

    python3 oficiales.py extraer 200 DR200e25.xls     # deja aeat/DR200.csv con las páginas que se usan (también 202, 131 y 309)
    python3 oficiales.py generar <DisenosAeatOficiales.cs>   # genera el C# desde los CSV

Los diseños se descargan de la Sede (Ayuda → Diseños de registro). Solo se extraen las páginas que rellena la aplicación:
identificación, balance, cuenta de pérdidas y ganancias, liquidación, bases negativas e ingreso/devolución (200); las dos páginas del
202; la liquidación y la domiciliación del 131 (sin el detalle de módulos por actividad); la página única del 309.
"""
import csv, os, re, sys

AQUI = os.path.dirname(os.path.abspath(__file__))
DIR = os.path.join(AQUI, "aeat")

# Modelo -> (hojas, columna del contenido en cada hoja, desde)
HOJAS = {
    "200": (["DP200000", "DP200001", "DP200003", "DP200004", "DP200005", "DP200006", "DP200007", "DP200008",
             "DP200012", "DP200013", "DP200014", "DP200014B", "DP200015", "DP200DID"], {}),
    "202": (["dr M202 (0)", "dr M202 (1)", "dr M202 (2)"], {"*": 5}),
    "131": (["Pág. 0", "Pág. 1", "DID"], {}),
    "309": (["M30900", "M30901"], {"M30901": 5}),
}
CAMPOS = ["pagina", "n", "pos", "lon", "tipo", "descripcion", "contenido"]


def _fila(pagina, v):
    """Fila del diseño (nº, posición, longitud, tipo, descripción, contenido) o None si es cabecera, nota o total."""
    try:
        n = int(float(v[0]))
    except (ValueError, TypeError):
        return None
    lon = v[2]
    if lon in (None, "") or not str(v[3] or "").strip(): return None   # tablas de valores de las notas
    lon = "V" if isinstance(lon, str) and lon.strip().lower().startswith("variable") else str(int(float(lon)))
    pos = str(v[1]).replace(".0", "") if v[1] not in (None, "") else ""
    desc = re.sub(r"\s+", " ", str(v[4] or "")).strip()
    cont = re.sub(r"\s+", " ", str(v[6] if len(v) > 6 and v[6] is not None else "")).strip()
    return {"pagina": pagina.strip(), "n": n, "pos": pos, "lon": lon, "tipo": str(v[3] or "").strip(), "descripcion": desc, "contenido": cont}


def extraer(modelo, ruta):
    hojas, columnas = HOJAS[modelo]
    filas = []
    if ruta.endswith(".xlsx"):
        import openpyxl
        w = openpyxl.load_workbook(ruta, data_only=True)
        leer = lambda nombre: [list(r) for r in next(x for x in w.worksheets if x.title.strip() == nombre).iter_rows(values_only=True)]
    else:
        import xlrd
        b = xlrd.open_workbook(ruta)
        leer = lambda nombre: [next(x for x in b.sheets() if x.name.strip() == nombre).row_values(r)
                               for r in range(next(x for x in b.sheets() if x.name.strip() == nombre).nrows)]
    for nombre in hojas:
        col = columnas.get(nombre, columnas.get("*", 6))
        for row in leer(nombre):
            v = list(row) + [None] * 8
            # Algunas hojas no tienen la columna «Validación»: el contenido está en la sexta
            f = _fila(nombre, [v[0], v[1], v[2], v[3], v[4], None, v[col]])
            if f: filas.append(f)
    os.makedirs(DIR, exist_ok=True)
    _escribir(os.path.join(DIR, f"DR{modelo}.csv"), filas)
    print("ok", len(filas))


def _escribir(ruta, filas):
    with open(ruta, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=CAMPOS)
        w.writeheader()
        w.writerows(filas)


def _leer(ruta):
    with open(ruta, encoding="utf-8") as f:
        return [dict(r, n=int(r["n"])) for r in csv.DictReader(f)]


# ------------------------------------------------------------------------------------------ claves
# Datos que no son casillas: (página, nº de campo) -> clave. Las casillas «[nnnnn]» se traducen solas con el prefijo de la página.
D200 = {
    ("DP200001", 6): "D:tipo", ("DP200001", 7): "D:nif", ("DP200001", 8): "D:nombre", ("DP200001", 9): "D:ejercicio",
    ("DP200001", 11): "D:inicioAnio", ("DP200001", 12): "D:inicioMes", ("DP200001", 13): "D:inicioDia",
    ("DP200001", 14): "D:finAnio", ("DP200001", 15): "D:finMes", ("DP200001", 16): "D:finDia",
    ("DP200001", 17): "D:tipoEjercicio", ("DP200001", 18): "D:cnae", ("DP200001", 20): "D:telefono",
    ("DP200001", 22): "D:rectificativa", ("DP200001", 23): "D:justificanteAnterior", ("DP200001", 24): "D:rectificativa",
    ("DP200001", 102): "D:incnMenor20M", ("DP200001", 103): "D:incn20a60M", ("DP200001", 104): "D:incn60M",
    ("DP200001", 107): "D:contacto", ("DP200001", 108): "D:telefono", ("DP200001", 110): "D:email",
    ("DP200014", 7): "C:00550",   # parte de la base que tributa al tipo general: toda, en las entidades que gestiona la aplicación
    ("DP200014", 31): "D:tipoGravamen200",
    ("DP200DID", 6): "D:cuentaCorrienteTributaria", ("DP200DID", 7): "D:ejercicio", ("DP200DID", 8): "D:tipoEjercicio",
    ("DP200DID", 10): "D:inicioDia", ("DP200DID", 11): "D:inicioMes", ("DP200DID", 12): "D:inicioAnio2",
    ("DP200DID", 13): "D:finDia", ("DP200DID", 14): "D:finMes", ("DP200DID", 15): "D:finAnio2",
    ("DP200DID", 16): "D:nif", ("DP200DID", 17): "D:nombre", ("DP200DID", 23): "D:devolucion", ("DP200DID", 24): "D:importeDevolver",
    ("DP200DID", 26): "D:marcaSepa", ("DP200DID", 27): "D:ibanDevolucion", ("DP200DID", 33): "D:modalidadIngreso",
    ("DP200DID", 34): "D:importeIngresar", ("DP200DID", 35): "D:ibanIngreso", ("DP200DID", 41): "D:resultadoCero",
    ("DP200000", 4): "D:versionPrograma", ("DP200000", 6): "D:nifDesarrollador",
}
D202 = {
    ("dr M202 (0)", 4): "D:ejercicio", ("dr M202 (0)", 5): "D:periodo", ("dr M202 (0)", 9): "D:versionPrograma",
    ("dr M202 (0)", 11): "D:nifDesarrollador",
    ("dr M202 (1)", 6): "D:tipo", ("dr M202 (1)", 7): "D:nif", ("dr M202 (1)", 8): "D:nombre", ("dr M202 (1)", 9): "D:nombrePila",
    ("dr M202 (1)", 11): "D:ejercicio", ("dr M202 (1)", 12): "D:periodo", ("dr M202 (1)", 13): "D:fechaInicio", ("dr M202 (1)", 14): "D:cnae",
    ("dr M202 (1)", 19): "D:reducidaDimension202", ("dr M202 (1)", 20): "D:incnMas6M", ("dr M202 (1)", 23): "D:incnMenor1M202",
    ("dr M202 (1)", 25): "D:tipoGravamen202", ("dr M202 (1)", 26): "D:tramoIncn", ("dr M202 (1)", 27): "D:modalidad202",
    ("dr M202 (2)", 52): "D:complementaria202", ("dr M202 (2)", 53): "D:justificanteAnterior", ("dr M202 (2)", 54): "D:iban",
}
D131 = {("Pág. 0", 4): "D:ejercicio", ("Pág. 0", 5): "D:periodo", ("Pág. 0", 9): "D:versionPrograma", ("Pág. 0", 11): "D:nifDesarrollador",
        ("Pág. 1", 6): "D:tipo", ("Pág. 1", 7): "D:nif", ("Pág. 1", 8): "D:nombre", ("Pág. 1", 9): "D:nombrePila",
        ("Pág. 1", 10): "D:ejercicio", ("Pág. 1", 11): "D:periodo", ("Pág. 1", 43): "C:05",
        ("Pág. 1", 67): "D:complementaria202", ("Pág. 1", 68): "D:justificanteAnterior", ("DID", 5): "D:iban"}
for _k in range(5):   # cinco actividades en módulos: epígrafe, rendimiento neto, % y pago fraccionado
    D131.update({("Pág. 1", 13 + 4 * _k): f"D:epigrafe{_k + 1}", ("Pág. 1", 14 + 4 * _k): f"C:A{_k + 1}R",
                 ("Pág. 1", 15 + 4 * _k): f"C:A{_k + 1}P", ("Pág. 1", 16 + 4 * _k): f"C:A{_k + 1}C"})
D309 = {("M30900", 4): "D:ejercicio", ("M30900", 5): "D:periodo", ("M30900", 9): "D:versionPrograma", ("M30900", 11): "D:nifDesarrollador",
        ("M30901", 6): "D:tipo", ("M30901", 7): "D:nif", ("M30901", 8): "D:nombre", ("M30901", 9): "D:nombrePila",
        ("M30901", 10): "D:ejercicio", ("M30901", 11): "D:periodo", ("M30901", 16): "D:situacion309", ("M30901", 17): "C:HI",
        ("M30901", 59): "D:complementaria202", ("M30901", 60): "D:justificanteAnterior", ("M30901", 61): "D:iban"}
# Prefijo de las casillas de cada página: F marcas de caracteres de la declaración, B balance y pérdidas y ganancias, C liquidación
PREFIJO = {"DP200001": "F:", "DP200003": "B:", "DP200004": "B:", "DP200005": "B:", "DP200006": "B:", "DP200007": "B:", "DP200008": "B:"}
# Sobres: la primera y la última fila (apertura y cierre) llevan ejercicio y periodo
SOBRE = {"DP200000": ("<T2000{D:ejercicio}0A0000>", "</T2000{D:ejercicio}0A0000>"),
         "dr M202 (0)": (None, "</T2020{D:ejercicio}{D:periodo}0000>"),
         "Pág. 0": (None, "</T1310{D:ejercicio}{D:periodo}0000>"),
         "M30900": (None, "</T3090{D:ejercicio}{D:periodo}0000>")}


def tipo_cs(t):
    return {"An": "Alfanumerico", "A": "Alfabetico", "Num": "Numerico", "N": "Numerico"}[t]


def cs(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def constante(contenido):
    """Valor fijo del campo según la columna «Contenido» ('Constante "<T"', '"<AUX>"', 'Constante 0A'…)."""
    c = contenido.strip()
    m = re.fullmatch(r'(?:Constante\.?\s*)?"([^"]*)"\s*', c) or re.fullmatch(r"Constante\s+(\S+)", c)
    if m: return m.group(1)
    m = re.fullmatch(r'"(0A|1P|2P|3P)".*', c)
    return None


def campo(f, mapa):
    pagina, n, t, lon = f["pagina"], f["n"], f["tipo"], f["lon"]
    if lon == "V": return None                                    # contenido de las páginas
    lon = int(lon)
    desc = f["descripcion"]
    nombre = cs(f"{pagina.replace('dr M202 ', 'P').replace('Pág. ', 'P').strip()} {n}. {desc}"[:120])
    tipo = tipo_cs(t)
    extra = []
    if tipo == "Numerico":
        porcentaje = "Porcentaje" in desc or "%" in desc
        dos = re.search(r"(y|\+)\s*(2|dos) decimales", f["contenido"])
        if lon == 17 or (porcentaje and lon == 5) or dos: extra.append("Decimales: 2")
        if t == "N": extra.append("Signo: true")
    clave = mapa.get((pagina, n))
    fijo = None
    sobre = SOBRE.get(pagina)
    if clave is None and sobre:
        if sobre[0] and f is FILAS_PAGINA[pagina][0]: return f"new CampoAeat({nombre}, TipoCampoAeat.Alfanumerico, {lon}, Plantilla: {cs(sobre[0])})"
        if f is FILAS_PAGINA[pagina][-1]: return f"new CampoAeat({nombre}, TipoCampoAeat.Alfanumerico, {lon}, Plantilla: {cs(sobre[1])})"
    if clave is None:
        fijo = constante(f["contenido"])
    if clave is None and fijo is None and pagina == "DP200015":
        # Página 15: solo el detalle de la compensación de bases negativas, por año de origen («BIN2019A» aplicado en 2019…)
        m = re.match(r"Detalle compensación bases imponibles negativas - (\d{4}|TOTAL)(\(\*\))? - (.*)", desc)
        con_asterisco = {re.match(r".* - (\d{4})\(\*\) - ", x["descripcion"]).group(1) for x in FILAS_PAGINA[pagina]
                         if re.match(r".* - (\d{4})\(\*\) - ", x["descripcion"])}
        # El año del periodo sale dos veces: con «(*)» (el que se usa) y sin él (otro detalle del mismo año, que se deja a cero)
        if not m or (m.group(1) in con_asterisco and not m.group(2)):
            return f"new CampoAeat({', '.join([nombre, 'TipoCampoAeat.' + tipo, str(lon)] + extra)})"
        col = "A" if m.group(3).startswith("Aplicad") else "F" if "futuros" in m.group(3) else "P"
        clave = {"P": "C:00670", "A": "C:00547", "F": "C:00671"}[col] if m.group(1) == "TOTAL" else f"C:BIN{m.group(1)}{col}"
        return f"new CampoAeat({', '.join([nombre, 'TipoCampoAeat.' + tipo, str(lon)] + extra + [f'Clave: {cs(clave)}'])})"
    if clave is None and fijo is None:
        # La casilla del campo es la última citada («Resultado previo (clave [16] x [17]) [18]»)
        m = re.findall(r"\[(\d{5}|\d{2})\]", desc)
        if m and "RESERVADO" not in desc.upper():
            clave = PREFIJO.get(pagina, "C:") + m[-1]
    if fijo is not None:
        if len(fijo) != lon: raise SystemExit(f"Constante de longitud distinta en {pagina} {n}: {fijo!r} ({lon})")
        extra.append(f"Fijo: {cs(fijo)}")
    elif clave:
        extra.append(f"Clave: {cs(clave)}")
    return f"new CampoAeat({', '.join([nombre, 'TipoCampoAeat.' + tipo, str(lon)] + extra)})"


FILAS_PAGINA = {}



def bloque(pagina, filas, mapa, ind):
    FILAS_PAGINA[pagina] = filas
    out = [c for c in (campo(f, mapa) for f in filas) if c]
    return f"\n".join(f"{ind}{c}," for c in out)


def generar(salida):
    partes = ['''// <auto-generated>
// Diseños de registro de los modelos 200 (ejercicio 2025, versión 1.02), 202 (2025, versión 1.3), 131 (2026) y 309 (versión 1.4) de la
// Agencia Tributaria, generados por tools/disenos-aeat/oficiales.py a partir de los ficheros oficiales (DR200e25.xls, DR202e25.xlsx,
// DR131_2026.xlsx, dr309e23.xls). Claves: «C:nnnnn» casilla de la liquidación, «B:nnnnn» casilla del balance o de la cuenta de pérdidas
// y ganancias, «F:nnnnn» carácter de la declaración, «D:x» dato de la declaración. No editar a mano: regenerar.
// </auto-generated>
namespace Contabilidad.Domain.Fiscal.Presentacion;

public static class DisenosAeatOficiales
{
''']
    # El 131 de 2026 no cambia respecto del de 2025: se usa también para ese año
    for modelo, archivo, mapa, desde, nombre in [("200", "DR200.csv", D200, "2025, 1, 1", "M200_2025"),
                                                  ("202", "DR202.csv", D202, "2025, 1, 1", "M202_2025"),
                                                  ("131", "DR131.csv", D131, "2025, 1, 1", "M131"),
                                                  ("309", "DR309.csv", D309, "2023, 1, 1", "M309")]:
        filas = _leer(os.path.join(DIR, archivo))
        paginas = []
        for f in filas:
            if not paginas or paginas[-1][0] != f["pagina"]: paginas.append((f["pagina"], []))
            paginas[-1][1].append(f)
        sobre, resto = paginas[0], paginas[1:]
        FILAS_PAGINA[sobre[0]] = sobre[1]
        corte = next(i for i, f in enumerate(sobre[1]) if f["lon"] == "V")
        apertura = [c for c in (campo(f, mapa) for f in sobre[1][:corte]) if c]
        cierre = [c for c in (campo(f, mapa) for f in sobre[1][corte + 1:]) if c]
        partes.append(f'    public static readonly DisenoAeat {nombre} = new("{modelo}", new DateOnly({desde}), null, [\n')
        partes.extend(f"        {c},\n" for c in apertura)
        for pagina, fs in resto:
            titulo = pagina.replace("dr M202 ", "Página ").replace("DP200", "Página ").replace("Pág. ", "Página ").replace("M309", "Página ")
            partes.append(f"        new BloqueAeat({cs(titulo)}, [\n{bloque(pagina, fs, mapa, '            ')}\n        ]),\n")
        partes.extend(f"        {c},\n" for c in cierre)
        partes.append("    ]);\n\n")
    partes.append("    public static readonly IReadOnlyList<DisenoAeat> Todos = [M200_2025, M202_2025, M131, M309];\n}\n")
    with open(salida, "w", encoding="utf-8") as f:
        f.write("".join(partes))
    print("ok")


if __name__ == "__main__":
    if sys.argv[1] == "extraer": extraer(sys.argv[2], sys.argv[3])
    elif sys.argv[1] == "generar": generar(sys.argv[2])
    else: raise SystemExit(__doc__)
