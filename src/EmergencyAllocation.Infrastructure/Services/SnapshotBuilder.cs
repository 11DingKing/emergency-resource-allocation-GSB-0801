using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Core.Solving;

namespace EmergencyAllocation.Infrastructure.Services;

public static class SnapshotBuilder
{
    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web);

    public static SchedulingProblem Build(
        IReadOnlyList<Team> teams,
        IReadOnlyList<EmergencyTask> tasks,
        IReadOnlyList<RoadSegment> roads,
        IReadOnlyList<RoadEvent> roadEvents,
        SolverOptions options,
        Guid? previousVersionId,
        string inputVersion,
        string? triggeringRoadEventId = null)
    {
        var latestEventByRoad = roadEvents
            .GroupBy(e => e.RoadSegmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.OccurredAt).First());

        var teamStates = teams
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new TeamState(
                t.Id,
                t.Name,
                t.VehicleId,
                t.Vehicle?.HeightMeters ?? 0m,
                t.CurrentNode,
                t.IsAvailable,
                t.Capabilities.ToHashSet(StringComparer.Ordinal),
                GetCurrentTaskId(t.Id, tasks)))
            .ToList();

        var taskStates = tasks
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new TaskState(
                t.Id,
                t.Name,
                t.LocationNode,
                t.RequiredArrivalMinutes,
                t.DurationMinutes,
                t.DangerLevel,
                t.Status,
                t.AssignedTeamId,
                t.RequiredCapabilities.ToHashSet(StringComparer.Ordinal)))
            .ToList();

        var roadStates = roads
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r =>
            {
                latestEventByRoad.TryGetValue(r.Id, out var evt);
                var closed = evt is { IsOpen: false };
                return new RoadState(
                    r.Id,
                    r.Name,
                    r.FromNode,
                    r.ToNode,
                    r.HeightLimitMeters,
                    r.TravelTimeMinutes,
                    r.IsOpen && !closed,
                    closed ? evt!.Id : null,
                    closed ? evt!.Reason : null);
            })
            .ToList();

        return new SchedulingProblem
        {
            InputVersion = inputVersion,
            Teams = teamStates,
            Tasks = taskStates,
            Roads = roadStates,
            Options = options,
            PreviousVersionId = previousVersionId,
            SnapshotTakenAt = DateTimeOffset.UtcNow,
            TriggeringRoadEventId = triggeringRoadEventId
        };
    }

    public static SnapshotHashes ComputeHashes(SchedulingProblem problem)
    {
        var roads = Hash(problem.Roads
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new
            {
                r.Id,
                r.FromNode,
                r.ToNode,
                r.HeightLimitMeters,
                r.TravelTimeMinutes,
                r.IsOpen,
                r.ClosedByEventId,
                r.ClosedByReason
            }));

        var tasks = Hash(problem.Tasks
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new
            {
                t.Id,
                t.LocationNode,
                t.RequiredArrivalMinutes,
                t.DangerLevel,
                t.Status,
                t.AssignedTeamId,
                caps = t.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal)
            }));

        var teamHash = Hash(problem.Teams
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new
            {
                t.Id,
                t.VehicleId,
                t.VehicleHeightMeters,
                t.CurrentNode,
                t.IsAvailable,
                caps = t.Capabilities.OrderBy(c => c, StringComparer.Ordinal)
            }));

        var vehicleHash = Hash(problem.Teams
            .OrderBy(t => t.VehicleId, StringComparer.Ordinal)
            .Select(t => new { t.VehicleId, t.VehicleHeightMeters }));

        var combined = Hash(new { roads, tasks, teams = teamHash, vehicles = vehicleHash });

        return new SnapshotHashes(combined, roads, tasks, teamHash, vehicleHash);
    }

    private static string Hash(object canonical)
    {
        var json = JsonSerializer.Serialize(canonical, HashOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string? GetCurrentTaskId(string teamId, IReadOnlyList<EmergencyTask> tasks)
    {
        return tasks.FirstOrDefault(t =>
            t.Status == Core.TaskStatus.InProgress &&
            string.Equals(t.AssignedTeamId, teamId, StringComparison.Ordinal))?.Id;
    }
}
