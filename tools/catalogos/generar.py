"""Genera database/19_bancos_epigrafes.sql con las entidades del Banco de España y los epígrafes del IAE de la AEAT.

    python3 tools/catalogos/generar.py <lista-mfi-es.csv> <Epigrafes_x_EEDD.xlsx> database/19_bancos_epigrafes.sql

- lista-mfi-es.csv: lista de instituciones financieras monetarias del Banco de España (Estadísticas → Listas de IFM). Se cargan las que
  tienen código de entidad de 4 dígitos (el que va en el IBAN: «ES0241» → 0241); los fondos del mercado monetario no tienen.
- Epigrafes_x_EEDD.xlsx: tabla de la AEAT de epígrafes del IAE con su clave de actividad (A01 arrendamiento, A02 ganadería
  independiente, A03 empresarial, A04 artística, A05 profesional), la que piden el 303 (página 4) y el 390 (datos estadísticos).
"""
import csv, sys

SECCION = {"A01": 1, "A02": 1, "A03": 1, "A04": 3, "A05": 2}


def sql(s):
    return "'" + str(s).replace("'", "''") + "'"


def bancos(ruta):
    with open(ruta, encoding="utf-8-sig") as f:
        filas = list(csv.reader(f))
    cab = [c.strip() for c in filas[0]]
    i = {k: cab.index(k) for k in ("CÓDIGO EUROPEO", "NOMBRE", "CATEGORÍA")}
    r = {}
    for f in filas[1:]:
        codigo = f[i["CÓDIGO EUROPEO"]].strip()
        if len(codigo) == 6 and codigo.startswith("ES") and codigo[2:].isdigit():
            r[codigo[2:]] = " ".join(f[i["NOMBRE"]].split())[:150]
    return sorted(r.items())


def epigrafes(ruta):
    import openpyxl
    w = openpyxl.load_workbook(ruta, read_only=True)
    r = {}
    for fila in list(w["Epígrafes"].iter_rows(values_only=True))[2:]:
        clave = (fila[1] or "").strip()
        if clave not in SECCION or fila[2] is None:
            continue                                  # claves B (actividades sin epígrafe) y C (no iniciadas)
        codigo = str(fila[2]).strip()
        if isinstance(fila[2], int): codigo = codigo.zfill(3)
        r[(SECCION[clave], codigo)] = (clave, " ".join(str(fila[3]).split())[:300])
    return sorted(r.items())


def generar(csv_bancos, xlsx_epigrafes, salida):
    b = bancos(csv_bancos)
    e = epigrafes(xlsx_epigrafes)
    out = ["""-- =============================================================================================
-- 19 · Catálogos oficiales: entidades bancarias del Banco de España y epígrafes del IAE de la AEAT
--      Generado por tools/catalogos/generar.py: no editar a mano. Se puede volver a ejecutar: actualiza
--      nombres y descripciones sin duplicar.
-- =============================================================================================

-- Clave de actividad de la AEAT de cada epígrafe (303 página 4 y 390): A01 arrendamiento, A02 ganadería
-- independiente, A03 empresarial, A04 artística, A05 profesional
ALTER TABLE cat.epigrafe_iae ADD COLUMN IF NOT EXISTS clave_actividad varchar(3)
  CHECK (clave_actividad IN ('A01','A02','A03','A04','A05'));

-- ---------------------------------------------------------------- entidades bancarias (código de 4 dígitos del IBAN)
INSERT INTO cat.banco (codigo_entidad, nombre, pais_id)
SELECT v.codigo, v.nombre, (SELECT id FROM cat.pais WHERE iso2 = 'ES')
FROM (VALUES
"""]
    out.append(",\n".join(f"  ({sql(c)}, {sql(n)})" for c, n in b))
    out.append("""
) AS v(codigo, nombre)
ON CONFLICT (pais_id, codigo_entidad) WHERE codigo_entidad IS NOT NULL DO UPDATE SET nombre = EXCLUDED.nombre;

-- ---------------------------------------------------------------- epígrafes del IAE (sección 1 empresarial, 2 profesional, 3 artística)
-- Las secciones 2 y 3 son actividades profesionales a efectos de retención (art. 95.2 del Reglamento del IRPF)
INSERT INTO cat.epigrafe_iae (seccion, codigo, descripcion, es_profesional, clave_actividad)
SELECT v.seccion, v.codigo, v.descripcion, v.seccion IN (2, 3), v.clave
FROM (VALUES
""")
    out.append(",\n".join(f"  ({s}, {sql(c)}, {sql(d)}, {sql(k)})" for (s, c), (k, d) in e))
    out.append("""
) AS v(seccion, codigo, descripcion, clave)
ON CONFLICT (seccion, codigo) DO UPDATE SET descripcion = EXCLUDED.descripcion, clave_actividad = EXCLUDED.clave_actividad;
""")
    with open(salida, "w", encoding="utf-8") as f:
        f.write("".join(out))
    print(f"ok: {len(b)} entidades, {len(e)} epígrafes")


if __name__ == "__main__":
    if len(sys.argv) != 4: raise SystemExit(__doc__)
    generar(*sys.argv[1:])
