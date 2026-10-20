# syntax=docker/dockerfile:1
# API de Contabilidad. Se construye desde la raíz del repositorio (necesita Domain e Infrastructure):
#   docker build -f src/Contabilidad.Api/Dockerfile -t iconta-api .
# Variables obligatorias al arrancar: ConnectionStrings__Contabilidad, Jwt__Key (mín. 32 caracteres) y Cors__Origenes__0 (dirección de la web).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Primero solo los proyectos: la restauración de paquetes queda en caché mientras no cambien
COPY global.json Directory.Build.props ./
COPY src/Contabilidad.Domain/Contabilidad.Domain.csproj src/Contabilidad.Domain/
COPY src/Contabilidad.Infrastructure/Contabilidad.Infrastructure.csproj src/Contabilidad.Infrastructure/
COPY src/Contabilidad.Api/Contabilidad.Api.csproj src/Contabilidad.Api/
RUN dotnet restore src/Contabilidad.Api/Contabilidad.Api.csproj
COPY src/Contabilidad.Domain/ src/Contabilidad.Domain/
COPY src/Contabilidad.Infrastructure/ src/Contabilidad.Infrastructure/
COPY src/Contabilidad.Api/ src/Contabilidad.Api/
RUN dotnet publish src/Contabilidad.Api/Contabilidad.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Serilog escribe en logs/: la carpeta debe ser del usuario sin privilegios con el que se ejecuta
RUN mkdir -p /app/logs && chown "$APP_UID" /app/logs
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
VOLUME /app/logs
# Con argumentos ejecuta la CLI, p. ej.: docker run --rm -it -e ... iconta-api crear-superadmin tu@correo.es "Tu nombre"
ENTRYPOINT ["dotnet", "Contabilidad.Api.dll"]
