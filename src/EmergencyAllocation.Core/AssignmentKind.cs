namespace EmergencyAllocation.Core;

public enum AssignmentKind
{
    Unassigned = 0,
    Kept = 1,
    NewAssignment = 2,
    PreemptedFrom = 3,
    ReassignedTo = 4
}
