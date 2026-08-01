namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Boots the real API against a shared in-memory SQLite database using the real greedy
/// solver, so end-to-end tests exercise controllers, the allocation service and the
/// algorithm together — while still avoiding a PostgreSQL server. The DbContext registration
/// is swapped from Npgsql to SQLite; everything else is production wiring.
/// </summary>
public sealed class DispatchApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public DispatchApiFactory()
    {
        _connection = new SqliteConnection($"Data Source=file:api-{Guid.NewGuid():N}?mode=memory&cache=shared");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Skip both the Npgsql DbContext registration and the startup MigrateAsync; the test
        // supplies its own SQLite provider and creates the schema via EnsureCreated.
        builder.UseSetting("Dispatch:UseExternalDb", "true");
        builder.UseSetting("Dispatch:AutoMigrate", "false");

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<DispatchDbContext>(options => options.UseSqlite(_connection));

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
            db.Database.EnsureCreated();
            SeedData.EnsureSeededAsync(db).GetAwaiter().GetResult();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
