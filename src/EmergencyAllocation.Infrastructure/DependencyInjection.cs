using EmergencyAllocation.Core.Solving;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Services;
using EmergencyAllocation.Infrastructure.Solver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmergencyAllocation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEmergencyAllocationInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContextFactory<AllocationDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AllocationDbContext).Assembly.FullName);
            });
        });

        services.AddSingleton<ISchedulingSolver, DeterministicSchedulingSolver>();
        services.AddScoped<IAllocationService, AllocationService>();
        services.AddScoped<IAdministrativeDataService, AdministrativeDataService>();

        return services;
    }
}
