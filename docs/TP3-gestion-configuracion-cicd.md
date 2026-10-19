# TP3 — Gestión de Configuración y Pipeline CI/CD

**Ingeniería y Calidad de Software — UTN FRT — 2026 — Comisión 4K2**

Este documento describe cómo se gestiona el código del backend del e-commerce (`Dsw2025Tpi`) y cómo llega automáticamente desde GitHub hasta Azure. Cubre la estrategia de ramas, la contenerización con Docker, el pipeline de GitHub Actions, la conexión entre la API y la base de datos, y el acceso público a Swagger.

```
Desarrollador ──push/PR──▶ GitHub ──dispara──▶ GitHub Actions ──push imagen──▶ Docker Hub
                                                     │
                                                     └──deploy──▶ Azure App Service ──SQL──▶ Azure SQL Database
```

---

## 1. Estrategia de ramificación

### Ramas principales

| Rama | Propósito | ¿Se despliega? |
|---|---|---|
| `main` | Código estable, listo para producción. | Sí, en cada merge. |
| `development` | Rama de integración: acá se juntan las funcionalidades terminadas y revisadas. | Sí, en cada merge. |

### Ramas de trabajo

- `feature/<nombre-funcionalidad>`: para cada historia o funcionalidad nueva. Ejemplo: `feature/tp3-docker-cicd`.
- `hotfix/<descripcion>`: para corregir un error. Ejemplo: `hotfix/stock-negativo`.

Las ramas de trabajo se crean siempre desde `development`.

### Flujo de trabajo

1. `git checkout development && git pull`
2. `git checkout -b feature/mi-funcionalidad`
3. Se programa y se hacen commits en la rama propia.
4. `git push -u origin feature/mi-funcionalidad`
5. Se abre un **Pull Request** hacia `development`. El pipeline compila y construye la imagen para validar el cambio, sin desplegar.
6. Otro integrante revisa el PR (**Peer Review**) y lo aprueba.
7. Se fusiona el PR en `development`. El pipeline despliega automáticamente.
8. Cuando `development` está estable, se abre un PR de `development` hacia `main`, con el mismo proceso de revisión.

### Protección de ramas (Branch Protection Rules)

En GitHub: **Settings → Branches → Add branch ruleset** (o *Add classic branch protection rule*), una regla para `main` y otra para `development`:

- ✅ **Require a pull request before merging**
  - ✅ **Require approvals: 1**
- ✅ **Block force pushes**
- ✅ **Restrict deletions**

Con estas reglas no se puede hacer `push` directo a `main` ni a `development`. Todo cambio entra por un PR aprobado por al menos un par.

---

## 2. Dockerización de la API

### Conceptos

- **Dockerfile:** receta paso a paso para construir la imagen.
- **Imagen:** paquete de solo lectura con el código compilado, el runtime y las librerías.
- **Contenedor:** una imagen en ejecución. Si la imagen es la clase, el contenedor es el objeto.
- **Registro (Registry):** donde se guardan las imágenes. Este proyecto usa Docker Hub.
- **Multi-stage build:** se compila en una imagen pesada (SDK) y se copian solo los binarios a una imagen final liviana (runtime).

### Dockerfile (raíz del repositorio)

| Línea / bloque | Qué hace |
|---|---|
| `FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build` | Etapa de compilación con el SDK completo de .NET 8. |
| `COPY *.csproj` + `RUN dotnet restore` | Copia solo los proyectos y restaura los paquetes NuGet. Si las dependencias no cambian, Docker reutiliza esta capa y el build es más rápido. |
| `COPY . .` + `RUN dotnet publish ... -c Release -o /app/publish` | Copia el resto del código y publica la API en modo Release. |
| `FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final` | Etapa final: solo el runtime de ASP.NET Core, sin compiladores. Imagen más chica y más segura. |
| `ENV ASPNETCORE_URLS=http://+:8080` | La API escucha en el puerto 8080. |
| `ENV ASPNETCORE_ENVIRONMENT=Production` | Ambiente de ejecución: producción. |
| `EXPOSE 8080` | Documenta el puerto que expone el contenedor. |
| `COPY --from=build /app/publish .` | Copia solo los binarios de la etapa `build`. El código fuente y el SDK no llegan a la imagen final. |
| `ENTRYPOINT ["dotnet", "Dsw2025Tpi.Api.dll"]` | Comando que arranca la API al iniciar el contenedor. |

