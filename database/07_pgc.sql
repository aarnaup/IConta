-- Cuadro de cuentas del Plan General de Contabilidad (RD 1514/2007) y del PGC de PYMES (RD 1515/2007),
-- con las modificaciones de los RD 602/2016 y 1/2021, hasta 4 dígitos.
-- Carga cat.cuenta_modelo, que se copia a cada empresa nueva al darla de alta.
-- Ejecutar DESPUÉS de 01_schema.sql. Se puede volver a ejecutar: actualiza nombres y no duplica.
--
-- Formato de cada línea: código | planes | nombre
--   planes: NP = Normal y PYMES, N = solo Normal, P = solo PYMES (cuando el nombre cambia en PYMES).
-- Nivel: 1 dígito grupo, 2 subgrupo, 3 cuenta, 4 subcuenta. Solo las subcuentas admiten apuntes;
-- a cada cuenta sin subcuentas en el PGC se le añade la subcuenta "xxx0" con el mismo nombre.

BEGIN;

CREATE TEMP TABLE pgc_fuente ON COMMIT DROP AS
SELECT trim(split_part(l, '|', 1)) AS codigo,
       trim(split_part(l, '|', 2)) AS planes,
       trim(split_part(l, '|', 3)) AS nombre
FROM regexp_split_to_table($pgc$
1|NP|Financiación básica
10|NP|Capital
100|NP|Capital social
101|NP|Fondo social
102|NP|Capital
103|NP|Socios por desembolsos no exigidos
1030|NP|Socios por desembolsos no exigidos, capital social
1034|NP|Socios por desembolsos no exigidos, capital pendiente de inscripción
104|NP|Socios por aportaciones no dinerarias pendientes
1040|NP|Socios por aportaciones no dinerarias pendientes, capital social
1044|NP|Socios por aportaciones no dinerarias pendientes, capital pendiente de inscripción
108|NP|Acciones o participaciones propias en situaciones especiales
109|NP|Acciones o participaciones propias para reducción de capital
11|NP|Reservas y otros instrumentos de patrimonio
110|NP|Prima de emisión o asunción
111|NP|Otros instrumentos de patrimonio neto
1110|NP|Patrimonio neto por emisión de instrumentos financieros compuestos
1111|NP|Resto de instrumentos de patrimonio neto
112|NP|Reserva legal
113|NP|Reservas voluntarias
114|NP|Reservas especiales
1140|NP|Reservas para acciones o participaciones de la sociedad dominante
1141|NP|Reservas estatutarias
1142|NP|Reserva por capital amortizado
1144|NP|Reservas por acciones propias aceptadas en garantía
115|N|Reservas por pérdidas y ganancias actuariales y otros ajustes
118|NP|Aportaciones de socios o propietarios
119|NP|Diferencias por ajuste del capital a euros
12|NP|Resultados pendientes de aplicación
120|NP|Remanente
121|NP|Resultados negativos de ejercicios anteriores
129|NP|Resultado del ejercicio
13|N|Subvenciones, donaciones y ajustes por cambio de valor
13|P|Subvenciones, donaciones y legados
130|NP|Subvenciones oficiales de capital
131|NP|Donaciones y legados de capital
132|NP|Otras subvenciones, donaciones y legados
133|N|Ajustes por valoración en activos financieros a valor razonable con cambios en el patrimonio neto
134|N|Operaciones de cobertura
1340|N|Cobertura de flujos de efectivo
1341|N|Cobertura de una inversión neta en un negocio en el extranjero
135|N|Diferencias de conversión
136|N|Ajustes por valoración en activos no corrientes y grupos enajenables de elementos, mantenidos para la venta
137|NP|Ingresos fiscales a distribuir en varios ejercicios
1370|NP|Ingresos fiscales por diferencias permanentes a distribuir en varios ejercicios
1371|NP|Ingresos fiscales por deducciones y bonificaciones a distribuir en varios ejercicios
14|NP|Provisiones
140|N|Provisión por retribuciones a largo plazo al personal
141|NP|Provisión para impuestos
142|NP|Provisión para otras responsabilidades
143|NP|Provisión por desmantelamiento, retiro o rehabilitación del inmovilizado
145|NP|Provisión para actuaciones medioambientales
146|N|Provisión para reestructuraciones
147|N|Provisión por transacciones con pagos basados en instrumentos de patrimonio
15|NP|Deudas a largo plazo con características especiales
150|NP|Acciones o participaciones a largo plazo consideradas como pasivos financieros
153|NP|Desembolsos no exigidos por acciones o participaciones consideradas como pasivos financieros
1533|NP|Desembolsos no exigidos, empresas del grupo
1534|NP|Desembolsos no exigidos, empresas asociadas
1535|NP|Desembolsos no exigidos, otras partes vinculadas
1536|NP|Otros desembolsos no exigidos
154|NP|Aportaciones no dinerarias pendientes por acciones o participaciones consideradas como pasivos financieros
1543|NP|Aportaciones no dinerarias pendientes, empresas del grupo
1544|NP|Aportaciones no dinerarias pendientes, empresas asociadas
1545|NP|Aportaciones no dinerarias pendientes, otras partes vinculadas
1546|NP|Otras aportaciones no dinerarias pendientes
16|NP|Deudas a largo plazo con partes vinculadas
160|NP|Deudas a largo plazo con entidades de crédito vinculadas
1603|NP|Deudas a largo plazo con entidades de crédito, empresas del grupo
1604|NP|Deudas a largo plazo con entidades de crédito, empresas asociadas
1605|NP|Deudas a largo plazo con otras entidades de crédito vinculadas
161|NP|Proveedores de inmovilizado a largo plazo, partes vinculadas
1613|NP|Proveedores de inmovilizado a largo plazo, empresas del grupo
1614|NP|Proveedores de inmovilizado a largo plazo, empresas asociadas
1615|NP|Proveedores de inmovilizado a largo plazo, otras partes vinculadas
162|NP|Acreedores por arrendamiento financiero a largo plazo, partes vinculadas
1623|NP|Acreedores por arrendamiento financiero a largo plazo, empresas del grupo
1624|NP|Acreedores por arrendamiento financiero a largo plazo, empresas asociadas
1625|NP|Acreedores por arrendamiento financiero a largo plazo, otras partes vinculadas
163|NP|Otras deudas a largo plazo con partes vinculadas
1633|NP|Otras deudas a largo plazo, empresas del grupo
1634|NP|Otras deudas a largo plazo, empresas asociadas
1635|NP|Otras deudas a largo plazo, otras partes vinculadas
17|NP|Deudas a largo plazo por préstamos recibidos, empréstitos y otros conceptos
170|NP|Deudas a largo plazo con entidades de crédito
171|NP|Deudas a largo plazo
172|NP|Deudas a largo plazo transformables en subvenciones, donaciones y legados
173|NP|Proveedores de inmovilizado a largo plazo
174|NP|Acreedores por arrendamiento financiero a largo plazo
175|NP|Efectos a pagar a largo plazo
176|NP|Pasivos por derivados financieros a largo plazo
1765|N|Pasivos por derivados financieros a largo plazo, cartera de negociación
1768|N|Pasivos por derivados financieros a largo plazo, instrumentos de cobertura
177|N|Obligaciones y bonos
178|N|Obligaciones y bonos convertibles
179|N|Deudas representadas en otros valores negociables
18|NP|Pasivos por fianzas, garantías y otros conceptos a largo plazo
180|NP|Fianzas recibidas a largo plazo
181|NP|Anticipos recibidos por ventas o prestaciones de servicios a largo plazo
185|NP|Depósitos recibidos a largo plazo
189|N|Garantías financieras a largo plazo
19|N|Situaciones transitorias de financiación
190|N|Acciones o participaciones emitidas
192|N|Suscriptores de acciones
194|N|Capital emitido pendiente de inscripción
195|N|Acciones o participaciones emitidas consideradas como pasivos financieros
197|N|Suscriptores de acciones consideradas como pasivos financieros
199|N|Acciones o participaciones emitidas consideradas como pasivos financieros pendientes de inscripción

2|NP|Activo no corriente
20|NP|Inmovilizaciones intangibles
200|NP|Investigación
201|NP|Desarrollo
202|NP|Concesiones administrativas
203|NP|Propiedad industrial
204|NP|Fondo de comercio
205|NP|Derechos de traspaso
206|NP|Aplicaciones informáticas
209|NP|Anticipos para inmovilizaciones intangibles
21|NP|Inmovilizaciones materiales
210|NP|Terrenos y bienes naturales
211|NP|Construcciones
212|NP|Instalaciones técnicas
213|NP|Maquinaria
214|NP|Utillaje
215|NP|Otras instalaciones
216|NP|Mobiliario
217|NP|Equipos para procesos de información
218|NP|Elementos de transporte
219|NP|Otro inmovilizado material
22|NP|Inversiones inmobiliarias
220|NP|Inversiones en terrenos y bienes naturales
221|NP|Inversiones en construcciones
23|NP|Inmovilizaciones materiales en curso
230|NP|Adaptación de terrenos y bienes naturales
231|NP|Construcciones en curso
232|NP|Instalaciones técnicas en montaje
233|NP|Maquinaria en montaje
237|NP|Equipos para procesos de información en montaje
239|NP|Anticipos para inmovilizaciones materiales
24|NP|Inversiones financieras a largo plazo en partes vinculadas
240|NP|Participaciones a largo plazo en partes vinculadas
2403|NP|Participaciones a largo plazo en empresas del grupo
2404|NP|Participaciones a largo plazo en empresas asociadas
2405|NP|Participaciones a largo plazo en otras partes vinculadas
241|NP|Valores representativos de deuda a largo plazo de partes vinculadas
2413|NP|Valores representativos de deuda a largo plazo de empresas del grupo
2414|NP|Valores representativos de deuda a largo plazo de empresas asociadas
2415|NP|Valores representativos de deuda a largo plazo de otras partes vinculadas
242|NP|Créditos a largo plazo a partes vinculadas
2423|NP|Créditos a largo plazo a empresas del grupo
2424|NP|Créditos a largo plazo a empresas asociadas
2425|NP|Créditos a largo plazo a otras partes vinculadas
249|NP|Desembolsos pendientes sobre participaciones a largo plazo en partes vinculadas
2493|NP|Desembolsos pendientes sobre participaciones a largo plazo en empresas del grupo
2494|NP|Desembolsos pendientes sobre participaciones a largo plazo en empresas asociadas
2495|NP|Desembolsos pendientes sobre participaciones a largo plazo en otras partes vinculadas
25|NP|Otras inversiones financieras a largo plazo
250|NP|Inversiones financieras a largo plazo en instrumentos de patrimonio
251|NP|Valores representativos de deuda a largo plazo
252|NP|Créditos a largo plazo
253|NP|Créditos a largo plazo por enajenación de inmovilizado
254|NP|Créditos a largo plazo al personal
255|NP|Activos por derivados financieros a largo plazo
2550|N|Activos por derivados financieros a largo plazo, cartera de negociación
2553|N|Activos por derivados financieros a largo plazo, instrumentos de cobertura
257|N|Derechos de reembolso derivados de contratos de seguro relativos a retribuciones a largo plazo al personal
258|NP|Imposiciones a largo plazo
259|NP|Desembolsos pendientes sobre participaciones en el patrimonio neto a largo plazo
26|NP|Fianzas y depósitos constituidos a largo plazo
260|NP|Fianzas constituidas a largo plazo
265|NP|Depósitos constituidos a largo plazo
28|NP|Amortización acumulada del inmovilizado
280|NP|Amortización acumulada del inmovilizado intangible
2800|NP|Amortización acumulada de investigación
2801|NP|Amortización acumulada de desarrollo
2802|NP|Amortización acumulada de concesiones administrativas
2803|NP|Amortización acumulada de propiedad industrial
2804|NP|Amortización acumulada de fondo de comercio
2805|NP|Amortización acumulada de derechos de traspaso
2806|NP|Amortización acumulada de aplicaciones informáticas
281|NP|Amortización acumulada del inmovilizado material
2811|NP|Amortización acumulada de construcciones
2812|NP|Amortización acumulada de instalaciones técnicas
2813|NP|Amortización acumulada de maquinaria
2814|NP|Amortización acumulada de utillaje
2815|NP|Amortización acumulada de otras instalaciones
2816|NP|Amortización acumulada de mobiliario
2817|NP|Amortización acumulada de equipos para procesos de información
2818|NP|Amortización acumulada de elementos de transporte
2819|NP|Amortización acumulada de otro inmovilizado material
282|NP|Amortización acumulada de las inversiones inmobiliarias
29|NP|Deterioro de valor de activos no corrientes
290|NP|Deterioro de valor del inmovilizado intangible
2900|NP|Deterioro de valor de investigación
2901|NP|Deterioro de valor de desarrollo
2902|NP|Deterioro de valor de concesiones administrativas
2903|NP|Deterioro de valor de propiedad industrial
2905|NP|Deterioro de valor de derechos de traspaso
2906|NP|Deterioro de valor de aplicaciones informáticas
291|NP|Deterioro de valor del inmovilizado material
2910|NP|Deterioro de valor de terrenos y bienes naturales
2911|NP|Deterioro de valor de construcciones
2912|NP|Deterioro de valor de instalaciones técnicas
2913|NP|Deterioro de valor de maquinaria
2914|NP|Deterioro de valor de utillaje
2915|NP|Deterioro de valor de otras instalaciones
2916|NP|Deterioro de valor de mobiliario
2917|NP|Deterioro de valor de equipos para procesos de información
2918|NP|Deterioro de valor de elementos de transporte
2919|NP|Deterioro de valor de otro inmovilizado material
292|NP|Deterioro de valor de las inversiones inmobiliarias
2920|NP|Deterioro de valor de los terrenos y bienes naturales
2921|NP|Deterioro de valor de construcciones
293|NP|Deterioro de valor de participaciones a largo plazo en partes vinculadas
2933|NP|Deterioro de valor de participaciones a largo plazo en empresas del grupo
2934|NP|Deterioro de valor de participaciones a largo plazo en empresas asociadas
2935|NP|Deterioro de valor de participaciones a largo plazo en otras partes vinculadas
294|NP|Deterioro de valor de valores representativos de deuda a largo plazo de partes vinculadas
2943|NP|Deterioro de valor de valores representativos de deuda a largo plazo de empresas del grupo
2944|NP|Deterioro de valor de valores representativos de deuda a largo plazo de empresas asociadas
2945|NP|Deterioro de valor de valores representativos de deuda a largo plazo de otras partes vinculadas
295|NP|Deterioro de valor de créditos a largo plazo a partes vinculadas
2953|NP|Deterioro de valor de créditos a largo plazo a empresas del grupo
2954|NP|Deterioro de valor de créditos a largo plazo a empresas asociadas
2955|NP|Deterioro de valor de créditos a largo plazo a otras partes vinculadas
296|NP|Deterioro de valor de participaciones en el patrimonio neto a largo plazo
297|NP|Deterioro de valor de valores representativos de deuda a largo plazo
298|NP|Deterioro de valor de créditos a largo plazo

3|NP|Existencias
30|NP|Comerciales
300|NP|Mercaderías A
301|NP|Mercaderías B
31|NP|Materias primas
310|NP|Materias primas A
311|NP|Materias primas B
32|NP|Otros aprovisionamientos
320|NP|Elementos y conjuntos incorporables
321|NP|Combustibles
322|NP|Repuestos
325|NP|Materiales diversos
326|NP|Embalajes
327|NP|Envases
328|NP|Material de oficina
33|NP|Productos en curso
330|NP|Productos en curso A
331|NP|Productos en curso B
34|NP|Productos semiterminados
340|NP|Productos semiterminados A
341|NP|Productos semiterminados B
35|NP|Productos terminados
350|NP|Productos terminados A
351|NP|Productos terminados B
36|NP|Subproductos, residuos y materiales recuperados
360|NP|Subproductos A
361|NP|Subproductos B
365|NP|Residuos A
366|NP|Residuos B
368|NP|Materiales recuperados A
369|NP|Materiales recuperados B
39|NP|Deterioro de valor de las existencias
390|NP|Deterioro de valor de las mercaderías
391|NP|Deterioro de valor de las materias primas
392|NP|Deterioro de valor de otros aprovisionamientos
393|NP|Deterioro de valor de los productos en curso
394|NP|Deterioro de valor de los productos semiterminados
395|NP|Deterioro de valor de los productos terminados
396|NP|Deterioro de valor de los subproductos, residuos y materiales recuperados

4|NP|Acreedores y deudores por operaciones comerciales
40|NP|Proveedores
400|NP|Proveedores
4000|NP|Proveedores (euros)
4004|NP|Proveedores (moneda extranjera)
4009|NP|Proveedores, facturas pendientes de recibir o de formalizar
401|NP|Proveedores, efectos comerciales a pagar
403|NP|Proveedores, empresas del grupo
4030|NP|Proveedores, empresas del grupo (euros)
4031|NP|Efectos comerciales a pagar, empresas del grupo
4034|NP|Proveedores, empresas del grupo (moneda extranjera)
4036|NP|Envases y embalajes a devolver a proveedores, empresas del grupo
4039|NP|Proveedores, empresas del grupo, facturas pendientes de recibir o de formalizar
404|NP|Proveedores, empresas asociadas
405|NP|Proveedores, otras partes vinculadas
406|NP|Envases y embalajes a devolver a proveedores
407|NP|Anticipos a proveedores
41|NP|Acreedores varios
410|NP|Acreedores por prestaciones de servicios
4100|NP|Acreedores por prestaciones de servicios (euros)
4104|NP|Acreedores por prestaciones de servicios (moneda extranjera)
4109|NP|Acreedores por prestaciones de servicios, facturas pendientes de recibir o de formalizar
411|NP|Acreedores, efectos comerciales a pagar
419|NP|Acreedores por operaciones en común
43|NP|Clientes
430|NP|Clientes
4300|NP|Clientes (euros)
4304|NP|Clientes (moneda extranjera)
4309|NP|Clientes, facturas pendientes de formalizar
431|NP|Clientes, efectos comerciales a cobrar
4310|NP|Efectos comerciales en cartera
4311|NP|Efectos comerciales descontados
4312|NP|Efectos comerciales en gestión de cobro
4315|NP|Efectos comerciales impagados
432|NP|Clientes, operaciones de factoring
433|NP|Clientes, empresas del grupo
4330|NP|Clientes, empresas del grupo (euros)
4331|NP|Efectos comerciales a cobrar, empresas del grupo
4332|NP|Clientes, empresas del grupo, operaciones de factoring
4334|NP|Clientes, empresas del grupo (moneda extranjera)
4336|NP|Clientes, empresas del grupo, de dudoso cobro
4337|NP|Envases y embalajes a devolver a clientes, empresas del grupo
4339|NP|Clientes, empresas del grupo, facturas pendientes de formalizar
434|NP|Clientes, empresas asociadas
435|NP|Clientes, otras partes vinculadas
436|NP|Clientes de dudoso cobro
437|NP|Envases y embalajes a devolver por clientes
438|NP|Anticipos de clientes
44|NP|Deudores varios
440|NP|Deudores
4400|NP|Deudores (euros)
4404|NP|Deudores (moneda extranjera)
4409|NP|Deudores, facturas pendientes de formalizar
441|NP|Deudores, efectos comerciales a cobrar
4410|NP|Deudores, efectos comerciales en cartera
4411|NP|Deudores, efectos comerciales descontados
4412|NP|Deudores, efectos comerciales en gestión de cobro
4415|NP|Deudores, efectos comerciales impagados
446|NP|Deudores de dudoso cobro
449|NP|Deudores por operaciones en común
46|NP|Personal
460|NP|Anticipos de remuneraciones
465|NP|Remuneraciones pendientes de pago
466|N|Remuneraciones mediante sistemas de aportación definida pendientes de pago
47|NP|Administraciones públicas
470|NP|Hacienda Pública, deudora por diversos conceptos
4700|NP|Hacienda Pública, deudora por IVA
4708|NP|Hacienda Pública, deudora por subvenciones concedidas
4709|NP|Hacienda Pública, deudora por devolución de impuestos
471|NP|Organismos de la Seguridad Social, deudores
472|NP|Hacienda Pública, IVA soportado
473|NP|Hacienda Pública, retenciones y pagos a cuenta
474|NP|Activos por impuesto diferido
4740|NP|Activos por diferencias temporarias deducibles
4742|NP|Derechos por deducciones y bonificaciones pendientes de aplicar
4745|NP|Crédito por pérdidas a compensar del ejercicio
475|NP|Hacienda Pública, acreedora por conceptos fiscales
4750|NP|Hacienda Pública, acreedora por IVA
4751|NP|Hacienda Pública, acreedora por retenciones practicadas
4752|NP|Hacienda Pública, acreedora por impuesto sobre sociedades
4758|NP|Hacienda Pública, acreedora por subvenciones a reintegrar
476|NP|Organismos de la Seguridad Social, acreedores
477|NP|Hacienda Pública, IVA repercutido
479|NP|Pasivos por diferencias temporarias imponibles
48|NP|Ajustes por periodificación
480|NP|Gastos anticipados
485|NP|Ingresos anticipados
49|NP|Deterioro de valor de créditos comerciales y provisiones a corto plazo
490|NP|Deterioro de valor de créditos por operaciones comerciales
493|NP|Deterioro de valor de créditos por operaciones comerciales con partes vinculadas
4933|NP|Deterioro de valor de créditos por operaciones comerciales con empresas del grupo
4934|NP|Deterioro de valor de créditos por operaciones comerciales con empresas asociadas
4935|NP|Deterioro de valor de créditos por operaciones comerciales con otras partes vinculadas
499|NP|Provisiones por operaciones comerciales
4994|NP|Provisión por contratos onerosos
4999|NP|Provisión para otras operaciones comerciales

5|NP|Cuentas financieras
50|NP|Empréstitos, deudas con características especiales y otras emisiones análogas a corto plazo
500|N|Obligaciones y bonos a corto plazo
501|N|Obligaciones y bonos convertibles a corto plazo
502|NP|Acciones o participaciones a corto plazo consideradas como pasivos financieros
505|N|Deudas representadas en otros valores negociables a corto plazo
506|N|Intereses a corto plazo de empréstitos y otras emisiones análogas
507|NP|Dividendos de acciones o participaciones consideradas como pasivos financieros
509|N|Valores negociables amortizados
5090|N|Obligaciones y bonos amortizados
5091|N|Obligaciones y bonos convertibles amortizados
5095|N|Otros valores negociables amortizados
51|NP|Deudas a corto plazo con partes vinculadas
510|NP|Deudas a corto plazo con entidades de crédito vinculadas
5103|NP|Deudas a corto plazo con entidades de crédito, empresas del grupo
5104|NP|Deudas a corto plazo con entidades de crédito, empresas asociadas
5105|NP|Deudas a corto plazo con otras entidades de crédito vinculadas
511|NP|Proveedores de inmovilizado a corto plazo, partes vinculadas
5113|NP|Proveedores de inmovilizado a corto plazo, empresas del grupo
5114|NP|Proveedores de inmovilizado a corto plazo, empresas asociadas
5115|NP|Proveedores de inmovilizado a corto plazo, otras partes vinculadas
512|NP|Acreedores por arrendamiento financiero a corto plazo, partes vinculadas
5123|NP|Acreedores por arrendamiento financiero a corto plazo, empresas del grupo
5124|NP|Acreedores por arrendamiento financiero a corto plazo, empresas asociadas
5125|NP|Acreedores por arrendamiento financiero a corto plazo, otras partes vinculadas
513|NP|Otras deudas a corto plazo con partes vinculadas
5133|NP|Otras deudas a corto plazo con empresas del grupo
5134|NP|Otras deudas a corto plazo con empresas asociadas
5135|NP|Otras deudas a corto plazo con otras partes vinculadas
514|NP|Intereses a corto plazo de deudas con partes vinculadas
5143|NP|Intereses a corto plazo de deudas, empresas del grupo
5144|NP|Intereses a corto plazo de deudas, empresas asociadas
5145|NP|Intereses a corto plazo de deudas, otras partes vinculadas
52|NP|Deudas a corto plazo por préstamos recibidos y otros conceptos
520|NP|Deudas a corto plazo con entidades de crédito
5200|NP|Préstamos a corto plazo de entidades de crédito
5201|NP|Deudas a corto plazo por crédito dispuesto
5208|NP|Deudas por efectos descontados
5209|NP|Deudas por operaciones de factoring
521|NP|Deudas a corto plazo
522|NP|Deudas a corto plazo transformables en subvenciones, donaciones y legados
523|NP|Proveedores de inmovilizado a corto plazo
524|NP|Acreedores por arrendamiento financiero a corto plazo
525|NP|Efectos a pagar a corto plazo
526|NP|Dividendo activo a pagar
527|NP|Intereses a corto plazo de deudas con entidades de crédito
528|NP|Intereses a corto plazo de deudas
529|NP|Provisiones a corto plazo
5290|N|Provisión a corto plazo por retribuciones al personal
5291|NP|Provisión a corto plazo para impuestos
5292|NP|Provisión a corto plazo para otras responsabilidades
5293|NP|Provisión a corto plazo por desmantelamiento, retiro o rehabilitación del inmovilizado
5295|NP|Provisión a corto plazo para actuaciones medioambientales
5296|N|Provisión a corto plazo para reestructuraciones
5297|N|Provisión a corto plazo por transacciones con pagos basados en instrumentos de patrimonio
53|NP|Inversiones financieras a corto plazo en partes vinculadas
530|NP|Participaciones a corto plazo en partes vinculadas
5303|NP|Participaciones a corto plazo en empresas del grupo
5304|NP|Participaciones a corto plazo en empresas asociadas
5305|NP|Participaciones a corto plazo en otras partes vinculadas
531|NP|Valores representativos de deuda a corto plazo de partes vinculadas
5313|NP|Valores representativos de deuda a corto plazo de empresas del grupo
5314|NP|Valores representativos de deuda a corto plazo de empresas asociadas
5315|NP|Valores representativos de deuda a corto plazo de otras partes vinculadas
532|NP|Créditos a corto plazo a partes vinculadas
5323|NP|Créditos a corto plazo a empresas del grupo
5324|NP|Créditos a corto plazo a empresas asociadas
5325|NP|Créditos a corto plazo a otras partes vinculadas
533|NP|Intereses a corto plazo de valores representativos de deuda de partes vinculadas
5333|NP|Intereses a corto plazo de valores representativos de deuda de empresas del grupo
5334|NP|Intereses a corto plazo de valores representativos de deuda de empresas asociadas
5335|NP|Intereses a corto plazo de valores representativos de deuda de otras partes vinculadas
534|NP|Intereses a corto plazo de créditos a partes vinculadas
5343|NP|Intereses a corto plazo de créditos a empresas del grupo
5344|NP|Intereses a corto plazo de créditos a empresas asociadas
5345|NP|Intereses a corto plazo de créditos a otras partes vinculadas
535|NP|Dividendo a cobrar de inversiones financieras en partes vinculadas
5353|NP|Dividendo a cobrar de empresas del grupo
5354|NP|Dividendo a cobrar de empresas asociadas
5355|NP|Dividendo a cobrar de otras partes vinculadas
539|NP|Desembolsos pendientes sobre participaciones a corto plazo en partes vinculadas
5393|NP|Desembolsos pendientes sobre participaciones a corto plazo en empresas del grupo
5394|NP|Desembolsos pendientes sobre participaciones a corto plazo en empresas asociadas
5395|NP|Desembolsos pendientes sobre participaciones a corto plazo en otras partes vinculadas
54|NP|Otras inversiones financieras a corto plazo
540|NP|Inversiones financieras a corto plazo en instrumentos de patrimonio
541|NP|Valores representativos de deuda a corto plazo
542|NP|Créditos a corto plazo
543|NP|Créditos a corto plazo por enajenación de inmovilizado
544|NP|Créditos a corto plazo al personal
545|NP|Dividendo a cobrar
546|NP|Intereses a corto plazo de valores representativos de deudas
547|NP|Intereses a corto plazo de créditos
548|NP|Imposiciones a corto plazo
549|NP|Desembolsos pendientes sobre participaciones en el patrimonio neto a corto plazo
55|NP|Otras cuentas no bancarias
550|NP|Titular de la explotación
551|NP|Cuenta corriente con socios y administradores
552|NP|Cuenta corriente con otras personas y entidades vinculadas
5523|NP|Cuenta corriente con empresas del grupo
5524|NP|Cuenta corriente con empresas asociadas
5525|NP|Cuenta corriente con otras partes vinculadas
553|N|Cuentas corrientes en fusiones y escisiones
5530|N|Socios de sociedad disuelta
5531|N|Socios, cuenta de fusión
5532|N|Socios de sociedad escindida
5533|N|Socios, cuenta de escisión
554|NP|Cuenta corriente con uniones temporales de empresas y comunidades de bienes
555|NP|Partidas pendientes de aplicación
556|NP|Desembolsos exigidos sobre participaciones en el patrimonio neto
5563|NP|Desembolsos exigidos sobre participaciones, empresas del grupo
5564|NP|Desembolsos exigidos sobre participaciones, empresas asociadas
5565|NP|Desembolsos exigidos sobre participaciones, otras partes vinculadas
5566|NP|Desembolsos exigidos sobre participaciones de otras empresas
557|NP|Dividendo activo a cuenta
558|NP|Socios por desembolsos exigidos
5580|NP|Socios por desembolsos exigidos sobre acciones o participaciones ordinarias
5585|NP|Socios por desembolsos exigidos sobre acciones o participaciones consideradas como pasivos financieros
559|NP|Derivados financieros a corto plazo
5590|N|Activos por derivados financieros a corto plazo, cartera de negociación
5593|N|Activos por derivados financieros a corto plazo, instrumentos de cobertura
5595|N|Pasivos por derivados financieros a corto plazo, cartera de negociación
5598|N|Pasivos por derivados financieros a corto plazo, instrumentos de cobertura
56|NP|Fianzas y depósitos recibidos y constituidos a corto plazo y ajustes por periodificación
560|NP|Fianzas recibidas a corto plazo
561|NP|Depósitos recibidos a corto plazo
565|NP|Fianzas constituidas a corto plazo
566|NP|Depósitos constituidos a corto plazo
567|NP|Intereses pagados por anticipado
568|NP|Intereses cobrados por anticipado
569|N|Garantías financieras a corto plazo
57|NP|Tesorería
570|NP|Caja, euros
571|NP|Caja, moneda extranjera
572|NP|Bancos e instituciones de crédito c/c vista, euros
573|NP|Bancos e instituciones de crédito c/c vista, moneda extranjera
574|NP|Bancos e instituciones de crédito, cuentas de ahorro, euros
575|NP|Bancos e instituciones de crédito, cuentas de ahorro, moneda extranjera
576|NP|Inversiones a corto plazo de gran liquidez
58|N|Activos no corrientes mantenidos para la venta y activos y pasivos asociados
580|N|Inmovilizado
581|N|Inversiones con personas y entidades vinculadas
582|N|Inversiones financieras
583|N|Existencias, deudores comerciales y otras cuentas a cobrar
584|N|Otros activos
585|N|Provisiones
586|N|Deudas con características especiales
587|N|Deudas con personas y entidades vinculadas
588|N|Acreedores comerciales y otras cuentas a pagar
589|N|Otros pasivos
59|N|Deterioro del valor de inversiones financieras a corto plazo y de activos no corrientes mantenidos para la venta
59|P|Deterioro del valor de inversiones financieras a corto plazo
593|NP|Deterioro de valor de participaciones a corto plazo en partes vinculadas
5933|NP|Deterioro de valor de participaciones a corto plazo en empresas del grupo
5934|NP|Deterioro de valor de participaciones a corto plazo en empresas asociadas
5935|NP|Deterioro de valor de participaciones a corto plazo en otras partes vinculadas
594|NP|Deterioro de valor de valores representativos de deuda a corto plazo de partes vinculadas
5943|NP|Deterioro de valor de valores representativos de deuda a corto plazo de empresas del grupo
5944|NP|Deterioro de valor de valores representativos de deuda a corto plazo de empresas asociadas
5945|NP|Deterioro de valor de valores representativos de deuda a corto plazo de otras partes vinculadas
595|NP|Deterioro de valor de créditos a corto plazo a partes vinculadas
5953|NP|Deterioro de valor de créditos a corto plazo a empresas del grupo
5954|NP|Deterioro de valor de créditos a corto plazo a empresas asociadas
5955|NP|Deterioro de valor de créditos a corto plazo a otras partes vinculadas
596|NP|Deterioro de valor de participaciones a corto plazo
597|NP|Deterioro de valor de valores representativos de deuda a corto plazo
598|NP|Deterioro de valor de créditos a corto plazo
599|N|Deterioro de valor de activos no corrientes mantenidos para la venta
5990|N|Deterioro de valor de inmovilizado no corriente mantenido para la venta
5991|N|Deterioro de valor de inversiones con personas y entidades vinculadas no corrientes mantenidas para la venta
5992|N|Deterioro de valor de inversiones financieras no corrientes mantenidas para la venta
5993|N|Deterioro de valor de existencias, deudores comerciales y otras cuentas a cobrar integrados en un grupo enajenable mantenido para la venta
5994|N|Deterioro de valor de otros activos mantenidos para la venta

6|NP|Compras y gastos
60|NP|Compras
600|NP|Compras de mercaderías
601|NP|Compras de materias primas
602|NP|Compras de otros aprovisionamientos
606|NP|Descuentos sobre compras por pronto pago
6060|NP|Descuentos sobre compras por pronto pago de mercaderías
6061|NP|Descuentos sobre compras por pronto pago de materias primas
6062|NP|Descuentos sobre compras por pronto pago de otros aprovisionamientos
607|NP|Trabajos realizados por otras empresas
608|NP|Devoluciones de compras y operaciones similares
6080|NP|Devoluciones de compras de mercaderías
6081|NP|Devoluciones de compras de materias primas
6082|NP|Devoluciones de compras de otros aprovisionamientos
609|NP|"Rappels" por compras
6090|NP|"Rappels" por compras de mercaderías
6091|NP|"Rappels" por compras de materias primas
6092|NP|"Rappels" por compras de otros aprovisionamientos
61|NP|Variación de existencias
610|NP|Variación de existencias de mercaderías
611|NP|Variación de existencias de materias primas
612|NP|Variación de existencias de otros aprovisionamientos
62|NP|Servicios exteriores
620|NP|Gastos en investigación y desarrollo del ejercicio
621|NP|Arrendamientos y cánones
622|NP|Reparaciones y conservación
623|NP|Servicios de profesionales independientes
624|NP|Transportes
625|NP|Primas de seguros
626|NP|Servicios bancarios y similares
627|NP|Publicidad, propaganda y relaciones públicas
628|NP|Suministros
629|NP|Otros servicios
63|NP|Tributos
630|NP|Impuesto sobre beneficios
6300|NP|Impuesto corriente
6301|NP|Impuesto diferido
631|NP|Otros tributos
633|NP|Ajustes negativos en la imposición sobre beneficios
634|NP|Ajustes negativos en la imposición indirecta
6341|NP|Ajustes negativos en IVA de activo corriente
6342|NP|Ajustes negativos en IVA de inversiones
636|NP|Devolución de impuestos
638|NP|Ajustes positivos en la imposición sobre beneficios
639|NP|Ajustes positivos en la imposición indirecta
6391|NP|Ajustes positivos en IVA de activo corriente
6392|NP|Ajustes positivos en IVA de inversiones
64|NP|Gastos de personal
640|NP|Sueldos y salarios
641|NP|Indemnizaciones
642|NP|Seguridad Social a cargo de la empresa
643|NP|Retribuciones a largo plazo mediante sistemas de aportación definida
644|N|Retribuciones a largo plazo mediante sistemas de prestación definida
6440|N|Contribuciones anuales
6442|N|Otros costes
645|N|Retribuciones al personal mediante instrumentos de patrimonio
6450|N|Retribuciones al personal liquidados con instrumentos de patrimonio
6457|N|Retribuciones al personal liquidados en efectivo basado en instrumentos de patrimonio
649|NP|Otros gastos sociales
65|NP|Otros gastos de gestión
650|NP|Pérdidas de créditos comerciales incobrables
651|NP|Resultados de operaciones en común
6510|NP|Beneficio transferido (gestor)
6511|NP|Pérdida soportada (partícipe o asociado no gestor)
659|NP|Otras pérdidas en gestión corriente
66|NP|Gastos financieros
660|NP|Gastos financieros por actualización de provisiones
661|N|Intereses de obligaciones y bonos
6610|N|Intereses de obligaciones y bonos a largo plazo, empresas del grupo
6611|N|Intereses de obligaciones y bonos a largo plazo, empresas asociadas
6612|N|Intereses de obligaciones y bonos a largo plazo, otras partes vinculadas
6613|N|Intereses de obligaciones y bonos a largo plazo, otras empresas
6615|N|Intereses de obligaciones y bonos a corto plazo, empresas del grupo
6616|N|Intereses de obligaciones y bonos a corto plazo, empresas asociadas
6617|N|Intereses de obligaciones y bonos a corto plazo, otras partes vinculadas
6618|N|Intereses de obligaciones y bonos a corto plazo, otras empresas
662|NP|Intereses de deudas
6620|NP|Intereses de deudas, empresas del grupo
6621|NP|Intereses de deudas, empresas asociadas
6622|NP|Intereses de deudas, otras partes vinculadas
6623|NP|Intereses de deudas con entidades de crédito
6624|NP|Intereses de deudas, otras empresas
663|NP|Pérdidas por valoración de instrumentos financieros por su valor razonable
664|NP|Gastos por dividendos de acciones o participaciones consideradas como pasivos financieros
6640|NP|Dividendos de pasivos, empresas del grupo
6641|NP|Dividendos de pasivos, empresas asociadas
6642|NP|Dividendos de pasivos, otras partes vinculadas
6643|NP|Dividendos de pasivos, otras empresas
665|NP|Intereses por descuento de efectos y operaciones de factoring
6650|NP|Intereses por descuento de efectos en entidades de crédito del grupo
6651|NP|Intereses por descuento de efectos en entidades de crédito asociadas
6652|NP|Intereses por descuento de efectos en otras entidades de crédito vinculadas
6653|NP|Intereses por descuento de efectos en otras entidades de crédito
6654|NP|Intereses por operaciones de factoring con entidades de crédito del grupo
6655|NP|Intereses por operaciones de factoring con entidades de crédito asociadas
6656|NP|Intereses por operaciones de factoring con otras entidades de crédito vinculadas
6657|NP|Intereses por operaciones de factoring con otras entidades de crédito
666|NP|Pérdidas en participaciones y valores representativos de deuda
6660|NP|Pérdidas en valores representativos de deuda a largo plazo, empresas del grupo
6661|NP|Pérdidas en valores representativos de deuda a largo plazo, empresas asociadas
6662|NP|Pérdidas en valores representativos de deuda a largo plazo, otras partes vinculadas
6663|NP|Pérdidas en participaciones y valores representativos de deuda a largo plazo, otras empresas
6665|NP|Pérdidas en participaciones y valores representativos de deuda a corto plazo, empresas del grupo
6666|NP|Pérdidas en participaciones y valores representativos de deuda a corto plazo, empresas asociadas
6667|NP|Pérdidas en valores representativos de deuda a corto plazo, otras partes vinculadas
6668|NP|Pérdidas en valores representativos de deuda a corto plazo, otras empresas
667|NP|Pérdidas de créditos no comerciales
6670|NP|Pérdidas de créditos a largo plazo, empresas del grupo
6671|NP|Pérdidas de créditos a largo plazo, empresas asociadas
6672|NP|Pérdidas de créditos a largo plazo, otras partes vinculadas
6673|NP|Pérdidas de créditos a largo plazo, otras empresas
6675|NP|Pérdidas de créditos a corto plazo, empresas del grupo
6676|NP|Pérdidas de créditos a corto plazo, empresas asociadas
6677|NP|Pérdidas de créditos a corto plazo, otras partes vinculadas
6678|NP|Pérdidas de créditos a corto plazo, otras empresas
668|NP|Diferencias negativas de cambio
669|NP|Otros gastos financieros
67|NP|Pérdidas procedentes de activos no corrientes y gastos excepcionales
670|NP|Pérdidas procedentes del inmovilizado intangible
671|NP|Pérdidas procedentes del inmovilizado material
672|NP|Pérdidas procedentes de las inversiones inmobiliarias
673|NP|Pérdidas procedentes de participaciones a largo plazo en partes vinculadas
6733|NP|Pérdidas procedentes de participaciones a largo plazo, empresas del grupo
6734|NP|Pérdidas procedentes de participaciones a largo plazo, empresas asociadas
6735|NP|Pérdidas procedentes de participaciones a largo plazo, otras partes vinculadas
675|N|Pérdidas por operaciones con obligaciones propias
678|NP|Gastos excepcionales
68|NP|Dotaciones para amortizaciones
680|NP|Amortización del inmovilizado intangible
681|NP|Amortización del inmovilizado material
682|NP|Amortización de las inversiones inmobiliarias
69|NP|Pérdidas por deterioro y otras dotaciones
690|NP|Pérdidas por deterioro del inmovilizado intangible
691|NP|Pérdidas por deterioro del inmovilizado material
692|NP|Pérdidas por deterioro de las inversiones inmobiliarias
693|NP|Pérdidas por deterioro de existencias
6930|NP|Pérdidas por deterioro de productos terminados y en curso de fabricación
6931|NP|Pérdidas por deterioro de mercaderías
6932|NP|Pérdidas por deterioro de materias primas
6933|NP|Pérdidas por deterioro de otros aprovisionamientos
694|NP|Pérdidas por deterioro de créditos por operaciones comerciales
695|NP|Dotación a la provisión por operaciones comerciales
6954|NP|Dotación a la provisión por contratos onerosos
6959|NP|Dotación a la provisión para otras operaciones comerciales
696|NP|Pérdidas por deterioro de participaciones y valores representativos de deuda a largo plazo
6960|NP|Pérdidas por deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, empresas del grupo
6961|NP|Pérdidas por deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, empresas asociadas
6962|NP|Pérdidas por deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, otras partes vinculadas
6963|NP|Pérdidas por deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, otras empresas
6965|NP|Pérdidas por deterioro en valores representativos de deuda a largo plazo, empresas del grupo
6966|NP|Pérdidas por deterioro en valores representativos de deuda a largo plazo, empresas asociadas
6967|NP|Pérdidas por deterioro en valores representativos de deuda a largo plazo, otras partes vinculadas
6968|NP|Pérdidas por deterioro en valores representativos de deuda a largo plazo, otras empresas
697|NP|Pérdidas por deterioro de créditos a largo plazo
6970|NP|Pérdidas por deterioro de créditos a largo plazo, empresas del grupo
6971|NP|Pérdidas por deterioro de créditos a largo plazo, empresas asociadas
6972|NP|Pérdidas por deterioro de créditos a largo plazo, otras partes vinculadas
6973|NP|Pérdidas por deterioro de créditos a largo plazo, otras empresas
698|NP|Pérdidas por deterioro de participaciones y valores representativos de deuda a corto plazo
6980|NP|Pérdidas por deterioro de participaciones en instrumentos de patrimonio neto a corto plazo, empresas del grupo
6981|NP|Pérdidas por deterioro de participaciones en instrumentos de patrimonio neto a corto plazo, empresas asociadas
6985|NP|Pérdidas por deterioro en valores representativos de deuda a corto plazo, empresas del grupo
6986|NP|Pérdidas por deterioro en valores representativos de deuda a corto plazo, empresas asociadas
6987|NP|Pérdidas por deterioro en valores representativos de deuda a corto plazo, otras partes vinculadas
6988|NP|Pérdidas por deterioro en valores representativos de deuda a corto plazo, otras empresas
699|NP|Pérdidas por deterioro de créditos a corto plazo
6990|NP|Pérdidas por deterioro de créditos a corto plazo, empresas del grupo
6991|NP|Pérdidas por deterioro de créditos a corto plazo, empresas asociadas
6992|NP|Pérdidas por deterioro de créditos a corto plazo, otras partes vinculadas
6993|NP|Pérdidas por deterioro de créditos a corto plazo, otras empresas

7|NP|Ventas e ingresos
70|NP|Ventas de mercaderías, de producción propia, de servicios, etc.
700|NP|Ventas de mercaderías
701|NP|Ventas de productos terminados
702|NP|Ventas de productos semiterminados
703|NP|Ventas de subproductos y residuos
704|NP|Ventas de envases y embalajes
705|NP|Prestaciones de servicios
706|NP|Descuentos sobre ventas por pronto pago
7060|NP|Descuentos sobre ventas por pronto pago de mercaderías
7061|NP|Descuentos sobre ventas por pronto pago de productos terminados
7062|NP|Descuentos sobre ventas por pronto pago de productos semiterminados
7063|NP|Descuentos sobre ventas por pronto pago de subproductos y residuos
708|NP|Devoluciones de ventas y operaciones similares
7080|NP|Devoluciones de ventas de mercaderías
7081|NP|Devoluciones de ventas de productos terminados
7082|NP|Devoluciones de ventas de productos semiterminados
7083|NP|Devoluciones de ventas de subproductos y residuos
7084|NP|Devoluciones de ventas de envases y embalajes
709|NP|"Rappels" sobre ventas
7090|NP|"Rappels" sobre ventas de mercaderías
7091|NP|"Rappels" sobre ventas de productos terminados
7092|NP|"Rappels" sobre ventas de productos semiterminados
7093|NP|"Rappels" sobre ventas de subproductos y residuos
7094|NP|"Rappels" sobre ventas de envases y embalajes
71|NP|Variación de existencias
710|NP|Variación de existencias de productos en curso
711|NP|Variación de existencias de productos semiterminados
712|NP|Variación de existencias de productos terminados
713|NP|Variación de existencias de subproductos, residuos y materiales recuperados
73|NP|Trabajos realizados para la empresa
730|NP|Trabajos realizados para el inmovilizado intangible
731|NP|Trabajos realizados para el inmovilizado material
732|NP|Trabajos realizados en inversiones inmobiliarias
733|NP|Trabajos realizados para el inmovilizado material en curso
74|NP|Subvenciones, donaciones y legados
740|NP|Subvenciones, donaciones y legados a la explotación
746|NP|Subvenciones, donaciones y legados de capital transferidos al resultado del ejercicio
747|NP|Otras subvenciones, donaciones y legados transferidos al resultado del ejercicio
75|NP|Otros ingresos de gestión
751|NP|Resultados de operaciones en común
7510|NP|Pérdida transferida (gestor)
7511|NP|Beneficio atribuido (partícipe o asociado no gestor)
752|NP|Ingresos por arrendamientos
753|NP|Ingresos de propiedad industrial cedida en explotación
754|NP|Ingresos por comisiones
755|NP|Ingresos por servicios al personal
759|NP|Ingresos por servicios diversos
76|NP|Ingresos financieros
760|NP|Ingresos de participaciones en instrumentos de patrimonio
7600|NP|Ingresos de participaciones en instrumentos de patrimonio, empresas del grupo
7601|NP|Ingresos de participaciones en instrumentos de patrimonio, empresas asociadas
7602|NP|Ingresos de participaciones en instrumentos de patrimonio, otras partes vinculadas
7603|NP|Ingresos de participaciones en instrumentos de patrimonio, otras empresas
761|NP|Ingresos de valores representativos de deuda
7610|NP|Ingresos de valores representativos de deuda, empresas del grupo
7611|NP|Ingresos de valores representativos de deuda, empresas asociadas
7612|NP|Ingresos de valores representativos de deuda, otras partes vinculadas
7613|NP|Ingresos de valores representativos de deuda, otras empresas
762|NP|Ingresos de créditos
7620|NP|Ingresos de créditos a largo plazo
7621|NP|Ingresos de créditos a corto plazo
763|NP|Beneficios por valoración de instrumentos financieros por su valor razonable
766|NP|Beneficios en participaciones y valores representativos de deuda
7660|NP|Beneficios en valores representativos de deuda a largo plazo, empresas del grupo
7661|NP|Beneficios en valores representativos de deuda a largo plazo, empresas asociadas
7662|NP|Beneficios en valores representativos de deuda a largo plazo, otras partes vinculadas
7663|NP|Beneficios en participaciones y valores representativos de deuda a largo plazo, otras empresas
7665|NP|Beneficios en participaciones y valores representativos de deuda a corto plazo, empresas del grupo
7666|NP|Beneficios en participaciones y valores representativos de deuda a corto plazo, empresas asociadas
7667|NP|Beneficios en valores representativos de deuda a corto plazo, otras partes vinculadas
7668|NP|Beneficios en valores representativos de deuda a corto plazo, otras empresas
767|N|Ingresos de activos afectos y de derechos de reembolso relativos a retribuciones a largo plazo
768|NP|Diferencias positivas de cambio
769|NP|Otros ingresos financieros
77|NP|Beneficios procedentes de activos no corrientes e ingresos excepcionales
770|NP|Beneficios procedentes del inmovilizado intangible
771|NP|Beneficios procedentes del inmovilizado material
772|NP|Beneficios procedentes de las inversiones inmobiliarias
773|NP|Beneficios procedentes de participaciones a largo plazo en partes vinculadas
7733|NP|Beneficios procedentes de participaciones a largo plazo, empresas del grupo
7734|NP|Beneficios procedentes de participaciones a largo plazo, empresas asociadas
7735|NP|Beneficios procedentes de participaciones a largo plazo, otras partes vinculadas
774|N|Diferencia negativa en combinaciones de negocios
775|N|Beneficios por operaciones con obligaciones propias
778|NP|Ingresos excepcionales
79|NP|Excesos y aplicaciones de provisiones y de pérdidas por deterioro
790|NP|Reversión del deterioro del inmovilizado intangible
791|NP|Reversión del deterioro del inmovilizado material
792|NP|Reversión del deterioro de las inversiones inmobiliarias
793|NP|Reversión del deterioro de existencias
7930|NP|Reversión del deterioro de productos terminados y en curso de fabricación
7931|NP|Reversión del deterioro de mercaderías
7932|NP|Reversión del deterioro de materias primas
7933|NP|Reversión del deterioro de otros aprovisionamientos
794|NP|Reversión del deterioro de créditos por operaciones comerciales
795|NP|Exceso de provisiones
7950|N|Exceso de provisión por retribuciones al personal
7951|NP|Exceso de provisión para impuestos
7952|NP|Exceso de provisión para otras responsabilidades
7954|NP|Exceso de provisión por operaciones comerciales
7955|NP|Exceso de provisión para actuaciones medioambientales
7956|N|Exceso de provisión para reestructuraciones
7957|N|Exceso de provisión por transacciones con pagos basados en instrumentos de patrimonio
796|NP|Reversión del deterioro de participaciones y valores representativos de deuda a largo plazo
7960|NP|Reversión del deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, empresas del grupo
7961|NP|Reversión del deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, empresas asociadas
7962|NP|Reversión del deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, otras partes vinculadas
7963|NP|Reversión del deterioro de participaciones en instrumentos de patrimonio neto a largo plazo, otras empresas
7965|NP|Reversión del deterioro de valores representativos de deuda a largo plazo, empresas del grupo
7966|NP|Reversión del deterioro de valores representativos de deuda a largo plazo, empresas asociadas
7967|NP|Reversión del deterioro de valores representativos de deuda a largo plazo, otras partes vinculadas
7968|NP|Reversión del deterioro de valores representativos de deuda a largo plazo, otras empresas
797|NP|Reversión del deterioro de créditos a largo plazo
7970|NP|Reversión del deterioro de créditos a largo plazo, empresas del grupo
7971|NP|Reversión del deterioro de créditos a largo plazo, empresas asociadas
7972|NP|Reversión del deterioro de créditos a largo plazo, otras partes vinculadas
7973|NP|Reversión del deterioro de créditos a largo plazo, otras empresas
798|NP|Reversión del deterioro de participaciones y valores representativos de deuda a corto plazo
7980|NP|Reversión del deterioro de participaciones en instrumentos de patrimonio neto a corto plazo, empresas del grupo
7981|NP|Reversión del deterioro de participaciones en instrumentos de patrimonio neto a corto plazo, empresas asociadas
7985|NP|Reversión del deterioro de valores representativos de deuda a corto plazo, empresas del grupo
7986|NP|Reversión del deterioro de valores representativos de deuda a corto plazo, empresas asociadas
7987|NP|Reversión del deterioro de valores representativos de deuda a corto plazo, otras partes vinculadas
7988|NP|Reversión del deterioro de valores representativos de deuda a corto plazo, otras empresas
799|NP|Reversión del deterioro de créditos a corto plazo
7990|NP|Reversión del deterioro de créditos a corto plazo, empresas del grupo
7991|NP|Reversión del deterioro de créditos a corto plazo, empresas asociadas
7992|NP|Reversión del deterioro de créditos a corto plazo, otras partes vinculadas
7993|NP|Reversión del deterioro de créditos a corto plazo, otras empresas

8|N|Gastos imputados al patrimonio neto
80|N|Gastos financieros por valoración de activos y pasivos
800|N|Pérdidas en activos financieros a valor razonable con cambios en el patrimonio neto
802|N|Transferencia de beneficios en activos financieros a valor razonable con cambios en el patrimonio neto
81|N|Gastos en operaciones de cobertura
810|N|Pérdidas por coberturas de flujos de efectivo
811|N|Pérdidas por coberturas de inversiones netas en un negocio en el extranjero
812|N|Transferencia de beneficios por coberturas de flujos de efectivo
813|N|Transferencia de beneficios por coberturas de inversiones netas en un negocio en el extranjero
82|N|Gastos por diferencias de conversión
820|N|Diferencias de conversión negativas
821|N|Transferencia de diferencias de conversión positivas
83|N|Impuesto sobre beneficios
830|N|Impuesto sobre beneficios
8300|N|Impuesto corriente
8301|N|Impuesto diferido
833|N|Ajustes negativos en la imposición sobre beneficios
834|N|Ingresos fiscales por diferencias permanentes
835|N|Ingresos fiscales por deducciones y bonificaciones
836|N|Transferencia de diferencias permanentes
837|N|Transferencia de deducciones y bonificaciones
838|N|Ajustes positivos en la imposición sobre beneficios
84|N|Transferencias de subvenciones, donaciones y legados
840|N|Transferencia de subvenciones oficiales de capital
841|N|Transferencia de donaciones y legados de capital
842|N|Transferencia de otras subvenciones, donaciones y legados
85|N|Gastos por pérdidas actuariales y ajustes en los activos por retribuciones a largo plazo de prestación definida
850|N|Pérdidas actuariales
851|N|Ajustes negativos en activos por retribuciones a largo plazo de prestación definida
86|N|Gastos por activos no corrientes en venta
860|N|Pérdidas en activos no corrientes y grupos enajenables de elementos mantenidos para la venta
862|N|Transferencia de beneficios en activos no corrientes y grupos enajenables de elementos mantenidos para la venta
89|N|Gastos de participaciones en empresas del grupo o asociadas con ajustes valorativos positivos previos
891|N|Deterioro de participaciones en el patrimonio, empresas del grupo
892|N|Deterioro de participaciones en el patrimonio, empresas asociadas

9|N|Ingresos imputados al patrimonio neto
90|N|Ingresos financieros por valoración de activos y pasivos
900|N|Beneficios en activos financieros a valor razonable con cambios en el patrimonio neto
902|N|Transferencia de pérdidas de activos financieros a valor razonable con cambios en el patrimonio neto
91|N|Ingresos en operaciones de cobertura
910|N|Beneficios por coberturas de flujos de efectivo
911|N|Beneficios por coberturas de una inversión neta en un negocio en el extranjero
912|N|Transferencia de pérdidas por coberturas de flujos de efectivo
913|N|Transferencia de pérdidas por coberturas de una inversión neta en un negocio en el extranjero
92|N|Ingresos por diferencias de conversión
920|N|Diferencias de conversión positivas
921|N|Transferencia de diferencias de conversión negativas
94|N|Ingresos por subvenciones, donaciones y legados
940|N|Ingresos de subvenciones oficiales de capital
941|N|Ingresos de donaciones y legados de capital
942|N|Ingresos de otras subvenciones, donaciones y legados
95|N|Ingresos por ganancias actuariales y ajustes en los activos por retribuciones a largo plazo de prestación definida
950|N|Ganancias actuariales
951|N|Ajustes positivos en activos por retribuciones a largo plazo de prestación definida
96|N|Ingresos por activos no corrientes en venta
960|N|Beneficios en activos no corrientes y grupos enajenables de elementos mantenidos para la venta
962|N|Transferencia de pérdidas en activos no corrientes y grupos enajenables de elementos mantenidos para la venta
99|N|Ingresos de participaciones en empresas del grupo o asociadas con ajustes valorativos negativos previos
991|N|Recuperación de ajustes valorativos negativos previos, empresas del grupo
992|N|Recuperación de ajustes valorativos negativos previos, empresas asociadas
993|N|Transferencia por deterioro de ajustes valorativos negativos previos, empresas del grupo
994|N|Transferencia por deterioro de ajustes valorativos negativos previos, empresas asociadas
$pgc$, '\r?\n') AS l
WHERE trim(l) <> '';

