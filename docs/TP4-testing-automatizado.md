# TP4 — Testing Automatizado: Informe Técnico de Pruebas

**Ingeniería y Calidad de Software — UTN FRT — 2026 — Comisión 4K2**

| Integrante | Legajo |
|---|---|
| Cristo Ochi, Rocío | 53183 |
| García Matar, Francisco | 53826 |
| Redel, Tadeo | 57132 |
| Samaniego, Leopoldo Gabriel | 53447 |

Este documento describe la suite de pruebas automatizadas del backend del e-commerce (`Dsw2025Tpi`), cómo se ejecuta y cómo se integró al pipeline de GitHub Actions del TP3 para que **ningún cambio con pruebas fallidas llegue a producción**.

---

## 1. Estrategia: pirámide de testing

```
                 ▲   E2E (Newman)            1 flujo completo, 11 requests, 24 aserciones
                ▲▲▲  Integración (SQLite)    9 pruebas: controller + JWT + EF Core
             ▲▲▲▲▲▲▲ Unitarias (xUnit+Moq)   10 pruebas: lógica de negocio aislada
```

| Nivel | Qué valida | Herramientas | Base de datos | Velocidad |
|---|---|---|---|---|
| Unitarias | Reglas de negocio de `ProductsManagementService` y `OrdersManagementService` | xUnit, Moq | Ninguna (repositorio simulado) | Milisegundos |
| Integración | Controllers, autorización JWT, middleware de errores, servicio y EF Core trabajando juntos | xUnit, `WebApplicationFactory`, SQLite en memoria | SQLite en memoria, una base nueva por prueba | Segundos |
| E2E | Flujo real de un usuario contra la API publicada | Postman, Newman | SQL Server en contenedor | Decenas de segundos |

Cuantas más pruebas hay en la base, más rápido se detecta un error y más barato es corregirlo. Las pruebas E2E son pocas porque son las más lentas y frágiles, pero confirman que todo el sistema funciona junto.

---

## 2. Organización en el repositorio

```
Dsw2025Tpi.UnitTests/            Pruebas unitarias
  Services/ProductsManagementServiceTests.cs
  Services/OrdersManagementServiceTests.cs
Dsw2025Tpi.IntegrationTests/     Pruebas de integración
  Infrastructure/ApiFactory.cs   API en memoria con SQLite
  Controllers/ProductsControllerTests.cs
  Controllers/OrdersControllerTests.cs
e2e/                             Colección Postman + entorno para Newman
.github/workflows/deploy.yml     Pipeline con etapas de pruebas
```

Los dos proyectos de prueba están en la carpeta de solución `tests` de `Dsw2025Tpi.sln`, así que aparecen en el Explorador de pruebas de Visual Studio.

### Ramas utilizadas (formato `test/{tarea_realizada}`)

| Rama | Contenido |
|---|---|
| `test/unit-tests` | Proyecto de pruebas unitarias y ajustes mínimos de la lógica de negocio. |
| `test/integration-tests` | Proyecto de pruebas de integración y ajuste de `Program.cs` para SQLite. |
| `test/e2e-newman` | Colección Postman y su documentación. |
| `test/ci-cd-pipeline` | Pipeline con etapas de pruebas y bloqueo del deploy. |
| `test/informe-pruebas` | Este informe. |

Cada rama parte de la anterior y entra a `development` por Pull Request, respetando las reglas de protección del TP3.

---

## 3. Pruebas unitarias

Se prueban los servicios de la capa `Application`, que concentran la lógica de negocio. El repositorio (`IRepository`) se reemplaza por un **mock estricto de Moq**: cualquier llamada que la prueba no haya configurado la hace fallar, y con `Verify` se comprueba qué se llamó y cuántas veces. Ninguna prueba unitaria toca la base de datos.

Para los listados, el mock **aplica el mismo predicado** que arma el servicio sobre una lista en memoria. Así se prueba el filtro que escribió el equipo y no un resultado preparado a mano.

### Creación de productos (Admin)

