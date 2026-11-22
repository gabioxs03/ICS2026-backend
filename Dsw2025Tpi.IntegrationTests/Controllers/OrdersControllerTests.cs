using System.Net;
using System.Net.Http.Json;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Dsw2025Tpi.IntegrationTests.Controllers;

/// <summary>
/// Pruebas de integración de POST /api/orders: autenticación JWT, transacción de la orden
/// y actualización real del inventario en SQLite.
/// </summary>
public class OrdersControllerTests : IntegrationTestBase
{
    private const string OrdersUrl = "/api/orders";

    private static object OrderPayload(Guid customerId, Guid productId, int quantity) => new
    {
        customerId,
        shippingAddress = "Av. Mate de Luna 1234, San Miguel de Tucumán",
        billingAddress = "Av. Mate de Luna 1234, San Miguel de Tucumán",
        notes = "Prueba de integración",
        items = new[] { new { productId, quantity } }
    };

    [Fact]
    public async Task CreateOrder_WhenUnauthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var customer = NewCustomer();
        var product = NewProduct("Mouse Gamer", stock: 10);
        await Factory.SeedAsync(customer, product);

        // Act: payload válido pero sin header Authorization
        var response = await Factory.CreateAnonymousClient()
            .PostAsJsonAsync(OrdersUrl, OrderPayload(customer.Id, product.Id, 1));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var orders = await Factory.QueryDbAsync(db => db.Set<Order>().CountAsync());
        Assert.Equal(0, orders);
    }

    [Fact]
    public async Task CreateOrder_WhenAuthenticatedWithValidData_ReturnsCreatedAndPersistsOrderAndStockInSqlite()
    {
        // 1. Token JWT válido de un usuario
        // 2. Cliente y producto con stock inicial 10 sembrados en SQLite
        var customer = NewCustomer();
        var product = NewProduct("Mouse Gamer", stock: 10, price: 1500m);
        await Factory.SeedAsync(customer, product);
        var client = Factory.CreateClientWithRole("User", customer.Id);

        // 3. Se compran 3 unidades
        var response = await client.PostAsJsonAsync(OrdersUrl, OrderPayload(customer.Id, product.Id, 3));

        // 4. 201 Created con header Location
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await ReadJsonAsync(response);
        var orderId = body.GetProperty("orderId").GetGuid();
        Assert.Equal(4500m, body.GetProperty("totalAmount").GetDecimal());

        // 5. La orden está en la tabla Orders y el stock bajó a 7 en la tabla Products
        var savedOrder = await Factory.QueryDbAsync(db =>
            db.Set<Order>().AsNoTracking().Include(o => o.OrderItems).SingleOrDefaultAsync(o => o.Id == orderId));
        Assert.NotNull(savedOrder);
        Assert.Equal(customer.Id, savedOrder!.CustomerId);
        Assert.Equal(4500m, savedOrder.TotalAmount);
        var item = Assert.Single(savedOrder.OrderItems!);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(3, item.Quantity);

        var savedProduct = await Factory.QueryDbAsync(db =>
            db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == product.Id));
        // DEMO TP4: aserción rota a propósito para mostrar el deploy bloqueado. NO MERGEAR.
        Assert.Equal(6, savedProduct.StockQuantity);
    }

    [Fact]
    public async Task CreateOrder_WhenStockIsInsufficient_ReturnsBadRequestWithErrorMessage()
    {
        // Arrange: el producto tiene 2 unidades y se piden 5
        var customer = NewCustomer();
        var product = NewProduct("Monitor", stock: 2);
        await Factory.SeedAsync(customer, product);
        var client = Factory.CreateClientWithRole("User", customer.Id);

        // Act
        var response = await client.PostAsJsonAsync(OrdersUrl, OrderPayload(customer.Id, product.Id, 5));

        // Assert: el middleware de errores responde 400 con un JSON que explica la falta de stock
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var error = await ReadJsonAsync(response);
        var message = error.GetProperty("Message").GetString();
        Assert.NotNull(message);
        Assert.Contains("stock", message, StringComparison.OrdinalIgnoreCase);

        // Nada cambió en la base
        var orders = await Factory.QueryDbAsync(db => db.Set<Order>().CountAsync());
        Assert.Equal(0, orders);
        var savedProduct = await Factory.QueryDbAsync(db =>
            db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == product.Id));
        Assert.Equal(2, savedProduct.StockQuantity);
    }
}
