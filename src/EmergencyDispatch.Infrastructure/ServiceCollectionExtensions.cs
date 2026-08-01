namespace EmergencyDispatch.Infrastructure;

using EmergencyDispatch.Solver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>DI wiring for the infrastructure layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PostgreSQL-backed <see cref="DispatchDbContext"/>, the default
    /// <see cref="IAllocationSolver"/> and the <see cref="IAllocationService"/>. Callers may
    /// override the solver registration (e.g. tests inject a deterministic double).
    /// </summary>
    public static IServiceCollection AddDispatchInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<DispatchDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services.AddDispatchServices();
    }

    /// <summary>
    /// Register the solver and allocation service without binding a database provider. Used by
    /// the test host, which supplies its own <see cref="DispatchDbContext"/> registration so a
    /// single provider is active in the service provider.
    /// </summary>
    public static IServiceCollection AddDispatchServices(this IServiceCollection services)
    {
        services.AddScoped<IAllocationSolver, GreedyAllocationSolver>();
        services.AddScoped<IAllocationService, AllocationService>();
        return services;
    }
}
