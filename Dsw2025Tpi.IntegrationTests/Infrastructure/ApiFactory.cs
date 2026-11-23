using System.Net.Http.Headers;
using Dsw2025Tpi.Api;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dsw2025Tpi.IntegrationTests.Infrastructure;

/// <summary>
/// Levanta la API real en memoria (TestServer) reemplazando SQL Server por SQLite en memoria.
/// Cada instancia tiene su propia base, así que cada prueba arranca con datos limpios.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    // Una conexión por contexto: SQLite en memoria vive mientras la conexión esté abierta.
    private readonly SqliteConnection _dataConnection = new("DataSource=:memory:");
    private readonly SqliteConnection _authConnection = new("DataSource=:memory:");

    public ApiFactory()
    {
        _dataConnection.Open();
        _authConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            ReplaceWithSqlite<Dsw2025TpiContext>(services, _dataConnection);
            ReplaceWithSqlite<AuthenticateContext>(services, _authConnection);
        });
    }

    private static void ReplaceWithSqlite<TContext>(IServiceCollection services, SqliteConnection connection)
        where TContext : DbContext
    {
        // Se quita la configuración de SQL Server que registra Program.cs...
        services.RemoveAll<DbContextOptions<TContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<TContext>>();

        // ...y se registra el mismo contexto apuntando a SQLite.
        services.AddDbContext<TContext>(options => options.UseSqlite(connection));
    }

    /// <summary>Cliente HTTP sin credenciales.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>Cliente HTTP con un JWT válido para el rol indicado ("Admin" o "User").</summary>
    public HttpClient CreateClientWithRole(string role, Guid? customerId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(role, customerId));
        return client;
    }

    /// <summary>Genera el token con el mismo servicio y la misma clave que usa la API.</summary>
    public string CreateToken(string role, Guid? customerId = null)
    {
        var jwt = Services.GetRequiredService<JwtTokenService>();
        return jwt.GenerateToken($"test-{role.ToLowerInvariant()}", role, customerId?.ToString() ?? string.Empty);
    }

    /// <summary>Inserta entidades directamente en la base SQLite.</summary>
    public async Task SeedAsync(params object[] entities)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    /// <summary>Consulta la base SQLite con un DbContext nuevo (sin datos cacheados de la petición).</summary>
    public async Task<T> QueryDbAsync<T>(Func<Dsw2025TpiContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        return await query(db);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _dataConnection.Dispose();
            _authConnection.Dispose();
        }
    }
}
