# Contabilidad (.NET 10 · Dapper · Autofac · Serilog · PostgreSQL)

## Estructura (`Contabilidad.slnx`)
- `database/` → `01_schema.sql` (esquema completo), `02_demo.sql` (empresa demo, ejercicio partido, canales), `03_facturacion_demo.sql` (IGIC, cuentas automáticas, plantillas de compra/venta, terceros y prorrata de ejemplo), `04_seguridad.sql` (usuarios, permisos por módulo, auditoría, borradores), `05_tesoreria.sql` (restricciones de vencimientos y formas de pago habituales), `06_fiscal.sql` (declaraciones: restricciones y orden de casillas), `07_pgc.sql` (cuadro de cuentas del PGC Normal y PYMES), `08_aplicacion_resultado.sql` (origen de asiento para la aplicación del resultado), `09_inmovilizado.sql` (amortización por fechas, categorías por defecto y diario de amortizaciones), `10_libros_registro.sql` (número de registro de las facturas recibidas).
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

# clave de firma JWT (mín. 32 caracteres; en Development ya hay una de pruebas)
export Jwt__Key="<cadena-aleatoria-larga>"
# primer superadmin (la contraseña se lee de CONTABILIDAD_ADMIN_PASSWORD o se pide por consola)
dotnet run --project src/Contabilidad.Api -- crear-superadmin tu@correo.es "Tu nombre"

