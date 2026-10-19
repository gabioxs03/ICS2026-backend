# syntax=docker/dockerfile:1

# ---------- Etapa 1: build (SDK completo de .NET 8) ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Se copian primero solo los .csproj y se restauran los paquetes.
# Así Docker reutiliza esta capa si no cambian las dependencias.
COPY Dsw2025Tpi.sln .
COPY Dsw2025Tpi.Api/Dsw2025Tpi.Api.csproj Dsw2025Tpi.Api/
COPY Dsw2025Tpi.Application/Dsw2025Tpi.Application.csproj Dsw2025Tpi.Application/
COPY Dsw2025Tpi.Data/Dsw2025Tpi.Data.csproj Dsw2025Tpi.Data/
COPY Dsw2025Tpi.Domain/Dsw2025Tpi.Domain.csproj Dsw2025Tpi.Domain/
RUN dotnet restore Dsw2025Tpi.Api/Dsw2025Tpi.Api.csproj

# Ahora se copia el resto del código y se publica en Release.
COPY . .
RUN dotnet publish Dsw2025Tpi.Api/Dsw2025Tpi.Api.csproj -c Release -o /app/publish --no-restore

# ---------- Etapa 2: final (solo runtime de ASP.NET, imagen liviana) ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Variables de entorno por defecto (se pueden sobreescribir con -e o desde Azure)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Solo se copian los binarios compilados; el código fuente y el SDK no viajan.
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Dsw2025Tpi.Api.dll"]
