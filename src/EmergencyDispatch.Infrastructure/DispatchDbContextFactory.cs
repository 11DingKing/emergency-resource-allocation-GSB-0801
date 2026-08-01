namespace EmergencyDispatch.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Enables <c>dotnet ef</c> to construct a <see cref="DispatchDbContext"/> at design time
/// (for creating migrations) without booting the API. The connection string here is only
/// used for scaffolding metadata, never at runtime.
/// </summary>
public sealed class DispatchDbContextFactory : IDesignTimeDbContextFactory<DispatchDbContext>
{
    public DispatchDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DISPATCH_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=emergency_dispatch;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new DispatchDbContext(options);
    }
}
