# Diseños de registro de la AEAT

`src/Contabilidad.Domain/Fiscal/Presentacion/DisenosAeat.cs` se genera con estos scripts a partir de las definiciones de
exportación de los módulos `l10n_es_aeat_mod*` de [OCA/l10n-spain](https://github.com/OCA/l10n-spain), que reproducen los
diseños de registro publicados por la Agencia Tributaria. Cuando la AEAT publique un diseño nuevo (y OCA lo incorpore):

```bash
git clone --depth 1 --filter=blob:none --sparse -b 17.0 https://github.com/OCA/l10n-spain.git
cd l10n-spain && git sparse-checkout set l10n_es_aeat_mod303 l10n_es_aeat_mod111 l10n_es_aeat_mod115 \
    l10n_es_aeat_mod130 l10n_es_aeat_mod347 l10n_es_aeat_mod349 l10n_es_aeat_mod190 && cd ..
OCA_L10N_SPAIN=l10n-spain python3 tools/disenos-aeat/generar.py src/Contabilidad.Domain/Fiscal/Presentacion/DisenosAeat.cs
```

Para añadir un año, añade su configuración principal a `MODELOS` en `generar.py` (con su fecha de inicio y fin). Si aparece una
expresión nueva, el script se detiene y hay que traducirla en `MAPA` a una clave de la aplicación (`C:nn` casilla, `D:x` dato de la
declaración, `I:x` dato de cada registro repetido) y, si hace falta, darle valor en `FicheroModelo.cs`.

## Modelos 200 y 202

`DisenosAeatSociedades.cs` se genera con `sociedades.py` desde los diseños de registro oficiales de la AEAT (Sede → Ayuda → Diseños
de registro → Modelos 200-299: `DR200eAA.xls` y `DR202eAA.xlsx`). Solo se extraen a `aeat/DR200.csv` y `aeat/DR202.csv` las páginas que
rellena la aplicación, para no guardar los Excel (el del 200 ocupa 11 MB):

```bash
python3 -m pip install xlrd openpyxl
python3 tools/disenos-aeat/sociedades.py extraer DR200e25.xls DR202e25.xlsx
python3 tools/disenos-aeat/sociedades.py generar src/Contabilidad.Domain/Fiscal/Presentacion/DisenosAeatSociedades.cs
```

Las casillas `[nnnnn]` se traducen solas (`C:` liquidación, `B:` balance y pérdidas y ganancias, `F:` caracteres de la declaración);
los demás datos se asignan por página y número de campo en `D200` y `D202`. Con un diseño nuevo, revisa que esos números de campo
siguen siendo los mismos y añade el año con su fecha de inicio.

## Modelo 180

El diseño (registros de 500 posiciones) está escrito a mano en `Diseno180.cs` desde el PDF «DR_Mod_180» de la AEAT.
