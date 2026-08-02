using EmergencyAllocation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmergencyAllocation.Infrastructure.Design;

public class AllocationDbContextFactory : IDesignTimeDbContextFactory<AllocationDbContext>
{
    public AllocationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AllocationDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=emergency_alloc;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly(typeof(AllocationDbContextFactory).Assembly.FullName))
            .Options;
        return new AllocationDbContext(options);
    }
}
