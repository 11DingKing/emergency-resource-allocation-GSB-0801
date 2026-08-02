using EmergencyAllocation.Domain.Dtos;

namespace EmergencyAllocation.Domain.Services;

public interface IAllocationService
{
    Task<AllocationVersionDto> SolveInitialAsync(SolveRequestDto request, CancellationToken ct = default);
    Task<AllocationVersionDto> RearrangeAsync(SolveRequestDto request, CancellationToken ct = default);
    Task<AllocationVersionDto?> GetVersionAsync(Guid versionId, CancellationToken ct = default);
    Task<AllocationVersionDto?> GetLatestCommittedAsync(CancellationToken ct = default);
    Task InterruptRoadAsync(RoadInterruptRequestDto request, CancellationToken ct = default);
    Task ReopenRoadAsync(RoadReopenRequestDto request, CancellationToken ct = default);
}
