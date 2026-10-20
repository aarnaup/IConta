$Container = "adaral-postgres-13"
$User = "postgres"
$Database = "contabilidad"

docker exec $Container createdb -U $User $Database

if ($LASTEXITCODE -ne 0) {
    throw "No se pudo crear la base de datos."
}

$scripts = @(
    "01_schema.sql"
    "02_demo.sql"
    "03_facturacion_demo.sql"
    "04_seguridad.sql"
    "05_tesoreria.sql"
    "06_fiscal.sql"
    "07_pgc.sql"
    "08_aplicacion_resultado.sql"
    "09_inmovilizado.sql"
    "10_libros_registro.sql"
    "11_conciliacion.sql"
    "12_remesas.sql"
    "13_bienes_inversion.sql"
)

foreach ($script in $scripts) {
    Write-Host "Ejecutando $script..."

    docker exec $Container psql `
        -U $User `
        -d $Database `
        -v ON_ERROR_STOP=1 `
        -f "/tmp/database/$script"

    if ($LASTEXITCODE -ne 0) {
        throw "Error ejecutando $script. Proceso detenido."
    }
}

Write-Host "Todos los scripts se han ejecutado correctamente."