-- Naturaleza por prefijo: gana el prefijo más largo que coincida con el código.
CREATE TEMP TABLE pgc_naturaleza (prefijo text PRIMARY KEY, naturaleza text NOT NULL) ON COMMIT DROP;
INSERT INTO pgc_naturaleza VALUES
  -- por grupo
  ('1','ACREEDORA'), ('2','DEUDORA'), ('3','DEUDORA'), ('4','MIXTA'), ('5','MIXTA'),
  ('6','DEUDORA'), ('7','ACREEDORA'), ('8','DEUDORA'), ('9','ACREEDORA'),
  -- grupo 1: cuentas correctoras del patrimonio y saldos que pueden cambiar de signo
  ('103','DEUDORA'), ('104','DEUDORA'), ('108','DEUDORA'), ('109','DEUDORA'), ('121','DEUDORA'),
  ('129','MIXTA'), ('133','MIXTA'), ('134','MIXTA'), ('135','MIXTA'), ('136','MIXTA'),
  ('153','DEUDORA'), ('154','DEUDORA'), ('190','DEUDORA'), ('192','DEUDORA'), ('195','DEUDORA'), ('197','DEUDORA'),
  -- grupos 2 y 3: amortizaciones, deterioros y desembolsos pendientes
  ('249','ACREEDORA'), ('259','ACREEDORA'), ('28','ACREEDORA'), ('29','ACREEDORA'), ('39','ACREEDORA'),
  -- grupo 4
  ('40','ACREEDORA'), ('4036','DEUDORA'), ('406','DEUDORA'), ('407','DEUDORA'), ('41','ACREEDORA'),
  ('43','DEUDORA'), ('4337','ACREEDORA'), ('437','ACREEDORA'), ('438','ACREEDORA'), ('44','DEUDORA'),
  ('460','DEUDORA'), ('465','ACREEDORA'), ('466','ACREEDORA'),
  ('470','DEUDORA'), ('471','DEUDORA'), ('472','DEUDORA'), ('473','DEUDORA'), ('474','DEUDORA'),
  ('475','ACREEDORA'), ('476','ACREEDORA'), ('477','ACREEDORA'), ('479','ACREEDORA'),
  ('480','DEUDORA'), ('485','ACREEDORA'), ('49','ACREEDORA'),
  -- grupo 5
  ('50','ACREEDORA'), ('509','DEUDORA'), ('51','ACREEDORA'), ('52','ACREEDORA'),
  ('53','DEUDORA'), ('539','ACREEDORA'), ('54','DEUDORA'), ('549','ACREEDORA'),
  ('556','ACREEDORA'), ('557','DEUDORA'), ('558','DEUDORA'),
  ('560','ACREEDORA'), ('561','ACREEDORA'), ('565','DEUDORA'), ('566','DEUDORA'), ('567','DEUDORA'),
  ('568','ACREEDORA'), ('569','ACREEDORA'), ('57','DEUDORA'),
  ('580','DEUDORA'), ('581','DEUDORA'), ('582','DEUDORA'), ('583','DEUDORA'), ('584','DEUDORA'),
  ('585','ACREEDORA'), ('586','ACREEDORA'), ('587','ACREEDORA'), ('588','ACREEDORA'), ('589','ACREEDORA'),
  ('59','ACREEDORA'),
  -- grupos 6 y 7: cuentas que minoran compras o ventas y variaciones de existencias
  ('606','ACREEDORA'), ('608','ACREEDORA'), ('609','ACREEDORA'), ('61','MIXTA'),
  ('636','ACREEDORA'), ('638','ACREEDORA'), ('639','ACREEDORA'),
  ('706','DEUDORA'), ('708','DEUDORA'), ('709','DEUDORA'), ('71','MIXTA'),
  -- grupo 8: el impuesto puede ser gasto o ingreso
  ('83','MIXTA');

