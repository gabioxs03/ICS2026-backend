using System.Text.Json;
using Dsw2025Tpi.Domain.Entities;

// Las pruebas levantan la API completa; se ejecutan en serie para que no compitan
// por recursos compartidos (por ejemplo, el archivo de log de la API).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dsw2025Tpi.IntegrationTests.Infrastructure;

/// <summary>
/// Base de las pruebas de integración: xUnit crea una instancia de la clase por cada prueba,
/// así que cada prueba tiene su propia API y su propia base SQLite vacía.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly ApiFactory Factory = new();

    protected static Product NewProduct(
        string name,
        string? sku = null,
        int stock = 10,
        decimal price = 1000m,
        bool isActive = true)
    {
        return new Product(sku ?? $"SKU-{Guid.NewGuid():N}"[..12], name, price, stock, description: null, internalCode: "INT-TEST")
        {
            IsActive = isActive
        };
    }

    protected static Customer NewCustomer()
    {
        return new Customer
        {
            Name = "Cliente de prueba",
            Email = $"cliente-{Guid.NewGuid():N}@test.local"
        };
    }

    /// <summary>Lee el cuerpo de la respuesta como JSON genérico.</summary>
    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose()
    {
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }
}
