namespace Dsw2025Tpi.IntegrationTests.Infrastructure;

// Contratos JSON tal como los ve un cliente de la API.
// Se definen aparte (y no se reutilizan los DTO de la aplicación) para que las pruebas
// detecten si cambia el formato que devuelve la API.

public record ProductItemContract(
    Guid Id,
    string Sku,
    string InternalCode,
    string Name,
    string? Description,
    decimal CurrentUnitPrice,
    int StockQuantity,
    bool IsActive);

public record ProductPageContract(
    List<ProductItemContract> ProductItems,
    int TotalItems);
