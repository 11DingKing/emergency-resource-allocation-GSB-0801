namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Builds SQLite-backed <see cref="DispatchDbContext"/> instances for tests. A single
/// keep-alive connection holds a named shared-cache in-memory database open; each
/// <see cref="NewContext"/> opens its own connection to the same database. This gives real,
/// independent connections (so concurrent transactions and the unique-index race behave like
/// production) while avoiding a PostgreSQL server. SQLite serializes writers and surfaces
/// contention as SQLITE_BUSY, which the service's retry path handles.
/// </summary>
public sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    private SqliteTestDatabase(SqliteConnection keepAlive, string connectionString)
    {
        _keepAlive = keepAlive;
        _connectionString = connectionString;
    }

    public static async Task<SqliteTestDatabase> CreateAsync(bool seed = true)
    {
        var connectionString =
            $"Data Source=file:dispatch-{Guid.NewGuid():N}?mode=memory&cache=shared";

        // Keep one connection open for the harness lifetime so the in-memory DB persists.
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var harness = new SqliteTestDatabase(keepAlive, connectionString);
        await using var db = harness.NewContext();
        await db.Database.EnsureCreatedAsync();
        if (seed)
        {
            await SeedData.EnsureSeededAsync(db);
        }
        return harness;
    }

    public DispatchDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new DispatchDbContext(options);
    }

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();
}
