import csv, glob, os, re, sys, xml.etree.ElementTree as ET
BASE = os.environ.get("OCA_L10N_SPAIN", "l10n-spain")
def load(module):
    configs, lines = {}, {}
    files = glob.glob(f"{BASE}/{module}/data/**/*", recursive=True)
    for f in files:
        if f.endswith(".xml"):
            try: root = ET.parse(f).getroot()
            except Exception: continue
            for r in root.iter("record"):
                d = {}
                for x in r.findall("field"):
                    v = x.text if x.text is not None else ""
                    if x.get("eval") is not None: v = x.get("eval")
                    d[x.get("name")] = (v, x.get("ref"))
                rid = r.get("id")
                if r.get("model") == "aeat.model.export.config":
                    configs[rid] = {"name": d.get("name", ("",))[0], "start": d.get("date_start", ("",))[0], "end": d.get("date_end", ("",))[0], "file": f}
                elif r.get("model") == "aeat.model.export.config.line":
                    g = lambda k, i=0: (d.get(k) or ("", None))[i]
                    lines.setdefault(g("export_config_id", 1), []).append({
                        "seq": int(g("sequence") or 0), "name": g("name").strip(), "type": g("export_type") or "string",
                        "size": int(g("size") or 0), "dec": int(g("decimal_size") or 0),
                        "sign": None if "apply_sign" not in d else g("apply_sign") in ("True", "1"),
                        "pos": g("positive_sign") if "positive_sign" in d else None, "neg": g("negative_sign") if "negative_sign" in d else None,
                        "fixed": g("fixed_value") if "fixed_value" in d else None, "expr": g("expression").strip() if "expression" in d else None,
                        "cond": g("conditional_expression") if "conditional_expression" in d else None,
                        "rep": g("repeat_expression") if "repeat_expression" in d else None,
                        "sub": (g("subconfig_id", 1) or "").split(".")[-1] if "subconfig_id" in d else None,
                        "align": g("alignment") if "alignment" in d else None, "yes": g("bool_yes") if "bool_yes" in d else None, "no": g("bool_no") if "bool_no" in d else None})
        elif f.endswith("aeat.model.export.config.csv"):
            for r in csv.DictReader(open(f)):
                r = {k: v for k, v in r.items() if k}
                configs[r["id"].split(".")[-1]] = {"name": r["name"], "start": r.get("date_start", ""), "end": r.get("date_end", ""), "file": f}
        elif f.endswith("aeat.model.export.config.line.csv"):
            for r in csv.DictReader(open(f)):
                G = lambda *ks: next((r[k] for k in ks if k in r and r[k] is not None), "")
                cid = G("export_config_id:id", "export_config_id/id", "export_config_id").split(".")[-1]
                lines.setdefault(cid, []).append({
                    "seq": int(G("sequence") or 0), "name": G("name").strip(), "type": G("export_type") or "string", "size": int(G("size") or 0),
                    "dec": int(G("decimal_size") or 0), "sign": (G("apply_sign") in ("1", "True")) if "apply_sign" in r else None,
                    "pos": G("positive_sign") or None, "neg": G("negative_sign") or None,
                    "fixed": G("fixed_value") or None, "expr": G("expression").strip() or None, "cond": G("conditional_expression") or None,
                    "rep": G("repeat_expression") or None, "sub": G("subconfig_id:id", "subconfig_id/id", "subconfig_id").split(".")[-1] or None,
                    "align": G("alignment") or None, "yes": G("bool_yes") or None, "no": G("bool_no") or None})
    for k in lines: lines[k].sort(key=lambda x: x["seq"])
    return configs, lines

def walk(lines, cid, seen=None, depth=0):
    seen = seen if seen is not None else []
    if cid not in seen: seen.append(cid)
    for l in lines.get(cid, []):
        if l["type"] == "subconfig" and l["sub"]: walk(lines, l["sub"], seen, depth + 1)
    return seen

if __name__ == "__main__":
    module = sys.argv[1]
    configs, lines = load(module)
    for cid, c in sorted(configs.items(), key=lambda x: (x[1]["start"] or "")):
        print(f"{cid} | {c['name']} | {c['start']} - {c['end']} | {len(lines.get(cid, []))} líneas")
