# syntax=docker/dockerfile:1
# Aplicación web de Contabilidad (estáticos + /config.js). Se construye desde la raíz del repositorio:
#   docker build -f src/Contabilidad.Web/Dockerfile -t iconta-web .
# Variable al arrancar: Api__Url (dirección pública de la API; vacía = mismo origen, detrás de un proxy).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Contabilidad.Web/Contabilidad.Web.csproj src/Contabilidad.Web/
RUN dotnet restore src/Contabilidad.Web/Contabilidad.Web.csproj
COPY src/Contabilidad.Web/ src/Contabilidad.Web/
RUN dotnet publish src/Contabilidad.Web/Contabilidad.Web.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Contabilidad.Web.dll"]
