using System.Linq.Expressions;
using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Application.Exceptions;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Dsw2025Tpi.UnitTests.Services;

/// <summary>
/// Pruebas unitarias de ProductsManagementService.
/// El repositorio se reemplaza por un mock (Moq), así que ninguna prueba toca la base de datos.
/// </summary>
public class ProductsManagementServiceTests
{
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private readonly ProductsManagementService _service;

    public ProductsManagementServiceTests()
    {
        _service = new ProductsManagementService(_repository.Object, NullLogger<ProductsManagementService>.Instance);
    }

    // ---------------------------------------------------------------------
    // Creación de productos (Admin)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AddProduct_WithValidData_ReturnsProductAndCallsRepositoryAdd()
    {
        // Arrange
        var request = new ProductModel.Request(
            Sku: "SKU-001",
            InternalCode: "INT-001",
            Name: "Mouse Gamer",
            Description: "Mouse RGB 7200 DPI",
            CurrentUnitPrice: 15000m,
            StockQuantity: 10);

        SetupSkuLookup(existing: null);
        _repository
            .Setup(r => r.Add(It.IsAny<Product>()))
            .ReturnsAsync((Product p) => p);

        // Act
        var result = await _service.AddProduct(request);

        // Assert: el objeto devuelto refleja los datos enviados
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("SKU-001", result.Sku);
        Assert.Equal("INT-001", result.InternalCode);
        Assert.Equal("Mouse Gamer", result.Name);
        Assert.Equal(15000m, result.CurrentUnitPrice);
        Assert.Equal(10, result.StockQuantity);
        Assert.True(result.IsActive);

        // Assert: se invocó al repositorio exactamente una vez con la entidad correcta
        _repository.Verify(r => r.Add(It.Is<Product>(p =>
            p.Id == result.Id &&
            p.Sku == "SKU-001" &&
            p.Name == "Mouse Gamer" &&
            p.CurrentUnitPrice == 15000m &&
            p.StockQuantity == 10 &&
            p.IsActive)), Times.Once);
    }

