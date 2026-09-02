using AI.Factory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Swaps the real SQLite-file connection for an isolated in-memory one so
/// HTTP-level tests never touch `data/factory.db` — same isolation pattern
/// as the existing Sqlite-in-memory unit tests, just at the WebApplicationFactory level.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ApiTestFactory()
    {
        _connection.Open();

        // Schema must exist before Program.Main runs (WebApplicationFactory
        // executes Main for real) — Main's own startup-recovery step reads
        // WorkItems before the pipeline (and this factory's CreateHost) ever runs.
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