| Prueba | Datos de entrada | Resultado esperado |
|---|---|---|
| `AddProduct_WithValidData_ReturnsProductAndCallsRepositoryAdd` | SKU único, nombre, precio 15000, stock 10 | Devuelve el producto con esos datos y activo. `Add` se invoca **exactamente una vez** con la entidad correcta. |
| `AddProduct_WithDuplicateSku_ThrowsDuplicatedEntityException` | El mock simula que el SKU ya existe | Lanza `DuplicatedEntityException` con el SKU en el mensaje. `Add` **nunca** se invoca. |
| `AddProduct_WithInvalidStockOrPrice_ThrowsInvalidDataException` (3 casos) | Stock 0, stock -5, precio -1 | Lanza `InvalidDataException` con mensaje. No se consulta ni se guarda nada en el repositorio. |

### Creación de orden (Cliente)

| Prueba | Datos de entrada | Resultado esperado |
|---|---|---|
| `CreateOrder_WithValidItemsAndStock_CalculatesTotalAndDecrementsProductStock` | Cliente existente; 2 × $100 (stock 10) y 3 × $50 (stock 5) | Total = 350, igual a la suma de subtotales. Stock queda en 8 y 2. `Update` una vez por producto y `Add` de la orden una vez. |
| `CreateOrder_WhenProductStockIsInsufficient_ThrowsInsufficientStockException` | Stock 2, se piden 5 | Lanza `InsufficientStockException`. El stock sigue en 2; no hay `Update` ni `Add`. |
| `CreateOrder_WhenCustomerDoesNotExist_ThrowsCustomerNotFoundException` | El repositorio devuelve `null` para el cliente | Lanza `CustomerNotFoundException` de inmediato: no consulta productos, no los modifica, no crea la orden. |
| `CreateOrder_WhenLaterItemHasInsufficientStock_DoesNotModifyAnyProduct` *(caso extra)* | Ítem 1 con stock; ítem 2 sin stock | Lanza la excepción sin descontar stock de **ningún** producto. |

### Listado de productos

| Prueba | Datos de entrada | Resultado esperado |
|---|---|---|
| `GetProducts_WhenIncludeInactiveIsFalse_ReturnsOnlyActiveProducts` | 2 activos y 2 inactivos | Devuelve solo los 2 activos. |
| `GetProducts_WithPaginationFilter_AppliesSkipAndTakeCorrectly` | 15 productos, `PageNumber = 2`, `PageSize = 5` | Devuelve exactamente "Producto 06" a "Producto 10" y `TotalItems = 15`. |
| `GetProducts_WithSearchTerm_FiltersByNameSkuOrInternalCode` | "Mouse Gamer", "Teclado Mecánico", "Monitor", un SKU `MOUSE-PAD-01` y un código interno `INT-MOUSE-USB`; búsqueda `"mouse"` | Devuelve los 3 que coinciden por nombre, SKU o código interno, sin distinguir mayúsculas. |

---

## 4. Pruebas de integración

Se levanta la **API real en memoria** con `WebApplicationFactory<Program>`: el mismo `Program.cs`, los mismos controllers, la misma autenticación JWT y el mismo middleware de errores. Lo único que cambia es la base: SQL Server se reemplaza por **SQLite en memoria**. Cada prueba arranca con su propia API y su propia base vacía, así que las pruebas no dependen unas de otras.

Los tokens JWT se generan con el mismo `JwtTokenService` y la misma clave que usa la API, con rol `Admin` o `User` según la prueba. Después de cada petición, la prueba abre un `DbContext` nuevo y consulta las tablas para comprobar qué quedó realmente guardado.

### Creación de productos

| Prueba | Petición | Resultado esperado |
|---|---|---|
| `AddProduct_WhenUnauthenticatedOrNotAdmin_ReturnsUnauthorizedOrForbidden` | `POST /api/products` sin token, y con token de rol `User` | **401** sin token, **403** con rol `User`. No se inserta nada. |
| `AddProduct_WhenAuthenticatedAsAdmin_PersistsInSqliteAndReturnsCreated` | `POST /api/products` con token `Admin` y JSON válido | **201 Created** con `Location`. La fila existe en `Products` con SKU, código interno, nombre, descripción, precio, stock y estado enviados. |
| `AddProduct_WhenSkuAlreadyExistsInDatabase_ReturnsConflictStatusCode` | Producto pre-insertado y `POST` con el mismo SKU | Error controlado (**400**, admitido por el enunciado junto con 409), nunca 500. El mensaje menciona el SKU y sigue habiendo una sola fila. |

