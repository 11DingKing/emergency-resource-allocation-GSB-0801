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
        SolverOptions options,
        Guid? previousVersionId,
        string inputVersion)
    {
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
                t.Id is null ? null : GetCurrentTaskId(t.Id, tasks)))
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
            .Select(r => new RoadState(
                r.Id,
                r.Name,
                r.FromNode,
                r.ToNode,
                r.HeightLimitMeters,
                r.TravelTimeMinutes,
                r.IsOpen))
            .ToList();

        return new SchedulingProblem
        {
            InputVersion = inputVersion,
            Teams = teamStates,
            Tasks = taskStates,
            Roads = roadStates,
            Options = options,
            PreviousVersionId = previousVersionId,
            SnapshotTakenAt = DateTimeOffset.UtcNow
        };
    }

    public static string ComputeHash(SchedulingProblem problem)
    {
        var canonical = new
        {
            teams = problem.Teams
                .OrderBy(t => t.Id, StringComparer.Ordinal)
                .Select(t => new { t.Id, t.VehicleId, t.VehicleHeightMeters, t.CurrentNode, t.IsAvailable, caps = t.Capabilities.OrderBy(c => c, StringComparer.Ordinal) }),
            tasks = problem.Tasks
                .OrderBy(t => t.Id, StringComparer.Ordinal)
                .Select(t => new { t.Id, t.LocationNode, t.RequiredArrivalMinutes, t.DangerLevel, t.Status, t.AssignedTeamId, caps = t.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal) }),
            roads = problem.Roads
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new { r.Id, r.FromNode, r.ToNode, r.HeightLimitMeters, r.TravelTimeMinutes, r.IsOpen })
        };

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
