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
