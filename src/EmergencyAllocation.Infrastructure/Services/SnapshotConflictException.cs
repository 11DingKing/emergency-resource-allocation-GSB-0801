using EmergencyAllocation.Infrastructure.Services.Contracts;

namespace EmergencyAllocation.Infrastructure.Services;

public sealed class SnapshotConflictException : Exception
{
    public SnapshotConflictException(SnapshotConflictResponse response)
        : base(response.Message)
    {
        Response = response;
    }

    public SnapshotConflictResponse Response { get; }
}
