# Contabilidad (.NET 10 · Dapper · Autofac · Serilog · PostgreSQL)

## Estructura (`Contabilidad.slnx`)
- `database/` → `01_schema.sql` (esquema completo), `02_demo.sql` (empresa demo, ejercicio partido, canales), `03_facturacion_demo.sql` (IGIC, cuentas automáticas, plantillas de compra/venta, terceros y prorrata de ejemplo), `04_seguridad.sql` (usuarios, permisos por módulo, auditoría, borradores), `05_tesoreria.sql` (restricciones de vencimientos y formas de pago habituales), `06_fiscal.sql` (declaraciones: restricciones y orden de casillas), `07_pgc.sql` (cuadro de cuentas del PGC Normal y PYMES), `08_aplicacion_resultado.sql` (origen de asiento para la aplicación del resultado), `09_inmovilizado.sql` (amortización por fechas, categorías por defecto y diario de amortizaciones), `10_libros_registro.sql` (número de registro de las facturas recibidas), `11_conciliacion.sql` (extractos bancarios y conciliación), `12_remesas.sql` (mandatos y remesas SEPA), `13_bienes_inversion.sql` (regularización de bienes de inversión), `14_operaciones_iva.sql` (intracomunitarias, importaciones e inversión del sujeto pasivo), `15_retenciones_349_caja.sql` (claves del 349, criterio de caja y países de la UE), `16_prorrata_cierre_tesoreria.sql` (regularización de prorrata, cierre de los grupos 8 y 9, extorno y reglas de conciliación).
- `src/Contabilidad.Domain` → reglas puras: canales (`RepartidorCanales`, evaluador de fórmulas) y facturación (`FacturaCalculadora`, `ResolutorCuentas`, `GeneradorAsientoFactura`).
- `src/Contabilidad.Infrastructure` → Dapper + Npgsql, repositorios y módulo Autofac.
- `src/Contabilidad.Api` → API minimal + Serilog + UI estática (`wwwroot/index.html`).
- `tests/Contabilidad.Tests` → xUnit sobre el dominio (no necesita base de datos).

## Puesta en marcha
```bash
createdb contabilidad
psql contabilidad -f database/01_schema.sql
psql contabilidad -f database/02_demo.sql
psql contabilidad -f database/03_facturacion_demo.sql
psql contabilidad -f database/04_seguridad.sql
psql contabilidad -f database/05_tesoreria.sql
psql contabilidad -f database/06_fiscal.sql
psql contabilidad -f database/07_pgc.sql
psql contabilidad -f database/08_aplicacion_resultado.sql
psql contabilidad -f database/09_inmovilizado.sql
psql contabilidad -f database/10_libros_registro.sql
psql contabilidad -f database/11_conciliacion.sql
psql contabilidad -f database/12_remesas.sql
psql contabilidad -f database/13_bienes_inversion.sql
psql contabilidad -f database/14_operaciones_iva.sql
psql contabilidad -f database/15_retenciones_349_caja.sql
psql contabilidad -f database/16_prorrata_cierre_tesoreria.sql

# clave de firma JWT (mín. 32 caracteres; en Development ya hay una de pruebas)
export Jwt__Key="<cadena-aleatoria-larga>"
# primer superadmin (la contraseña se lee de CONTABILIDAD_ADMIN_PASSWORD o se pide por consola)
dotnet run --project src/Contabilidad.Api -- crear-superadmin tu@correo.es "Tu nombre"

dotnet test Contabilidad.slnx
dotnet run --project src/Contabilidad.Api    # cadena de conexión en appsettings.json
```

## Swagger y Postman
En Development (o en cualquier entorno con `OpenApi__Habilitar=true`):
- **Swagger UI**: `http://localhost:5080/swagger`. Pulsa «Authorize» y pega el token de `POST /api/auth/login`.
- **Documento OpenAPI**: `http://localhost:5080/openapi/v1.json`. En Postman: *Import* → pega esa URL (o el fichero). Crea una colección con las rutas agrupadas por módulo y autenticación *Bearer Token*: haz el login, copia el `token` de la respuesta en la variable de la colección (`bearerToken`) y el resto de peticiones ya lo envían.
- Los parámetros `{empresaId}` y `{ejercicioId}` son los `id` de `GET /api/mis-empresas` y `GET /api/empresas/{id}/ejercicios`. Fechas como `2026-12-31`; errores de negocio, 400 con `{ error, detalle }`.