### Creación de orden

| Prueba | Petición | Resultado esperado |
|---|---|---|
| `CreateOrder_WhenUnauthenticated_ReturnsUnauthorized` | `POST /api/orders` sin header `Authorization` | **401**. No se crea ninguna orden. |
| `CreateOrder_WhenAuthenticatedWithValidData_ReturnsCreatedAndPersistsOrderAndStockInSqlite` | Cliente y producto con stock 10 sembrados; se compran 3 | **201 Created** con `Location`. La orden está en `Orders` con su ítem y total 4500, y el stock en `Products` bajó a **7**. |
| `CreateOrder_WhenStockIsInsufficient_ReturnsBadRequestWithErrorMessage` | Stock 2, se piden 5 | **400** con JSON cuyo `Message` indica la falta de stock. Ni órdenes nuevas ni cambio de stock. |

### Listado de productos

| Prueba | Petición | Resultado esperado |
|---|---|---|
| `GetProducts_PublicEndpoint_ReturnsOkWithPaginatedJsonStructure` | `GET /api/products` sin credenciales | **200 OK**. El JSON tiene `productItems` y `totalItems` y se deserializa correctamente. |
| `GetProducts_WithQueryStringFilters_ReturnsFilteredResultsFromSqlite` | 3 productos sembrados; `GET /api/products?search=mouse&pageNumber=1&pageSize=10` | Solo los 2 productos que contienen "mouse". |
| `GetAuthProducts_AdminEndpoint_WhenCalledByAdmin_IncludesInactiveProducts` | Un producto inactivo sembrado; `GET /api/products/admin` con token `Admin` | **200 OK** e incluye el inactivo. El endpoint público, en cambio, no lo muestra. |

---

## 5. Prueba E2E (Newman)

La colección `e2e/Dsw2025Tpi.postman_collection.json` recorre el flujo completo de un usuario contra la API publicada y conectada a SQL Server, igual que en producción:

| # | Paso | Verificación |
|---|---|---|
| 1 | Login del administrador | 200, devuelve token, rol `Admin` |
| 2 | Alta de producto con SKU único por ejecución | 201, `Location`, stock 10, activo |
| 3 | Alta repetida con el mismo SKU | 400/409, nunca 500 |
| 4 | Listado público filtrando por ese SKU | 200, estructura paginada, aparece solo ese producto |
| 5 | Registro de un cliente nuevo | 200, devuelve token y `customerId` |
| 6 | Orden sin token | 401 |
| 7 | Orden de 3 unidades | 201, `Location`, total 3000, estado `PENDING` |
| 8 | Consulta del producto | Stock 7 |
| 9 | Orden de 100 unidades | 400 con mensaje de falta de stock |
| 10 | Consulta de la orden creada | 200, un ítem de 3 unidades |
| 11 | Consulta del producto | El stock sigue en 7 |

Las instrucciones para correrla localmente están en `e2e/README.md`.

---

## 6. Cambios en el código de producción

Para que las pruebas reflejen el comportamiento que pide el enunciado se hicieron cambios chicos, sin alterar ningún contrato existente:

| Archivo | Cambio | Por qué | Impacto |
|---|---|---|---|
| `ProductsManagementService.GetProducts` | La búsqueda compara en minúsculas (`ToLower()`) | El enunciado pide coincidencia ignorando mayúsculas. Antes dependía de la collation de la base. | En SQL Server el resultado es el mismo (ya era insensible). |
| `ProductModel.ResponsePagination` | Se agregó la propiedad `TotalItems` (alias de `Total`) | El contrato del TP4 usa `totalItems`. | El JSON ahora trae `total` **y** `totalItems`; el frontend que usa `total` sigue funcionando. |
| `OrdersManagementService.CreateOrder` | Primero valida todos los ítems y recién después descuenta stock | Antes, si el segundo ítem no tenía stock, el primero ya había quedado descontado y guardado. Lo detectó el caso extra de las pruebas unitarias. | Mismas excepciones y mismas respuestas; se elimina el descuento parcial. |
| `Program.cs` | `Migrate()` solo si el proveedor es SQL Server; si no, `EnsureCreated()` | Las migraciones son específicas de SQL Server y no corren sobre SQLite. | En Azure y en local con SQL Server se sigue ejecutando `Migrate()` exactamente igual. |