    [Fact]
    public async Task AddProduct_WithDuplicateSku_ThrowsDuplicatedEntityException()
    {
        // Arrange: el repositorio simula que ya existe un producto con ese SKU
        var existing = new Product("SKU-001", "Producto existente", 100m, 5, internalCode: "INT-OLD");
        SetupSkuLookup(existing);

        var request = new ProductModel.Request("SKU-001", "INT-002", "Otro producto", null, 200m, 3);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<DuplicatedEntityException>(() => _service.AddProduct(request));
        Assert.Contains("SKU-001", ex.Message);

        // No se debe intentar guardar el producto
        _repository.Verify(r => r.Add(It.IsAny<Product>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 100)]    // stock igual a cero
    [InlineData(-5, 100)]   // stock negativo
    [InlineData(10, -1)]    // precio negativo
    public async Task AddProduct_WithInvalidStockOrPrice_ThrowsInvalidDataException(int stock, int price)
    {
        // Arrange (los atributos no admiten decimal, por eso el precio llega como int)
        var request = new ProductModel.Request("SKU-001", "INT-001", "Mouse Gamer", null, (decimal)price, stock);

        // Act + Assert: el error trae un mensaje descriptivo
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _service.AddProduct(request));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));

        // La validación ocurre ANTES de consultar o guardar en el repositorio
        _repository.Verify(r => r.First(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()), Times.Never);
        _repository.Verify(r => r.Add(It.IsAny<Product>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // Listado de productos (Admin)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProducts_WhenIncludeInactiveIsFalse_ReturnsOnlyActiveProducts()
    {
        // Arrange: lista mixta de activos e inactivos
        var products = new List<Product>
        {
            CreateProduct("Auriculares", "SKU-A", isActive: true),
            CreateProduct("Base refrigerante", "SKU-B", isActive: false),
            CreateProduct("Cámara web", "SKU-C", isActive: true),
            CreateProduct("Disco externo", "SKU-D", isActive: false),
        };
        SetupFilteredProducts(products);

        var filter = new ProductModel.FilterProduct(Status: null, Search: null, PageNumber: 1, PageSize: 10);

        // Act
        var result = await _service.GetProducts(filter, includeInactive: false);

        // Assert
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.ProductItems.Count);
        Assert.All(result.ProductItems, p => Assert.True(p.IsActive));
        Assert.DoesNotContain(result.ProductItems, p => p.Sku == "SKU-B" || p.Sku == "SKU-D");
    }

    [Fact]
    public async Task GetProducts_WithPaginationFilter_AppliesSkipAndTakeCorrectly()
    {
        // Arrange: 15 productos activos ("Producto 01" ... "Producto 15")
        var products = Enumerable.Range(1, 15)
            .Select(i => CreateProduct($"Producto {i:D2}", $"SKU-{i:D2}"))
            .ToList();
        SetupFilteredProducts(products);

        var filter = new ProductModel.FilterProduct(Status: null, Search: null, PageNumber: 2, PageSize: 5);

        // Act
        var result = await _service.GetProducts(filter);

        // Assert: exactamente 5 elementos, los de la segunda página, y el total real
        Assert.Equal(5, result.ProductItems.Count);
        Assert.Equal(15, result.TotalItems);
        Assert.Equal(
            new[] { "Producto 06", "Producto 07", "Producto 08", "Producto 09", "Producto 10" },
            result.ProductItems.Select(p => p.Name));
    }

    [Fact]
    public async Task GetProducts_WithSearchTerm_FiltersByNameSkuOrInternalCode()
    {
        // Arrange
        var products = new List<Product>
        {
            CreateProduct("Mouse Gamer", "SKU-100", internalCode: "INT-100"),       // coincide por nombre
            CreateProduct("Teclado Mecánico", "SKU-200", internalCode: "INT-200"),  // no coincide
            CreateProduct("Monitor", "SKU-300", internalCode: "INT-300"),           // no coincide
            CreateProduct("Alfombrilla XL", "MOUSE-PAD-01", internalCode: "INT-400"), // coincide por SKU
            CreateProduct("Cable USB", "SKU-500", internalCode: "INT-MOUSE-USB"),   // coincide por código interno
        };
        SetupFilteredProducts(products);

        var filter = new ProductModel.FilterProduct(Status: null, Search: "mouse", PageNumber: 1, PageSize: 10);

        // Act
        var result = await _service.GetProducts(filter);

        // Assert: solo los que coinciden por nombre, SKU o código interno, sin importar mayúsculas
        Assert.Equal(3, result.TotalItems);
        Assert.Equal(
            new[] { "Alfombrilla XL", "Cable USB", "Mouse Gamer" },
            result.ProductItems.Select(p => p.Name).OrderBy(n => n));
        Assert.DoesNotContain(result.ProductItems, p => p.Name == "Teclado Mecánico" || p.Name == "Monitor");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void SetupSkuLookup(Product? existing)
    {
        _repository
            .Setup(r => r.First(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync(existing);
    }

    /// <summary>
    /// Simula el repositorio real: aplica el predicado que arma el servicio sobre la lista en memoria.
    /// Así se prueba la lógica de filtrado del servicio y no un resultado "armado a mano".
    /// </summary>
    private void SetupFilteredProducts(List<Product> products)
    {
        _repository
            .Setup(r => r.GetFiltered(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync((Expression<Func<Product, bool>> predicate, string[] _) =>
                (IEnumerable<Product>?)products.AsQueryable().Where(predicate).ToList());
    }

    private static Product CreateProduct(string name, string sku, bool isActive = true, string? internalCode = null)
    {
        return new Product(sku, name, 1000m, 10, description: null, internalCode: internalCode ?? $"INT-{sku}")
        {
            IsActive = isActive
        };
    }
}