## Plan General Contable
`07_pgc.sql` carga en `cat.cuenta_modelo` el cuadro de cuentas del PGC Normal (RD 1514/2007, 1.218 cuentas) y del PGC de PYMES (RD 1515/2007, 992 cuentas), con las modificaciones de 2016 y 2021, hasta 4 dígitos. Se puede volver a ejecutar sin duplicar.

- Al dar de alta una empresa se copia el plan elegido: grupos, subgrupos y cuentas tal cual; las subcuentas (4 dígitos) se rellenan con ceros hasta los dígitos de la empresa (`4300` → `43000000`) y son las únicas que admiten apuntes.
- Cada cuenta de 3 dígitos sin subcuentas en el PGC recibe la subcuenta `xxx0` con su mismo nombre (`5720`, `6000`, `7000`...), así siempre hay dónde asentar.
- La naturaleza (deudora/acreedora/mixta) se asigna por grupo con excepciones para las cuentas correctoras (amortizaciones, deterioros, devoluciones, rappels...).
- Las subcuentas de clientes y proveedores se numeran dentro de `4300` y `4000` (`43000001`, `40000001`...).
- La lista se ha escrito a partir del cuadro de cuentas publicado; **conviene contrastarla con el BOE** antes de usarla en producción, sobre todo las diferencias entre Normal y PYMES.

## Facturas
`POST /api/empresas/{id}/ejercicios/{ej}/facturas/previsualizar` calcula cuotas y devuelve el asiento previsto sin guardar nada.
`POST /api/empresas/{id}/ejercicios/{ej}/facturas` guarda la factura y, si no es borrador, genera el asiento en la misma transacción.

- **Tipo de operación** por línea (`tipoOperacion`):
  - Emitidas: `INTERIOR`, `EXENTA`, `NO_SUJETA`, `INTRACOM_ENTREGA` (exenta, art. 25), `EXPORTACION` (exenta, art. 21) e `INVERSION_SUJETO_PASIVO` (sin cuota: la declara el cliente).
  - Recibidas: `INTERIOR`, `EXENTA`, `NO_SUJETA`, `IMPORTACION` (IVA pagado en aduana, DUA: lleva cuota como una interior), `INTRACOM_ADQUISICION` e `INVERSION_SUJETO_PASIVO`. Estas dos se **autorrepercuten**: el proveedor cobra solo la base y el asiento lleva la cuota a la 477 (devengada) y la parte deducible a la 472 (la no deducible, a gasto).
  - En el IGIC no hay intracomunitarias (Canarias está fuera del territorio IVA de la UE): las compras de fuera son importaciones y las ventas, exportaciones.

- % de IVA/IGIC: el vigente en la fecha de devengo (`fechaOperacion`, o la de expedición).
- Recargo de equivalencia: en emitidas, si el cliente lo tiene marcado; en recibidas, si la empresa está en ese régimen.
- Prorrata (recibidas): regla de deducción `PRORRATA`, con el % del año natural y el sector de la actividad (`fiscal.prorrata`).
- Retención IRPF: en emitidas solo si la empresa tiene una actividad con epígrafe profesional.
- Rectificativas: base negativa, el asiento sale con Debe/Haber invertidos.
- Plantillas: líneas con cuenta fija o por rol (`conta.cuenta_automatica`). Orígenes de importe: BASE, CUOTA, RECARGO, RETENCION, TOTAL, CUOTA_NO_DEDUCIBLE, EXPRESION.

## Canales
Ver bloque anterior: A, B, C, D reparten el importe completo; E, F… por porcentaje, fórmula o resto. Cada cambio de fórmula crea una versión nueva con fecha.

## Dos áreas y seguridad
- **Administración** (`/api/admin/**`, solo superadmin): usuarios, asignación a empresas con rol y permisos por módulo, alta de empresas (copia el plan modelo, crea diarios, canales 1/2/3/A–D y cuentas automáticas) y de ejercicios. Aquí irá también la gestión de catálogos e impresos.
- **Empresa** (`/api/empresas/{id}/...` y `/api/empresas/{id}/ejercicios/{ej}/...`): al entrar se elige empresa y ejercicio; el ejercicio se valida en cada petición (pertenece a la empresa, está abierto y contiene la fecha).
- Login con email y contraseña (PBKDF2-SHA256, 600.000 iteraciones, mínimo 12 caracteres), JWT, bloqueo tras 5 fallos (15 min) y límite de 10 intentos/min por IP.
- El token **no** lleva permisos: se consultan en BD en cada petición, así que quitar un acceso surte efecto al instante.
- Un usuario puede estar en varias empresas con roles distintos (ADMIN, CONTABLE, LECTURA) y sobrescribir el nivel por módulo (NINGUNO/LECTURA/ESCRITURA).
- **Borradores**: quien no sea gestor, superadmin o tenga `salta_revision` guarda siempre en borrador (lo fuerza el servidor); un revisor aprueba (la factura se recalcula y se comparan totales) o rechaza.
- Usuarios con contraseña temporal solo pueden usar `/api/auth` hasta cambiarla. Acciones sensibles quedan en `core.auditoria`.

