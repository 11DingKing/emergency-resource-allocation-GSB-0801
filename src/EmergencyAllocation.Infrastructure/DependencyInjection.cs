using EmergencyAllocation.Domain.Services;
using EmergencyAllocation.Domain.Solver;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Seeding;
using EmergencyAllocation.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EmergencyAllocation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAllocationInfrastructure(
        this IServiceCollection services,
        string connectionString,
        bool useInMemory = false,
        bool seedDemoData = true,
        string? inMemoryDatabaseName = null)
    {
        services.AddDbContextFactory<AllocationDbContext>(options =>
        {
            if (useInMemory)
            {
                options.UseInMemoryDatabase(inMemoryDatabaseName ?? "emergency-alloc");
                options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            }
            else
            {
                options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3));
            }
        });

        services.AddSingleton<IAllocationSolver, GreedyAllocationSolver>();
        services.AddScoped<IAllocationService, AllocationService>();

        if (seedDemoData)
            services.AddHostedService<SeedingHostedService>();

        return services;
    }
}
