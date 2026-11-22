using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Application.Exceptions;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Dsw2025Tpi.UnitTests.Services;

/// <summary>
/// Pruebas unitarias de la creación de órdenes (OrdersManagementService.CreateOrder).
/// El repositorio es un mock estricto: cualquier llamada no configurada hace fallar la prueba.
/// </summary>
public class OrdersManagementServiceTests
{
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private readonly OrdersManagementService _service;
    private readonly Customer _customer = new() { Name = "Laura Gómez", Email = "laura@example.com" };

    public OrdersManagementServiceTests()
    {
        _service = new OrdersManagementService(_repository.Object, NullLogger<OrdersManagementService>.Instance);
    }

    [Fact]
    public async Task CreateOrder_WithValidItemsAndStock_CalculatesTotalAndDecrementsProductStock()
    {
        // Arrange: cliente existente y dos productos con stock suficiente
        var mouse = CreateProduct("Mouse Gamer", price: 100m, stock: 10);
        var teclado = CreateProduct("Teclado Mecánico", price: 50m, stock: 5);

        SetupCustomer(_customer);
        SetupProduct(mouse);
        SetupProduct(teclado);
        _repository.Setup(r => r.Update(It.IsAny<Product>())).ReturnsAsync((Product p) => p);
        _repository.Setup(r => r.Add(It.IsAny<Order>())).ReturnsAsync((Order o) => o);

        var request = CreateRequest(
            new OrderItemRequest(mouse.Id, 2),    // 2 x 100 = 200
            new OrderItemRequest(teclado.Id, 3)); // 3 x  50 = 150

        // Act
        var result = await _service.CreateOrder(request);

        // Assert: el total es la suma exacta de los subtotales
        Assert.Equal(350m, result.TotalAmount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(result.Items.Sum(i => i.Subtotal), result.TotalAmount);

        // Assert: se descontó el stock de cada producto
        Assert.Equal(8, mouse.StockQuantity);
        Assert.Equal(2, teclado.StockQuantity);

        // Assert: se actualizó cada producto en el repositorio
        _repository.Verify(r => r.Update(It.Is<Product>(p => p.Id == mouse.Id && p.StockQuantity == 8)), Times.Once);
        _repository.Verify(r => r.Update(It.Is<Product>(p => p.Id == teclado.Id && p.StockQuantity == 2)), Times.Once);

        // Assert: se agregó la orden una sola vez, con el cliente y el total correctos
        _repository.Verify(r => r.Add(It.Is<Order>(o =>
            o.CustomerId == _customer.Id &&
            o.TotalAmount == 350m &&
            o.OrderItems != null && o.OrderItems.Count == 2)), Times.Once);
    }

    [Fact]
    public async Task CreateOrder_WhenProductStockIsInsufficient_ThrowsInsufficientStockException()
    {
        // Arrange: producto con stock 2 y pedido de 5 unidades
        var monitor = CreateProduct("Monitor", price: 300m, stock: 2);
        SetupCustomer(_customer);
        SetupProduct(monitor);

        var request = CreateRequest(new OrderItemRequest(monitor.Id, 5));

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InsufficientStockException>(() => _service.CreateOrder(request));
        Assert.Contains("stock", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No se persiste la orden ni se altera el producto
        Assert.Equal(2, monitor.StockQuantity);
        _repository.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        _repository.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrder_WhenCustomerDoesNotExist_ThrowsCustomerNotFoundException()
    {
        // Arrange: el repositorio devuelve null para el cliente
        var missingCustomerId = Guid.NewGuid();
        _repository
            .Setup(r => r.GetById<Customer>(missingCustomerId, It.IsAny<string[]>()))
            .ReturnsAsync((Customer?)null);

        var request = new OrderRequest(
            missingCustomerId,
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            null,
            new List<OrderItemRequest> { new(Guid.NewGuid(), 1) });

        // Act + Assert
        var ex = await Assert.ThrowsAsync<CustomerNotFoundException>(() => _service.CreateOrder(request));
        Assert.Contains(missingCustomerId.ToString(), ex.Message);

        // Falla de inmediato: no consulta productos, no los modifica y no crea la orden
        _repository.Verify(r => r.GetById<Product>(It.IsAny<Guid>(), It.IsAny<string[]>()), Times.Never);
        _repository.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        _repository.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrder_WhenLaterItemHasInsufficientStock_DoesNotModifyAnyProduct()
    {
        // Caso extra: el primer ítem tiene stock, pero el segundo no.
        // Ningún producto debe quedar con el stock descontado a medias.
        var mouse = CreateProduct("Mouse Gamer", price: 100m, stock: 10);
        var monitor = CreateProduct("Monitor", price: 300m, stock: 1);
        SetupCustomer(_customer);
        SetupProduct(mouse);
        SetupProduct(monitor);

        var request = CreateRequest(
            new OrderItemRequest(mouse.Id, 2),
            new OrderItemRequest(monitor.Id, 3));

        await Assert.ThrowsAsync<InsufficientStockException>(() => _service.CreateOrder(request));

        Assert.Equal(10, mouse.StockQuantity);
        Assert.Equal(1, monitor.StockQuantity);
        _repository.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        _repository.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void SetupCustomer(Customer customer)
    {
        _repository
            .Setup(r => r.GetById<Customer>(customer.Id, It.IsAny<string[]>()))
            .ReturnsAsync(customer);
    }

    private void SetupProduct(Product product)
    {
        _repository
            .Setup(r => r.GetById<Product>(product.Id, It.IsAny<string[]>()))
            .ReturnsAsync(product);
    }

    private OrderRequest CreateRequest(params OrderItemRequest[] items)
    {
        return new OrderRequest(
            _customer.Id,
            "Av. Siempre Viva 742",
            "Av. Siempre Viva 742",
            "Entregar por la tarde",
            items.ToList());
    }

    private static Product CreateProduct(string name, decimal price, int stock)
    {
        return new Product($"SKU-{Guid.NewGuid():N}"[..12], name, price, stock, internalCode: "INT-TEST");
    }
}