El archivo `.dockerignore` excluye `bin/`, `obj/`, `.git/` y otros archivos que no hacen falta para construir la imagen.

### Variables de entorno

La URL y el ambiente **no están fijos en el código**: se pasan como variables de entorno. Las de la imagen son valores por defecto y se pueden sobreescribir al correr el contenedor (`-e`) o desde Azure.

| Variable | Valor por defecto | Para qué sirve |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:8080` | Dirección y puerto donde escucha la API. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Ambiente (`Development` / `Production`). |
| `ConnectionStrings__Dsw2025TpiEntities` | *(se define en Azure)* | Cadena de conexión a la base de datos. Reemplaza la de `appsettings.json`. |

> ASP.NET Core lee las variables de entorno y las usa por encima de `appsettings.json`. El doble guion bajo (`__`) equivale al `:` de la configuración. Por eso `ConnectionStrings__Dsw2025TpiEntities` reemplaza a `ConnectionStrings:Dsw2025TpiEntities` sin cambiar código.

### Verificación local

Requisito: Docker Desktop instalado y abierto. En una terminal, en la raíz del repositorio:

```bash
# 1. Construir la imagen
docker build -t dsw2025tpi-api:local .

# 2. Levantar el contenedor (apuntando a una base SQL Server accesible)
docker run -d -p 8080:8080 --name mi-api-backend \
  -e "ConnectionStrings__Dsw2025TpiEntities=<cadena de conexión>" \
  dsw2025tpi-api:local