---

## 7. Integración al pipeline CI/CD y bloqueo del deploy

Archivo: `.github/workflows/deploy.yml`. Se dispara en `push` y `pull_request` hacia `development` y `main`, igual que en el TP3.

```
unit-tests ──▶ integration-tests ──▶ e2e-tests ──▶ build-and-deploy
     │                │                  │         (Docker + Azure)
     └────────────────┴──────────────────┴──▶ notify-failure (solo si algo falla)
```

| Job | Depende de | Qué hace |
|---|---|---|
| `unit-tests` | — | Compila la solución y corre las pruebas unitarias. |
| `integration-tests` | `unit-tests` | Corre las pruebas de integración con SQLite. |
| `e2e-tests` | `integration-tests` | Levanta SQL Server como contenedor de servicio, publica y arranca la API, espera `/healthcheck` y corre Newman. |
| `build-and-deploy` | las **tres** etapas de pruebas | El job del TP3: imagen Docker, push a Docker Hub y deploy a Azure. |
| `notify-failure` | las tres etapas, con `if: failure()` | Marca el run con el error "Deploy bloqueado" y deja en el resumen qué etapa falló. |

**Cómo se bloquea el deploy:** `build-and-deploy` declara `needs: [unit-tests, integration-tests, e2e-tests]`. En GitHub Actions, un job con `needs` solo arranca si todos los jobs de los que depende terminaron bien. Si falla una prueba, el job de pruebas termina en rojo, los jobs que dependen de él quedan como *skipped* y **no se construye ni se publica la imagen**. Lo mismo pasa en un Pull Request: el PR queda con el check en rojo.

**Notificación:** GitHub envía un mail de "Run failed" a quien hizo el push o abrió el PR. Además, el logger `GitHubActionsTestLogger` marca cada prueba fallida como anotación en el código y arma un resumen con todas las pruebas en la pestaña del run. Los resultados (`.trx` y JUnit de Newman) quedan descargables como *artifacts*.

**Protección de ramas:** para que un PR con pruebas fallidas no se pueda mergear, en la regla de `development` y `main` (Settings → Rules / Branches) se activa **Require status checks to pass** y se agregan como obligatorios los checks *Pruebas unitarias*, *Pruebas de integración* y *Pruebas E2E (Newman)*.

---

## 8. Cómo ejecutar las pruebas localmente

```bash
# Todas las pruebas .NET (unitarias + integración)
dotnet test Dsw2025Tpi.sln

# Solo unitarias o solo integración
dotnet test Dsw2025Tpi.UnitTests
dotnet test Dsw2025Tpi.IntegrationTests
```

En Visual Studio: **Prueba → Explorador de pruebas → Ejecutar todas**. Las pruebas de integración no necesitan SQL Server ni ninguna configuración: usan SQLite en memoria.

---

## 9. Evidencia de ejecución

> Las capturas se agregan después de correr el pipeline en GitHub.

### 9.1 Ejecución local de las pruebas

*[Captura: Explorador de pruebas de Visual Studio o salida de `dotnet test` con todas las pruebas en verde]*

### 9.2 Pipeline exitoso

*[Captura: run de GitHub Actions con los cinco jobs; los de pruebas en verde y `build-and-deploy` ejecutado]*

*[Captura: resumen del run con el listado de pruebas que generó el logger]*

### 9.3 Deploy bloqueado ante un fallo

Procedimiento:

1. Se crea la rama `test/demo-deploy-bloqueado` con una aserción cambiada a propósito (por ejemplo, esperar stock 6 en lugar de 7).
2. Se abre un PR hacia `development` (y/o se hace push).
3. El job de pruebas falla, `build-and-deploy` queda como *skipped* y `notify-failure` informa "Deploy bloqueado".
4. Se cierra el PR sin mergear y se borra la rama.

*[Captura: run con el job de pruebas en rojo y `build-and-deploy` en gris (skipped)]*

*[Captura: anotación de la prueba fallida y resumen "Deploy bloqueado por pruebas fallidas"]*

*[Captura: PR con el check en rojo y el botón de merge bloqueado]*

### 9.4 Tablero ágil (Sprint 3)

*[Captura: tablero de Trello con las tarjetas del TP4 en "Done", pasando por las columnas de calidad]*