## Tesorería
- **Vencimientos automáticos**: al contabilizar una factura (directa o al aprobar un borrador) se crean en la misma transacción. Forma de pago: la indicada en la factura (`formaPagoId`), si no la del tercero, y si no hay ninguna, un único vencimiento a la fecha de expedición. El importe es el total **neto de retención**. Plazos iguales; la diferencia de redondeo va al último. Una rectificativa (total negativo) no genera vencimientos.
- **Cobrar/pagar** (`POST /api/empresas/{id}/ejercicios/{ej}/tesoreria/vencimientos/{v}/liquidar`): genera el asiento (cobro: Banco D / Cliente H; pago: Proveedor D / Banco H) en el diario de bancos, un movimiento bancario y el registro de cobro/pago, y actualiza el vencimiento (PARCIAL o LIQUIDADO). Admite cobros parciales; bloquea la fila para que dos cobros simultáneos no superen el importe. Exige poder contabilizar sin revisión.
- **Cuentas bancarias** (IBAN validado con mod-97) enlazadas a una subcuenta 57x del plan.
- **Consultas**: `GET .../tesoreria/vencimientos` (filtros tipo, estado, fechas, tercero), `.../tesoreria/prevision` (vencido + próximos meses), `.../tesoreria/cuentas-bancarias`.
- `POST .../tesoreria/vencimientos/{v}/anular` solo para vencimientos sin movimientos.

## Remesas SEPA
En la web, dentro de «Tesorería». Rutas en `/api/empresas/{id}/tesoreria/mandatos`, `.../tesoreria/remesas` y `.../terceros/{t}/cuentas-bancarias`; contabilizar y devolver, en `/api/empresas/{id}/ejercicios/{ej}/tesoreria/remesas/...`.

- **Cuentas bancarias de clientes y proveedores** (IBAN validado, BIC opcional) y **mandatos** de domiciliación (referencia, fecha de firma, esquema CORE o B2B, recurrente o único).
- **Remesa de cobros**: recibos pendientes de clientes con mandato activo → fichero **pain.008.001.02** (adeudos directos). La secuencia sale sola: primer adeudo del mandato FRST, los siguientes RCUR, mandato único OOFF. El identificador de acreedor se calcula del NIF (`ES` + control + sufijo + NIF; sufijo en `core.empresa.sepa_sufijo`, por defecto 000).
- **Remesa de pagos**: pagos pendientes a proveedores con IBAN → fichero **pain.001.001.03** (transferencias).
- Los ficheros se validan contra los esquemas oficiales ISO 20022 en los tests. Textos transliterados al juego de caracteres SEPA.
- Al generar, los vencimientos quedan **REMESADOS** (no se pueden cobrar a mano, siguen en la previsión). Se puede anular mientras no esté contabilizada.
- **Contabilizar** cuando el banco abona o carga: un asiento con el banco (por canal) contra cada cliente o proveedor; los vencimientos quedan liquidados.
- **Devolución** de un recibo: asiento cliente contra banco, gastos opcionales, y el recibo vuelve a estar pendiente para remesarlo otra vez.

## Conciliación bancaria
Rutas en `/api/empresas/{id}/tesoreria/extractos` y `.../tesoreria/conciliacion`; contabilizar desde una línea, en `/api/empresas/{id}/ejercicios/{ej}/tesoreria/extractos/lineas/{linea}/...`. En la web, dentro de «Tesorería».