# 3. Ver los logs
docker logs -f mi-api-backend
```

Después se abre **http://localhost:8080/swagger** y se prueba, por ejemplo, `GET /api/products`. El endpoint **http://localhost:8080/healthcheck** debe responder `Healthy`.

> La base `(localdb)` de Visual Studio no es accesible desde un contenedor Linux. Para probar localmente se puede usar la cadena de conexión de la base de Azure (sección 4) o un SQL Server propio.

Para detener y borrar el contenedor: `docker rm -f mi-api-backend`.

---

## 3. Pipeline CI/CD con GitHub Actions

- **CI (Integración Continua):** cada cambio que llega al repositorio se compila y valida automáticamente.
- **CD (Despliegue Continuo):** si la validación pasa, la aplicación se publica sola en la nube.
- **GitHub Actions:** la herramienta de GitHub que ejecuta estos pasos en máquinas virtuales cuando ocurre un evento de git.

Archivo: `.github/workflows/deploy.yml`

### Disparadores (`on:`)

| Evento | Qué hace el pipeline |
|---|---|
| `push` a `development` o `main` (incluye el merge de un PR) | Ejecuta todo: compila, construye la imagen, la sube y despliega. |
| `pull_request` hacia `development` o `main` | Solo valida: compila y construye la imagen, **sin subir ni desplegar**. |
| `workflow_dispatch` | Permite correrlo a mano desde la pestaña *Actions* de GitHub. |

### Etapas

| # | Paso | Detalle |
|---|---|---|
| 1 | Obtener el código | `actions/checkout` clona el repositorio en la máquina virtual. |
| 2 | Configurar .NET 8 | Instala el SDK. |
| 3 | Restaurar paquetes | `dotnet restore` descarga las dependencias NuGet. |
| 4 | Compilar en limpio | `dotnet build -c Release`. Si hay errores de compilación, el pipeline se corta acá. |
| 5 | Construir la imagen Docker | Usa el `Dockerfile`. Se etiqueta con el **SHA del commit** (trazabilidad: qué versión del código generó la imagen) y con `latest`. |
| 6 | Login en Docker Hub | Con usuario y token guardados como secrets. *No corre en PR.* |
| 7 | Subir la imagen | `docker push` de ambas etiquetas. *No corre en PR.* |
| 8 | Desplegar en Azure | `azure/webapps-deploy` le indica al App Service que use la imagen nueva (la del SHA del commit). *No corre en PR.* |

Si cualquier paso falla, los siguientes no se ejecutan y **no se despliega nada**.

### Secrets de GitHub

En el repositorio: **Settings → Secrets and variables → Actions → New repository secret**.

| Secret | Contenido |
|---|---|
| `DOCKERHUB_USERNAME` | Usuario de Docker Hub. |
| `DOCKERHUB_TOKEN` | Personal Access Token de Docker Hub con permisos *Read, Write, Delete*. |
| `APP_SERVICE_NAME` | Nombre del App Service en Azure. |
| `AZURE_WEBAPP_PUBLISH_PROFILE` | Contenido completo del archivo *publish profile* descargado desde el App Service. |

Ninguna credencial queda escrita en el código ni en el workflow.

---

## 4. Infraestructura en Azure y conexión API ↔ base de datos

### 4.1 Docker Hub

1. Crear una cuenta en https://hub.docker.com/.
2. **Repositories → Create repository**, nombre `ics-backend`, visibilidad pública.
3. **Account settings → Personal access tokens → Generate new token**, permisos *Read, Write, Delete*. El token se copia en el secret `DOCKERHUB_TOKEN`.

### 4.2 Base de datos (Azure SQL Database, plan Free)

1. **SQL databases → Create**. Si no hay servidor, se crea uno con **SQL authentication**, usuario administrador y contraseña.
2. Aplicar la oferta gratuita (*Free offer*).
3. En el servidor SQL: **Networking → Allow Azure services and resources to access this server = Yes**. Sin esto, el App Service no puede conectarse.
4. En la base: **Connection strings → ADO.NET (SQL authentication)**. Se copia y se reemplaza `{your_password}` por la contraseña real.

Las tablas se crean solas: al arrancar, la API ejecuta las migraciones de Entity Framework (`Database.Migrate()`) y carga los administradores (`admins.json`) y los clientes (`customers.json`).

### 4.3 App Service (plan Free F1)

1. **App Services → Create → Web App**.
   - **Publish:** Container · **Operating System:** Linux · **Pricing plan:** Free F1.
2. Pestaña **Container**:
   - Image source: *Other container registries* · Access type: *Public*
   - Registry server URL: `https://index.docker.io`
   - Image and tag: `<usuario-dockerhub>/ics-backend:latest`
   - Port: `8080`
3. Una vez creado, **Settings → Environment variables → App settings**, agregar:

| Nombre | Valor |
|---|---|
| `ConnectionStrings__Dsw2025TpiEntities` | La cadena ADO.NET del paso 4.2 (con la contraseña). |
| `WEBSITES_PORT` | `8080` |

4. **Overview → Download publish profile**. El contenido del archivo se carga en el secret `AZURE_WEBAPP_PUBLISH_PROFILE`.

> En los planes Linux, la descarga del publish profile requiere habilitar **Configuration → General settings → SCM Basic Auth Publishing Credentials = On**.

**Cómo se conectan:** el App Service inyecta `ConnectionStrings__Dsw2025TpiEntities` como variable de entorno dentro del contenedor. ASP.NET Core la lee por encima de `appsettings.json`, así que la API usa la base de Azure sin que la cadena de conexión esté en el repositorio.

---

## 5. Swagger público

En `Program.cs`, Swagger estaba habilitado solo en el ambiente `Development`. Se movió fuera de esa condición para que funcione también en `Production`:

```csharp
app.UseSwagger();
app.UseSwaggerUI();
```

URL pública: **https://\<nombre-app-service\>.azurewebsites.net/swagger**

---

## 6. Deuda técnica conocida

- **Clave JWT en `appsettings.json`:** la clave de firma sigue escrita en el archivo y viaja dentro de la imagen. Debería pasarse como variable de entorno (`Jwt__Key`) en el App Service y eliminarse del repositorio (relacionado con REF-01 / AT-01 del TP1).
- **CORS:** solo permite `http://localhost:5173`. Cuando el frontend se despliegue, hay que agregar su dominio público.