DO $$
DECLARE v_huerfanas text;
BEGIN
  IF EXISTS (SELECT 1 FROM pgc_fuente WHERE codigo !~ '^[0-9]{1,4}$' OR planes NOT IN ('NP','N','P') OR nombre = '') THEN
    RAISE EXCEPTION 'Línea mal formada en el cuadro de cuentas: %',
      (SELECT string_agg(codigo || '|' || planes || '|' || nombre, '; ') FROM pgc_fuente
       WHERE codigo !~ '^[0-9]{1,4}$' OR planes NOT IN ('NP','N','P') OR nombre = '');
  END IF;

  -- Un mismo código no puede aparecer dos veces para el mismo plan
  IF EXISTS (SELECT 1 FROM pgc_fuente f, unnest(CASE f.planes WHEN 'NP' THEN ARRAY['N','P'] ELSE ARRAY[f.planes] END) p
             GROUP BY f.codigo, p HAVING count(*) > 1) THEN
    RAISE EXCEPTION 'Hay códigos repetidos en el cuadro de cuentas.';
  END IF;

  -- Cuentas por plan, con las subcuentas "xxx0" de las cuentas que no tienen ninguna
  CREATE TEMP TABLE pgc_plan ON COMMIT DROP AS
  WITH base AS (
    SELECT pm.id AS plan_id, f.codigo, f.nombre
    FROM pgc_fuente f
    JOIN cat.plan_modelo pm ON (pm.codigo = 'PGC_NORMAL' AND f.planes IN ('NP','N'))
                            OR (pm.codigo = 'PGC_PYMES'  AND f.planes IN ('NP','P'))
  )
  SELECT plan_id, codigo, nombre FROM base
  UNION ALL
  SELECT c.plan_id, c.codigo || '0', c.nombre FROM base c
  WHERE length(c.codigo) = 3
    AND NOT EXISTS (SELECT 1 FROM base s WHERE s.plan_id = c.plan_id AND length(s.codigo) = 4 AND left(s.codigo, 3) = c.codigo);

  -- Cada cuenta debe colgar de otra del mismo plan
  SELECT string_agg(DISTINCT p.codigo, ', ') INTO v_huerfanas
  FROM pgc_plan p
  WHERE length(p.codigo) > 1
    AND NOT EXISTS (SELECT 1 FROM pgc_plan q WHERE q.plan_id = p.plan_id AND q.codigo = left(p.codigo, length(p.codigo) - 1));
  IF v_huerfanas IS NOT NULL THEN
    RAISE EXCEPTION 'Cuentas sin cuenta padre en su plan: %', v_huerfanas;
  END IF;

  INSERT INTO cat.cuenta_modelo (plan_modelo_id, codigo, nombre, nivel, padre_codigo, naturaleza)
  SELECT p.plan_id, p.codigo, p.nombre, length(p.codigo),
         CASE WHEN length(p.codigo) > 1 THEN left(p.codigo, length(p.codigo) - 1) END,
         (SELECT n.naturaleza FROM pgc_naturaleza n WHERE p.codigo LIKE n.prefijo || '%'
          ORDER BY length(n.prefijo) DESC LIMIT 1)
  FROM pgc_plan p
  ON CONFLICT (plan_modelo_id, codigo) DO UPDATE
    SET nombre = EXCLUDED.nombre, nivel = EXCLUDED.nivel,
        padre_codigo = EXCLUDED.padre_codigo, naturaleza = EXCLUDED.naturaleza;
END $$;

COMMIT;

-- Resumen
SELECT pm.codigo AS plan, count(*) AS cuentas, count(*) FILTER (WHERE m.nivel = 4) AS subcuentas
FROM cat.cuenta_modelo m JOIN cat.plan_modelo pm ON pm.id = m.plan_modelo_id
GROUP BY pm.codigo ORDER BY pm.codigo;