- **Importar extractos** en **Norma 43** (cuaderno 43 de la AEB: registros 11, 22, 23, 33 y 88; se comprueban los totales y el saldo final; la cuenta se reconoce por el IBAN, aunque el fichero traiga varias) o en **CSV** con cabecera (fecha, concepto, importe o cargo/abono, saldo opcional; separador, fechas y decimales españoles o ingleses).
- Reimportar un fichero o importar periodos solapados **no duplica** líneas; dos movimientos iguales el mismo día sí se mantienen.
- Se concilia contra los **apuntes de la cuenta contable del banco** (572…), vengan de donde vengan: cobros y pagos, asientos manuales, impuestos… Apertura y cierre no cuentan.
- **Automática**: mismo importe y fecha a 5 días como mucho, el más cercano; si hay duda (dos candidatos igual de cerca en fechas distintas) lo deja para una persona.
- **Manual**: varias líneas contra varios apuntes, si las sumas coinciden.
- **Desde una línea del banco**: contabilizar el movimiento contra una contrapartida (comisiones, intereses…) o liquidar un cobro o pago pendiente; en ambos casos queda conciliado en la misma operación.
- **Estado** a una fecha: saldo del banco, saldo contable, pendientes de cada lado y diferencia (debe ser cero). Lo anterior al primer extracto se da por conciliado.
- Se puede deshacer una conciliación (no toca los asientos), ignorar una línea duplicada o errónea y eliminar un extracto sin líneas conciliadas.

## Modelos fiscales
Se calculan desde las facturas contabilizadas (fecha contable dentro del periodo) y los asientos. Pertenecen a la empresa y al año natural, no al ejercicio contable (`/api/empresas/{id}/fiscal/...`).

| Modelo | Qué calcula |
|---|---|
| **303** IVA | Devengado 4/10/21 %, recargo de equivalencia, adquisiciones intracomunitarias (10/11) e inversión del sujeto pasivo (12/13), rectificativas; deducible interior (28–31), importaciones (32–35) e intracomunitarias (36–39), corriente e inversión; prorrata, bienes de inversión (43), compensaciones; informativas 59 (entregas intracomunitarias), 60 (exportaciones) y 122 (ISP emitidas) |
| **420** IGIC | Lo mismo, con una clave por cada tipo de IGIC presente; importaciones `ICB/ICC` (`IIB/IIC` inversión), ISP `ISPB/ISPC`, exportaciones `EXP` |
| **390 / 425** | Resumen anual con las claves de la autoliquidación; se contrasta con la suma de las presentadas |
| **130** | 20 % del rendimiento neto acumulado (cuentas 70/75 menos 60/62/631/64/65/66/68), menos pagos anteriores y retenciones soportadas |
| **347 / 415** | Terceros que superan 3.005,06 € por clave (A compras, B ventas), con desglose trimestral |
| **349** | Operaciones intracomunitarias por operador (NIF-IVA con prefijo del país) y clave: E entregas, S servicios prestados, T triangulares, A adquisiciones, I servicios recibidos; rectificaciones de periodos anteriores con la base declarada y la rectificada |
| **111** | Retenciones de actividades económicas de las facturas recibidas (casillas 07–09, 28–30), con detalle por perceptor |
| **115** | Retenciones de arrendamientos de inmuebles urbanos (casillas 01–05), con detalle por arrendador |

- **Flujo**: `calcular` (no guarda) → `declaraciones` (guarda como CALCULADA) → `presentar` (fija las cifras; antes recalcula y se niega si la contabilidad cambió) o `descartar`. Presentar y descartar exigen poder contabilizar sin revisión.
- **Compensaciones**: el saldo a favor de un 303/420 presentado se arrastra a los siguientes (máx. 4 años) y se aplica solo.
- **Prorrata**: se mantiene en «Modelos fiscales → Prorrata» (`GET/PUT /api/empresas/{id}/fiscal/prorratas`): provisional, definitivo u operaciones del año, con las que se calcula el definitivo redondeado a la unidad superior. En el último periodo se regulariza la diferencia entre el % provisional usado y el definitivo, también la de los bienes de inversión del año.
- **Bienes de inversión**: la regularización de años anteriores y la de las ventas va en la casilla 43 (IGIC: `RBI`); ver «Bienes de inversión» más abajo.
- **Numeración de casillas**: el 303 usa la del formulario (01–09, 16–24, 27, 28–31, 44–46, 64–71…); el IGIC y los resúmenes usan claves descriptivas. **Contrástala con el formulario del año antes de presentar**: esta aplicación calcula y registra, pero no genera el fichero oficial ni envía nada a la Agencia Tributaria.
- Un tipo de IVA sin casilla (p. ej. 7 % en el 303) **detiene el cálculo** con un error en vez de omitirse en silencio.
- **347**: no incluye las facturas con operaciones intracomunitarias (van al 349), importaciones ni exportaciones.
- **111 / 115**: salen de las retenciones de las facturas recibidas según el tipo de retención (profesionales → 111, arrendamientos → 115), por fecha contable. Avisan si la cuenta 4751 se ha movido más de lo que suman las facturas (nóminas o asientos manuales, que hay que añadir a mano).
- **349**: la clave se indica en la factura (`clave349`; por defecto E en ventas y A en compras). Las rectificativas que indican la factura rectificada (`facturaRectificadaId`) de un periodo anterior van como rectificación de aquel periodo. Los servicios intracomunitarios recibidos (clave I) se deducen en las casillas 28/29 del 303 en vez de 36/37.

