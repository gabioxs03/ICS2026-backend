# Pruebas E2E (Postman + Newman)

La colección `Dsw2025Tpi.postman_collection.json` recorre un flujo completo de usuario contra la API levantada de verdad (con SQL Server):

1. Login del administrador (`admin` de `admins.json`).
2. Alta de un producto con SKU único por ejecución, y alta repetida con el mismo SKU (debe fallar con 400/409).
3. Listado público filtrando por el SKU creado.
4. Registro de un cliente nuevo.
5. Intento de crear una orden sin token (401).
6. Creación de una orden de 3 unidades (201, total 3000, estado `PENDING`).
7. Verificación de que el stock bajó de 10 a 7.
8. Orden por 100 unidades (400 por falta de stock) y verificación de que el stock sigue en 7.
9. Consulta de la orden creada.

## Ejecutarla localmente

Con la API corriendo (por ejemplo en `http://localhost:8080` con Docker, o desde Visual Studio):

```bash
npm install -g newman
newman run e2e/Dsw2025Tpi.postman_collection.json \
  -e e2e/local.postman_environment.json \
  --env-var baseUrl=http://localhost:8080
```

Si la API corre en otro puerto, se cambia `baseUrl`. La colección también se puede importar en Postman (*Import* → seleccionar los dos archivos) y correr con el *Collection Runner*.

## En el pipeline

El job `e2e-tests` de `.github/workflows/deploy.yml` levanta un SQL Server en un contenedor de servicio, publica y arranca la API, espera a que `/healthcheck` responda y corre esta colección con Newman. Si falla alguna aserción, el deploy no se ejecuta.
