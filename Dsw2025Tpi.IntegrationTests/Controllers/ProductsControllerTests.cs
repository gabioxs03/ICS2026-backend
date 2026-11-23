using System.Net;
using System.Net.Http.Json;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Dsw2025Tpi.IntegrationTests.Controllers;

/// <summary>
/// Pruebas de integración de /api/products: controller + autenticación JWT + servicio + EF Core sobre SQLite.
/// </summary>
public class ProductsControllerTests : IntegrationTestBase
{
    private const string ProductsUrl = "/api/products";

    private static object ValidProductPayload(string sku) => new
    {
        sku,
        internalCode = "INT-IT-01",
        name = "Mouse Gamer",
        description = "Mouse RGB 7200 DPI",
        currentUnitPrice = 15000.50m,
        stockQuantity = 25
    };

    // ---------------------------------------------------------------------
    // Creación de productos (Admin)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AddProduct_WhenUnauthenticatedOrNotAdmin_ReturnsUnauthorizedOrForbidden()
    {
        // Sin token -> 401 Unauthorized
        var anonymous = Factory.CreateAnonymousClient();
        var anonymousResponse = await anonymous.PostAsJsonAsync(ProductsUrl, ValidProductPayload("SKU-ANON"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        // Token válido pero con rol "User" -> 403 Forbidden ([Authorize(Roles = "Admin")])
        var user = Factory.CreateClientWithRole("User");
        var userResponse = await user.PostAsJsonAsync(ProductsUrl, ValidProductPayload("SKU-USER"));
        Assert.Equal(HttpStatusCode.Forbidden, userResponse.StatusCode);

        // En ninguno de los dos casos se guardó algo
        var count = await Factory.QueryDbAsync(db => db.Set<Product>().CountAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddProduct_WhenAuthenticatedAsAdmin_PersistsInSqliteAndReturnsCreated()
    {
        // Arrange
        var admin = Factory.CreateClientWithRole("Admin");

        // Act
        var response = await admin.PostAsJsonAsync(ProductsUrl, ValidProductPayload("SKU-IT-001"));

        // Assert: 201 Created con la ubicación del recurso
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var created = await response.Content.ReadFromJsonAsync<ProductItemContract>();
        Assert.NotNull(created);

        // Assert: la fila existe en la tabla Products con los valores enviados
        var saved = await Factory.QueryDbAsync(db =>
            db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Sku == "SKU-IT-001"));

        Assert.NotNull(saved);
        Assert.Equal(created!.Id, saved!.Id);
        Assert.Equal("INT-IT-01", saved.InternalCode);
        Assert.Equal("Mouse Gamer", saved.Name);
        Assert.Equal("Mouse RGB 7200 DPI", saved.Description);
        Assert.Equal(15000.50m, saved.CurrentUnitPrice);
        Assert.Equal(25, saved.StockQuantity);
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task AddProduct_WhenSkuAlreadyExistsInDatabase_ReturnsConflictStatusCode()
    {
        // Arrange: ya hay un producto con el SKU en la base
        await Factory.SeedAsync(NewProduct("Producto existente", sku: "SKU-DUP"));
        var admin = Factory.CreateClientWithRole("Admin");

        // Act
        var response = await admin.PostAsJsonAsync(ProductsUrl, ValidProductPayload("SKU-DUP"));

        // Assert: la API responde con un error controlado (409 o 400), nunca con un 500
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.BadRequest });

        var error = await ReadJsonAsync(response);
        Assert.Contains("SKU-DUP", error.GetProperty("Message").GetString());

        // Y no se insertó un segundo producto
        var count = await Factory.QueryDbAsync(db => db.Set<Product>().CountAsync(p => p.Sku == "SKU-DUP"));
        Assert.Equal(1, count);
    }

    // ---------------------------------------------------------------------
    // Listado de productos
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProducts_PublicEndpoint_ReturnsOkWithPaginatedJsonStructure()
    {
        // Arrange
        await Factory.SeedAsync(NewProduct("Auriculares"), NewProduct("Cámara web"));
        var anonymous = Factory.CreateAnonymousClient();

        // Act: endpoint [AllowAnonymous], sin credenciales
        var response = await anonymous.GetAsync(ProductsUrl);

        // Assert: 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert: el JSON tiene la estructura de paginación (productItems y totalItems)
        var json = await ReadJsonAsync(response);
        Assert.True(json.TryGetProperty("productItems", out _), "Falta la propiedad productItems");
        Assert.True(json.TryGetProperty("totalItems", out _), "Falta la propiedad totalItems");

        var page = await response.Content.ReadFromJsonAsync<ProductPageContract>();
        Assert.NotNull(page);
        Assert.Equal(2, page!.TotalItems);
        Assert.Equal(2, page.ProductItems.Count);
    }

    [Fact]
    public async Task GetProducts_WithQueryStringFilters_ReturnsFilteredResultsFromSqlite()
    {
        // Arrange: 3 productos, 2 coinciden con el término de búsqueda
        await Factory.SeedAsync(
            NewProduct("Mouse Gamer"),
            NewProduct("Mouse Inalámbrico"),
            NewProduct("Teclado Mecánico"));
        var anonymous = Factory.CreateAnonymousClient();

        // Act: los filtros viajan por query string (model binding de FilterProduct)
        var response = await anonymous.GetAsync($"{ProductsUrl}?search=mouse&pageNumber=1&pageSize=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ProductPageContract>();
        Assert.NotNull(page);
        Assert.Equal(2, page!.TotalItems);
        Assert.All(page.ProductItems, p => Assert.Contains("mouse", p.Name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(page.ProductItems, p => p.Name == "Teclado Mecánico");
    }

    [Fact]
    public async Task GetAuthProducts_AdminEndpoint_WhenCalledByAdmin_IncludesInactiveProducts()
    {
        // Arrange: un producto activo y uno inactivo
        var active = NewProduct("Monitor 24", isActive: true);
        var inactive = NewProduct("Monitor discontinuado", isActive: false);
        await Factory.SeedAsync(active, inactive);

        // Act: endpoint administrativo con token de Admin
        var admin = Factory.CreateClientWithRole("Admin");
        var adminResponse = await admin.GetAsync($"{ProductsUrl}/admin");

        // Assert: 200 OK e incluye el producto inactivo
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        var adminPage = await adminResponse.Content.ReadFromJsonAsync<ProductPageContract>();
        Assert.NotNull(adminPage);
        Assert.Contains(adminPage!.ProductItems, p => p.Id == inactive.Id && !p.IsActive);
        Assert.Contains(adminPage.ProductItems, p => p.Id == active.Id);

        // A diferencia del endpoint público, que no lo muestra
        var publicResponse = await Factory.CreateAnonymousClient().GetAsync(ProductsUrl);
        var publicPage = await publicResponse.Content.ReadFromJsonAsync<ProductPageContract>();
        Assert.NotNull(publicPage);
        Assert.DoesNotContain(publicPage!.ProductItems, p => p.Id == inactive.Id);
    }
}