## Criterio de caja
Régimen especial del criterio de caja (arts. 163 decies a sexiesdecies LIVA). Se activa en «Modelos fiscales → Régimen de IVA y criterio de caja» (`PUT /api/empresas/{id}/fiscal/configuracion` con `{ "regimenIva": "CRITERIO_CAJA" }`). También se puede marcar un proveedor como acogido al criterio de caja (`criterioCaja` al crearlo o `PUT .../terceros/{id}/criterio-caja`): sus facturas se deducen al pagarlas aunque la empresa esté en el régimen general.

- La factura decide al contabilizarse si va por caja (y no cambia aunque luego cambie el régimen). Solo las operaciones interiores: intracomunitarias, importaciones, exportaciones e inversión del sujeto pasivo siguen la regla general.
- El asiento de la factura deja el impuesto en **4779** (repercutido pendiente) o **4729** (soportado pendiente), que se crean solas.
- **Devengo**: al cobrar (o deducir al pagar), en proporción a lo cobrado sobre el total de la factura; lo que no se haya cobrado el 31 de diciembre del año siguiente se devenga ese día. Los recibos devueltos restan. Las rectificativas, en su fecha.
- **303/420**: las casillas de devengo y deducción llevan lo cobrado y pagado en el periodo; además la información adicional 62/63 (entregas del periodo por la regla general) y 74/75 (adquisiciones).
- **Traspaso**: `POST .../ejercicios/{ej}/fiscal/criterio-caja/traspasar` (`{anio, periodo}`) contabiliza lo devengado del periodo: 4779 a 477 y 472 a 4729 (origen `CRITERIO_CAJA`), y se puede anular. La liquidación del IVA no toca las cuentas de pendiente y avisa si falta el traspaso.
- **Libros registro**: las facturas por caja llevan sus cobros y pagos (fecha, importe, medio y cuenta) en el CSV y en el formato AEAT (columnas de cobro/pago; un cobro por fila).

## Libros registro de IVA/IGIC
`GET /api/empresas/{id}/fiscal/libros/{emitidas|recibidas}?anio=2026&periodo=4T` (periodos 1T–4T, 01–12 o 0A) y `.../csv` para descargarlo. En la web, dentro de «Modelos fiscales».

- Una línea por factura contabilizada y tipo impositivo: fechas, serie y número, NIF y nombre, tipo de factura y de operación, base, tipo, cuota, recargo, retención, total, factura rectificada y asiento. Las recibidas añaden cuota deducible y bien de inversión.
- El periodo va por **fecha contable**, como el 303/420: los totales por tipo se pueden contrastar con la declaración.
- Las facturas recibidas llevan **número de registro** correlativo por año (orden de recepción, art. 64 RD 1624/1992). Lo asigna la base de datos al contabilizarlas, también al aprobar un borrador, y no cambia aunque luego entren facturas con fechas anteriores.
- El CSV usa separador «;», coma decimal y UTF-8 con BOM, para abrirlo directamente en Excel.
- Avisa de facturas en borrador del periodo (no salen en el libro) y de operaciones que no son interiores.
- Criterio de caja: cada factura por caja lleva sus cobros o pagos (ver «Criterio de caja»).
- **Formato oficial de la AEAT** (solo IVA): `GET /api/empresas/{id}/fiscal/libros-aeat?anio=2026&periodo=2T` devuelve un XLSX con las hojas «EXPEDIDAS» y «RECIBIDAS» en la disposición del modelo de la Agencia (cabeceras en las filas 6 y 7, datos desde la 8). Tipo de factura F1/F2/R1/F5, calificación S1/S2/N1, exenciones E1/E2/E5, NIF de extranjeros con tipo 02/03/04 y país, número y fecha de recepción, bien de inversión, inversión del sujeto pasivo y periodo de deducción. Botón «Formato AEAT (XLSX)» en la web. Las columnas de actividad (IAE), clave de operación y criterio de caja se dejan en blanco.
- No genera el SII.

## Liquidación del IVA/IGIC
`POST /api/empresas/{id}/ejercicios/{ej}/fiscal/declaraciones/{d}/asiento` contabiliza la liquidación de un 303 o 420 **presentado** (`.../asiento/previsualizar` la calcula sin guardar; `DELETE .../asiento` la anula).

