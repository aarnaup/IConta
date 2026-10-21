# Esquemas de la Agencia Tributaria Canaria

`tests/Contabilidad.Tests/Esquemas/Presentacion-420-XMLSchema.xsd` y `Presentacion-425-XMLSchema.xsd` son los esquemas XML de los modelos 420
y 425 de la ATC, tal como los distribuyen los módulos `l10n_es_atc_mod420` y `l10n_es_atc_mod425` de
[OCA/l10n-spain](https://github.com/OCA/l10n-spain) (rama 17.0). Los tests validan contra ellos el XML que genera
`src/Contabilidad.Domain/Fiscal/Presentacion/XmlAtc.cs`. Ese XML es el que importa el programa de ayuda de la ATC para generar la
presentación. Cuando la ATC publique esquemas nuevos, sustitúyelos y vuelve a pasar los tests.
