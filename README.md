# Contabilidad (.NET 10 · Dapper · Autofac · Serilog · PostgreSQL)

## Estructura (`Contabilidad.slnx`)
- `database/` → `01_schema.sql` (esquema completo), `02_demo.sql` (empresa demo, ejercicio partido, canales), `03_facturacion_demo.sql` (IGIC, cuentas automáticas, plantillas de compra/venta, terceros y prorrata de ejemplo), `04_seguridad.sql` (usuarios, permisos por módulo, auditoría, borradores), `05_tesoreria.sql` (restricciones de vencimientos y formas de pago habituales), `06_fiscal.sql` (declaraciones: restricciones y orden de casillas), `07_pgc.sql` (cuadro de cuentas del PGC Normal y PYMES).
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

## Pendiente
Operaciones intracomunitarias, importaciones e inversión del sujeto pasivo (hoy detienen el cálculo del 303/420); modelos 131, 111, 115, 190, 180, 349, 200 y 202; formato oficial de los resúmenes anuales 390/425 y ficheros de presentación; declaraciones complementarias y sustitutivas; asiento de liquidación del IVA/IGIC; regularización de bienes de inversión; criterio de caja, régimen simplificado y recargo de equivalencia como régimen de la empresa; deshacer un cobro/pago (extorno), remesas de recibos y ficheros SEPA, importación y conciliación de extractos bancarios, cuentas bancarias de clientes/proveedores; libros registro; inmovilizado y bienes de inversión; analítica, presupuestos y balances; cierre de ejercicios; gestión de catálogos e impresos en la UI de administración.

## Estado de verificación
Compilado con el SDK de .NET 10 (sin errores ni avisos) y los 116 tests de `tests/Contabilidad.Tests` pasan (`dotnet test Contabilidad.slnx`). Con la base cargada desde los scripts 01→06 en PostgreSQL 16 se ha probado la API de punta a punta: login, administración, alta de facturas emitidas y recibidas con su asiento, mayor, vencimientos, cobro parcial y pago total, previsión de tesorería y modelos 420 y 347 (calcular, guardar y presentar). La UI web carga y permite entrar.