- Salda las cuentas 472 y 477 con su saldo a la fecha de fin del periodo: 477 (D) a 472 (H). La diferencia va a la **4750** si sale a ingresar o a la **4700** si sale a compensar o devolver.
- Si la declaración aplica cuotas a compensar de periodos anteriores, esa parte sale de la 4700 (H) y solo el resto va a la 4750.
- Se calcula canal a canal, como el resto de la contabilidad. Si ningún canal coincide con el resultado de la declaración, avisa (facturas contabilizadas después de presentar, apuntes manuales, periodos sin liquidar…).
- Las liquidaciones van en orden: no se puede contabilizar un periodo si hay otro posterior ya liquidado, ni anular uno que tenga otro posterior.
- La regularización por prorrata definitiva (casilla 44) se contabiliza en «Modelos fiscales → Prorrata» (`POST .../ejercicios/{ej}/fiscal/prorratas/regularizar`, `{anio}`): por cada línea con la regla de prorrata, cuota × (% definitivo − % aplicado), contra 6391/6392 (más deducción) o 6341/6342 (menos), por canal y tipo; se puede anular. La liquidación la contrasta con la declaración.
- Cuentas: roles `HP_ACREEDORA_IVA` y `HP_DEUDORA_IVA` en cuentas automáticas o, si no, las subcuentas 4750 y 4700 del plan.

## Cierre de ejercicio
`GET /api/empresas/{id}/ejercicios/{ej}/cierre` dice si se puede cerrar, qué lo impide y el resultado por canal. `POST .../cierre` cierra; `POST .../cierre/deshacer` lo revierte.

- En una transacción: **regularización** (periodo 13: gastos e ingresos contra la 129), **cierre** (periodo 14: todas las cuentas a cero) y **apertura** del ejercicio siguiente (periodo 0, mismos saldos). El ejercicio queda `CERRADO` y no admite más asientos.
- Requisitos: el ejercicio anterior cerrado, el siguiente creado y contiguo, sin asientos ni facturas en borrador, sin saldo en los grupos 8 y 9 y con cuenta 129 (rol `RESULTADO_EJERCICIO`). Avisa si queda IVA/IGIC sin liquidar.
- Deshacer anula los tres asientos (si el ejercicio siguiente sigue abierto) y reabre el ejercicio.
- En la web: botón «Contabilizar liquidación» en Modelos fiscales y pestaña «Cierre».

## Aplicación del resultado
`GET /api/empresas/{id}/ejercicios/{ej}/aplicacion-resultado` muestra, por canal, el resultado pendiente de aplicar (saldo de la 129, que llega con la apertura), el capital, la reserva legal y la **reserva legal mínima** (10 % del beneficio hasta llegar al 20 % del capital, art. 274 LSC). `POST .../previsualizar` calcula el asiento; `POST .../aplicacion-resultado` lo contabiliza; `DELETE .../aplicacion-resultado/{asiento}` lo anula. En la web, dentro de la pestaña «Cierre».

- Destinos: reserva legal (112), reservas voluntarias (113), remanente (120), compensar pérdidas anteriores (121), dividendos, otra cuenta del grupo 1 y, si hay pérdida, resultados negativos (121) o compensación con reservas.
- Cada destino va por importe, por porcentaje o «el resto»; lo repartido debe coincidir con el resultado.
- Dividendos: lo ya repartido a cuenta (557) se cancela primero y el resto queda a pagar (526).
- Avisa (sin bloquear) si la reserva legal queda por debajo del mínimo o si se reparten dividendos con pérdidas anteriores sin compensar (art. 273 LSC).
- Canal a canal: se puede aplicar un canal concreto o todos a la vez (mejor con porcentajes). No deja aplicar dos veces el mismo resultado.

## Inmovilizado
Fichas en `/api/empresas/{id}/inmovilizado` (categorías, listado, ficha con cuadro) y operaciones con asiento en `/api/empresas/{id}/ejercicios/{ej}/inmovilizado` (alta, amortización, bajas). En la web, pestaña «Inmovilizado».

