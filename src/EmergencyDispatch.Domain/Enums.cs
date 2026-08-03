namespace EmergencyDispatch.Domain;

/// <summary>Lifecycle state of a mission task.</summary>
public enum TaskStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
}

/// <summary>Whether an allocation version was produced by an initial solve or a replan.</summary>
public enum AllocationKind
{
    Initial = 0,
    Replan = 1,
}
