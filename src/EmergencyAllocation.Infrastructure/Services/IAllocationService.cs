using EmergencyAllocation.Core.Entities;
using EmergencyAllocation.Infrastructure.Services.Contracts;

namespace EmergencyAllocation.Infrastructure.Services;

public interface IAllocationService
{
    Task<AllocationResult> SolveInitialAsync(InitialSolveRequest request, CancellationToken cancellationToken = default);

    Task<AllocationResult> RearrangeAsync(RearrangeRequest request, CancellationToken cancellationToken = default);

    Task<AllocationResult?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    Task<AllocationResult?> GetByInputVersionAsync(string inputVersion, CancellationToken cancellationToken = default);

    Task<AllocationResult?> GetLatestCommittedAsync(CancellationToken cancellationToken = default);
}

public interface IAdministrativeDataService
{
    Task UpdateRoadAsync(string roadId, RoadUpdateRequest request, CancellationToken cancellationToken = default);

    Task UpdateTaskAsync(string taskId, TaskStateUpdateRequest request, CancellationToken cancellationToken = default);

    Task UpdateTeamPositionAsync(string teamId, string currentNode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoadSegment>> ListRoadsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoadEvent>> ListRoadEventsAsync(string? roadSegmentId = null, CancellationToken cancellationToken = default);

    Task<RoadEvent> RecordRoadEventAsync(RoadEventRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmergencyTask>> ListTasksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Team>> ListTeamsAsync(CancellationToken cancellationToken = default);
}