- **Categorías** con sus cuentas (21x/20x, 28x, 68x), método y vida útil por defecto. Cada empresa recibe 12 habituales (construcciones, maquinaria, mobiliario, equipos informáticos, transporte, aplicaciones…) con vidas útiles dentro de la tabla fiscal; las subcuentas que falten se crean desde el PGC.
- **Alta**: valor, residual, fecha de inicio, vida útil, canal, factura y proveedor opcionales, y **amortización previa** para traspasos desde otro programa. Si se indica contrapartida, contabiliza la compra (21x contra 523/572…); si no, se entiende que ya está con su factura.
- **Amortización lineal por días**: lo acumulado a una fecha es la base por la fracción de vida transcurrida; las cuotas son diferencias de acumulados, así que no hay deriva de redondeo y al final se llega exacto. `POST .../amortizacion` contabiliza todo lo pendiente hasta la fecha en un asiento (diario de amortizaciones, cada elemento en su canal). Se puede ejecutar cada mes, trimestre o año; si quedó algo atrasado, se incluye con aviso. Anular solo la última de cada elemento.
- **Baja o venta**: amortiza hasta la fecha y en el mismo asiento salda la 28x y la 21x, carga el cobro y lleva la diferencia a 671/771 (670/770 intangible, 672/772 inversiones inmobiliarias). Se puede anular. El IVA de la venta no se genera: emite la factura aparte.
- **Ficha**: cuadro por ejercicios de la empresa (también partidos) y amortización ya contabilizada.
- El **cierre** avisa si queda amortización pendiente hasta fin de ejercicio.

## Bienes de inversión (regularización del IVA/IGIC)
Arts. 107 a 110 de la Ley del IVA (las mismas reglas en el IGIC). Registro en la ficha del elemento (`POST /api/empresas/{id}/inmovilizado/{elemento}/bien-inversion`), libro en `GET .../inmovilizado/bienes-inversion?anio=2027` y asiento en `POST .../ejercicios/{ej}/inmovilizado/bienes-inversion/regularizar` (`{anio, periodo}`). En la web, pestaña «Inmovilizado».

- **Registro**: solo elementos de más de 3.005,06 €. Cuota soportada y % de deducción salen de la factura del elemento (líneas marcadas como bien de inversión) y de la prorrata del año; se pueden indicar a mano. 5 años de regularización, 10 los inmuebles.
- **Año de inicio**: se ajusta con la prorrata definitiva, como el resto de cuotas (casilla 44). La deducción de referencia es la del % definitivo de ese año.
- **Años siguientes**: en el último periodo de cada año, (cuota × % definitivo del año − deducción inicial) ÷ 5 (o 10), solo si los porcentajes se separan **más de 10 puntos**. Sin el definitivo del año, se avisa y no se calcula.
- **Venta** dentro del periodo: en la declaración del periodo de la venta se regularizan de una vez ese año y los que quedan, como si el bien se usara al 100 % (venta sujeta y no exenta) o al 0 % (marcada como exenta o no sujeta). Una baja sin venta deja de regularizarse.
- **303/420**: el importe entra solo en la casilla 43 (IGIC: `RBI`), con un aviso por bien. Avisa también de facturas del año con bienes de inversión y prorrata que no están en el registro.
- **Asiento** (origen `BIEN_INVERSION`): más deducción, 472 a 6392; menos, 6342 a 472, en el canal del elemento. Se puede anular; la liquidación del IVA contrasta con lo contabilizado en el periodo. Una venta con su regularización contabilizada no se puede anular sin anular antes ésta.

## Balances
`GET /api/empresas/{id}/ejercicios/{ej}/balances/...` (permiso de lectura en Balances o Contabilidad). En la web, pestaña «Balances», con opción de imprimir.

| Informe | Ruta | Parámetros |
|---|---|---|
| Sumas y saldos | `sumas-saldos` | `desde`, `hasta`, `nivel` (1 grupo … 4 subcuenta, 5 cuenta completa), `incluir` (MOVIMIENTOS, REGULARIZACION, CIERRE) |
| Balance de situación | `situacion` | `hasta` |
| Pérdidas y ganancias | `perdidas-ganancias` | `desde`, `hasta` |

- Todo es **por canal base** (`canalId`, por defecto el primero): cada canal es una contabilidad completa y no se suman entre sí.
- Balance y PyG siguen el **modelo PYMES/abreviado** y llevan la columna del ejercicio anterior. Se calculan sin los asientos de regularización y cierre, así que también sirven para ejercicios ya cerrados. Antes de regularizar, el resultado del ejercicio sale de los grupos 6 y 7.
- Cada cuenta va a la partida cuyo prefijo coincide con más dígitos. Las cuentas corrientes (551, 552, 555…) van al activo o al pasivo según su saldo. Cada partida se puede desplegar para ver sus cuentas.
- Si una cuenta con saldo no encaja en ninguna partida, se lista aparte; si el balance no cuadra, se avisa. Un test comprueba que todas las subcuentas del PGC cargado tienen partida.
- **Contrasta la asignación de cuentas y la numeración con el modelo oficial antes de depositar las cuentas.** No hay todavía modelo normal completo, estado de cambios en el patrimonio neto, estado de flujos de efectivo ni memoria.

