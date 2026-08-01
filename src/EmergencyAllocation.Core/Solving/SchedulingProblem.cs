namespace EmergencyAllocation.Core.Solving;

public sealed class SchedulingProblem
{
    public required string InputVersion { get; init; }
    public required IReadOnlyList<TeamState> Teams { get; init; }
    public required IReadOnlyList<TaskState> Tasks { get; init; }
    public required IReadOnlyList<RoadState> Roads { get; init; }
    public required SolverOptions Options { get; init; }
    public Guid? PreviousVersionId { get; init; }
    public DateTimeOffset SnapshotTakenAt { get; init; } = DateTimeOffset.UtcNow;
}