dotnet test Contabilidad.slnx
dotnet run --project src/Contabilidad.Api    # cadena de conexión en appsettings.json
```

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

## Modelos fiscales
Se calculan desde las facturas contabilizadas (fecha contable dentro del periodo) y los asientos. Pertenecen a la empresa y al año natural, no al ejercicio contable (`/api/empresas/{id}/fiscal/...`).

| Modelo | Qué calcula |
|---|---|
| **303** IVA | Devengado 4/10/21 %, recargo de equivalencia, rectificativas, deducible corriente e inversión, prorrata, compensaciones |
| **420** IGIC | Lo mismo, con una clave por cada tipo de IGIC presente |
| **390 / 425** | Resumen anual con las claves de la autoliquidación; se contrasta con la suma de las presentadas |
| **130** | 20 % del rendimiento neto acumulado (cuentas 70/75 menos 60/62/631/64/65/66/68), menos pagos anteriores y retenciones soportadas |
| **347 / 415** | Terceros que superan 3.005,06 € por clave (A compras, B ventas), con desglose trimestral |

- **Flujo**: `calcular` (no guarda) → `declaraciones` (guarda como CALCULADA) → `presentar` (fija las cifras; antes recalcula y se niega si la contabilidad cambió) o `descartar`. Presentar y descartar exigen poder contabilizar sin revisión.
- **Compensaciones**: el saldo a favor de un 303/420 presentado se arrastra a los siguientes (máx. 4 años) y se aplica solo.
- **Prorrata**: en el último periodo se regulariza la diferencia entre el % provisional usado y el definitivo (`fiscal.prorrata.porcentaje_definitivo`).
- **Numeración de casillas**: el 303 usa la del formulario (01–09, 16–24, 27, 28–31, 44–46, 64–71…); el IGIC y los resúmenes usan claves descriptivas. **Contrástala con el formulario del año antes de presentar**: esta aplicación calcula y registra, pero no genera el fichero oficial ni envía nada a la Agencia Tributaria.
- Un tipo de IVA sin casilla (p. ej. 7 % en el 303) o una operación intracomunitaria/importación **detiene el cálculo** con un error en vez de omitirse en silencio.

## Libros registro de IVA/IGIC
`GET /api/empresas/{id}/fiscal/libros/{emitidas|recibidas}?anio=2026&periodo=4T` (periodos 1T–4T, 01–12 o 0A) y `.../csv` para descargarlo. En la web, dentro de «Modelos fiscales».

- Una línea por factura contabilizada y tipo impositivo: fechas, serie y número, NIF y nombre, tipo de factura y de operación, base, tipo, cuota, recargo, retención, total, factura rectificada y asiento. Las recibidas añaden cuota deducible y bien de inversión.
- El periodo va por **fecha contable**, como el 303/420: los totales por tipo se pueden contrastar con la declaración.
- Las facturas recibidas llevan **número de registro** correlativo por año (orden de recepción, art. 64 RD 1624/1992). Lo asigna la base de datos al contabilizarlas, también al aprobar un borrador, y no cambia aunque luego entren facturas con fechas anteriores.
- El CSV usa separador «;», coma decimal y UTF-8 con BOM, para abrirlo directamente en Excel.
- Avisa de facturas en borrador del periodo (no salen en el libro) y de operaciones que no son interiores.
- No genera el formato oficial de la AEAT para libros de IRPF/IVA ni el SII.

## Liquidación del IVA/IGIC
`POST /api/empresas/{id}/ejercicios/{ej}/fiscal/declaraciones/{d}/asiento` contabiliza la liquidación de un 303 o 420 **presentado** (`.../asiento/previsualizar` la calcula sin guardar; `DELETE .../asiento` la anula).

- Salda las cuentas 472 y 477 con su saldo a la fecha de fin del periodo: 477 (D) a 472 (H). La diferencia va a la **4750** si sale a ingresar o a la **4700** si sale a compensar o devolver.
- Si la declaración aplica cuotas a compensar de periodos anteriores, esa parte sale de la 4700 (H) y solo el resto va a la 4750.
- Se calcula canal a canal, como el resto de la contabilidad. Si ningún canal coincide con el resultado de la declaración, avisa (facturas contabilizadas después de presentar, apuntes manuales, periodos sin liquidar…).
- Las liquidaciones van en orden: no se puede contabilizar un periodo si hay otro posterior ya liquidado, ni anular uno que tenga otro posterior.
- La regularización por prorrata definitiva no está en la contabilidad: se avisa para registrarla a mano (634/639).
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
Operaciones intracomunitarias, importaciones e inversión del sujeto pasivo (hoy detienen el cálculo del 303/420); modelos 131, 111, 115, 190, 180, 349, 200 y 202; formato oficial de los resúmenes anuales 390/425 y ficheros de presentación; declaraciones complementarias y sustitutivas; regularización de bienes de inversión; criterio de caja, régimen simplificado y recargo de equivalencia como régimen de la empresa; deshacer un cobro/pago (extorno), remesas de recibos y ficheros SEPA, importación y conciliación de extractos bancarios, cuentas bancarias de clientes/proveedores; formato oficial AEAT de los libros registro y SII; libro registro de bienes de inversión; bienes de inversión (regularización del IVA durante 5/10 años), amortización degresiva y por tablas, diferencias entre amortización contable y fiscal; traspaso automático de los grupos 8 y 9 al subgrupo 13 en el cierre; asiento de regularización de la prorrata definitiva; modelo normal de cuentas anuales, estado de cambios en el patrimonio neto, flujos de efectivo y memoria; analítica y presupuestos; gestión de catálogos e impresos en la UI de administración.

## Estado de verificación
Compilado con el SDK de .NET 10 (sin errores ni avisos) y los 182 tests de `tests/Contabilidad.Tests` pasan (`dotnet test Contabilidad.slnx`). Con la base cargada desde los scripts 01→10 en PostgreSQL 16 se ha probado la API de punta a punta: login, administración, alta de facturas emitidas y recibidas con su asiento, mayor, vencimientos, cobro parcial y pago total, previsión de tesorería y modelos 420 y 347 (calcular, guardar y presentar). También el alta de empresas con el PGC Normal y PYMES, y en una de ellas cliente, factura, cobro y modelo 303. También la liquidación del IGIC de tres trimestres seguidos (a ingresar, a compensar y compensando lo anterior, con una venta repartida en tres canales) y el cierre del ejercicio con su apertura, deshacer y volver a cerrar, por la API y desde la web. Y los balances de sumas y saldos, situación y pérdidas y ganancias de esos dos ejercicios, con comparativo, por canal y en pantalla e impresión, y la aplicación del resultado de 2026/27 en 2027/28 (reserva legal, dividendos, voluntarias y remanente; anular y repetir). Y el inmovilizado: altas con y sin asiento, amortización por días en varias ejecuciones, anular, venta con pérdida, aviso en el cierre, en la API y en la web. Y los libros registro: numeración de recibidas al contabilizar y al aprobar borradores, facturas con dos tipos y rectificativas, totales que cuadran con el 420 y descarga del CSV desde la web.