## Pendiente
Operaciones no sujetas por reglas de localización (casilla 120); retenciones de nóminas en el 111; modelos 131, 190, 180, 200 y 202; ficheros de presentación del 111, 115 y 349; formato oficial de los resúmenes anuales 390/425 y ficheros de presentación; declaraciones complementarias y sustitutivas; régimen simplificado y recargo de equivalencia como régimen de la empresa; rectificativas en criterio de caja según el cobro de la factura original; deshacer un cobro/pago (extorno), reglas de conciliación por concepto, SII; regularización de bienes de inversión por cambio de afectación (sin prorrata) y en sectores diferenciados desde la web; amortización degresiva y por tablas, diferencias entre amortización contable y fiscal; traspaso automático de los grupos 8 y 9 al subgrupo 13 en el cierre; modelo normal de cuentas anuales, estado de cambios en el patrimonio neto, flujos de efectivo y memoria; analítica y presupuestos; gestión de catálogos e impresos en la UI de administración.

## Estado de verificación
Compilado con el SDK de .NET 10 (sin errores ni avisos) y los 253 tests de `tests/Contabilidad.Tests` pasan (`dotnet test Contabilidad.slnx`). Con la base cargada desde los scripts 01→16 en PostgreSQL 16 se ha probado la API de punta a punta: login, administración, alta de facturas emitidas y recibidas con su asiento, mayor, vencimientos, cobro parcial y pago total, previsión de tesorería y modelos 420 y 347 (calcular, guardar y presentar). También el alta de empresas con el PGC Normal y PYMES, y en una de ellas cliente, factura, cobro y modelo 303. También la liquidación del IGIC de tres trimestres seguidos (a ingresar, a compensar y compensando lo anterior, con una venta repartida en tres canales) y el cierre del ejercicio con su apertura, deshacer y volver a cerrar, por la API y desde la web. Y los balances de sumas y saldos, situación y pérdidas y ganancias de esos dos ejercicios, con comparativo, por canal y en pantalla e impresión, y la aplicación del resultado de 2026/27 en 2027/28 (reserva legal, dividendos, voluntarias y remanente; anular y repetir). Y el inmovilizado: altas con y sin asiento, amortización por días en varias ejecuciones, anular, venta con pérdida, aviso en el cierre, en la API y en la web. Y los libros registro: numeración de recibidas al contabilizar y al aprobar borradores, facturas con dos tipos y rectificativas, totales que cuadran con el 420 y descarga del CSV desde la web; y el XLSX en formato AEAT de una empresa peninsular (cliente francés con NIF-IVA, exenta, bien de inversión). Y la conciliación bancaria: Norma 43 y CSV, reimportación sin duplicados, casación automática, pago y comisión contabilizados desde el extracto, deshacer y conciliar a mano, por la API y en la web. Y las remesas SEPA: mandatos, remesa de cobros con FRST/RCUR, contabilización por canal, devolución con gastos y nueva remesa, remesa de pagos y anulación; los XML generados validan contra los esquemas oficiales. Y los bienes de inversión en una empresa de IVA: compra con prorrata provisional del 80 % y definitiva del 78 % (calculada con las operaciones), registro desde la factura, regularización de 2027 con prorrata del 60 % (−756,00, casilla 43 y asiento 6342/472), venta sujeta en 2028 (2.772,00 por 3 años, en el 303 del 2T) con su asiento, anular y repetir, y la liquidación del 303 cuadrando con la contabilidad, por la API y en la web. Y un trimestre con adquisición intracomunitaria, compra con inversión del sujeto pasivo (bien de inversión), DUA de importación, entrega intracomunitaria, exportación y venta interior: asientos con autorrepercusión, casillas 10–13, 30–37, 59 y 60 del 303, liquidación sin diferencias y 347 sin las operaciones exteriores. Y en el mismo trimestre: 111 y 115 desde facturas de una abogada (con rectificativa) y un arrendador; 349 con entrega a Francia, adquisición de Alemania, servicio de Irlanda (clave I, deducido en 28/29) y rectificación de una entrega del 2T. Y criterio de caja: venta cobrada a medias y compra pagada, 303 del 4T con lo cobrado y pagado, traspaso 4779→477 y 472→4729, liquidación cuadrada sin avisos, anular y repetir, el resto devengado el 31/12 del año siguiente y el libro AEAT con los cobros, por la API y en la web.
