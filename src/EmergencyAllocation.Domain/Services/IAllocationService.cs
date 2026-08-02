using EmergencyAllocation.Domain.Dtos;

namespace EmergencyAllocation.Domain.Services;

public interface IAllocationService
{
    Task<AllocationVersionDto> SolveInitialAsync(SolveRequestDto request, CancellationToken ct = default);
    Task<AllocationVersionDto> RearrangeAsync(SolveRequestDto request, CancellationToken ct = default);
    Task<AllocationVersionDto?> GetVersionAsync(Guid versionId, CancellationToken ct = default);
    Task<AllocationVersionDto?> GetLatestCommittedAsync(CancellationToken ct = default);
    Task<RoadEventDto> InterruptRoadAsync(RoadInterruptRequestDto request, CancellationToken ct = default);
    Task<RoadEventDto> ReopenRoadAsync(RoadReopenRequestDto request, CancellationToken ct = default);
    Task<TaskDto> EscalateTaskAsync(TaskEscalationRequestDto request, CancellationToken ct = default);
    Task<SnapshotDigestDto> GetCurrentDigestsAsync(CancellationToken ct = default);
}
