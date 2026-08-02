using EmergencyAllocation.Domain.Dtos;

namespace EmergencyAllocation.Domain.Services;

public sealed class SnapshotConflictException : Exception
{
    public SnapshotConflictDto Conflict { get; }

    public SnapshotConflictException(SnapshotConflictDto conflict)
        : base(conflict.Message)
    {
        Conflict = conflict;
    }
}
