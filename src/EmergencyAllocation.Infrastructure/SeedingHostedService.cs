using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmergencyAllocation.Infrastructure;

public sealed class SeedingHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SeedingHostedService> _logger;

    public SeedingHostedService(IServiceScopeFactory scopeFactory, ILogger<SeedingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
            await ctx.Database.EnsureCreatedAsync(cancellationToken);

            if (await ctx.Teams.AnyAsync(cancellationToken)) return;

            ctx.Teams.AddRange(SeedData.Teams());
            ctx.Tasks.AddRange(SeedData.Tasks());
            ctx.RoadSegments.AddRange(SeedData.Roads());
            await ctx.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded demo teams, tasks and roads.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed initial data.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
