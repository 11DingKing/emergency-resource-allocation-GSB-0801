using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmergencyAllocation.Infrastructure.Services;

public sealed class AdministrativeDataService : IAdministrativeDataService
{
    private readonly IDbContextFactory<AllocationDbContext> _dbContextFactory;
    private readonly ILogger<AdministrativeDataService> _logger;

    public AdministrativeDataService(
        IDbContextFactory<AllocationDbContext> dbContextFactory,
        ILogger<AdministrativeDataService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task UpdateRoadAsync(string roadId, RoadUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var road = await context.RoadSegments.FirstOrDefaultAsync(r => r.Id == roadId, cancellationToken);
        if (road is null)
        {
            throw new KeyNotFoundException($"Road segment '{roadId}' not found.");
        }

        if (request.IsOpen.HasValue)
        {
            road.IsOpen = request.IsOpen.Value;
            _logger.LogInformation("Road {RoadId} open state set to {IsOpen}.", roadId, road.IsOpen);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateTaskAsync(string taskId, TaskStateUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            throw new KeyNotFoundException($"Task '{taskId}' not found.");
        }

        task.Status = request.Status;
        if (request.AssignedTeamId is not null)
        {
            task.AssignedTeamId = request.AssignedTeamId;
        }

        if (request.CurrentNode is not null)
        {
            var team = await context.Teams.FirstOrDefaultAsync(t => t.Id == task.AssignedTeamId, cancellationToken);
            if (team is not null)
            {
                team.CurrentNode = request.CurrentNode;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateTeamPositionAsync(string teamId, string currentNode, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var team = await context.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            throw new KeyNotFoundException($"Team '{teamId}' not found.");
        }

        team.CurrentNode = currentNode;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoadSegment>> ListRoadsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RoadSegments.AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmergencyTask>> ListTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tasks.AsNoTracking().OrderBy(t => t.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Team>> ListTeamsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Teams.AsNoTracking().Include(t => t.Vehicle).OrderBy(t => t.Id).ToListAsync(cancellationToken);
    }
}
