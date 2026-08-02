using EmergencyAllocation.Domain;
using EmergencyAllocation.Domain.Solver;
using TaskStatus = EmergencyAllocation.Domain.TaskStatus;

namespace EmergencyAllocation.Tests;

public static class SolverTestData
{
    public static SolverTeam Team(string code, string baseNode, IEnumerable<string> caps,
        double vehicleHeight, string? vehicleCode = null, Guid? teamId = null, Guid? vehicleId = null)
    {
        var tid = teamId ?? Guid.NewGuid();
        return new SolverTeam(
            tid, code, baseNode, true,
            caps.ToHashSet(StringComparer.Ordinal),
            new[]
            {
                new SolverVehicle(vehicleId ?? Guid.NewGuid(), vehicleCode ?? ("V-" + code),
                    tid, vehicleHeight, 500, true)
            });
    }

    public static SolverTask PendingTask(string code, string location, TaskSeverity sev,
        IEnumerable<string> caps, int duration = 30, int? deadline = null, Guid? taskId = null) =>
        new(taskId ?? Guid.NewGuid(), code, code, location, sev, 1, TaskStatus.Pending,
            duration, deadline, caps.ToHashSet(StringComparer.Ordinal),
            null, null, false);

    public static SolverRoad Road(string code, string from, string to, int minutes,
        double? height = null, bool open = true, long version = 1) =>
        new(code, from, to, minutes, height, open, version);
}
