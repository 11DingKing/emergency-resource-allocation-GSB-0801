namespace EmergencyAllocation.Domain;

public enum TaskSeverity
{
    Routine = 0,
    Urgent = 1,
    LifeSafety = 2
}

public enum TaskStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}

public enum VehicleKind
{
    Standard = 0,
    HighClearance = 1,
    Boat = 2
}

public enum AllocationVersionStatus
{
    Pending = 0,
    Committed = 1,
    Superseded = 2,
    Failed = 3,
    NoFeasibleSolution = 4
}

public enum AllocationDecision
{
    Assigned = 0,
    Kept = 1,
    Reassigned = 2,
    Unassigned = 3
}
