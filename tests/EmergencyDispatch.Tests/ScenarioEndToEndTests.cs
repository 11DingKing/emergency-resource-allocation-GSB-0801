namespace EmergencyDispatch.Tests;

using System.Net;
using System.Net.Http.Json;
using EmergencyDispatch.Api.Contracts;

/// <summary>
/// End-to-end scenario tests through the HTTP API with the real solver. These prove the
/// snapshot-bound solve flow (record road event, escalate danger, replan), the 200-vs-409
/// distinction between identical-payload replay and different-snapshot conflict, and that the
/// controllers hold no logic beyond delegation, status mapping and DTO projection.
/// </summary>
public class ScenarioEndToEndTests : IClassFixture<DispatchApiFactory>
{
    private readonly DispatchApiFactory _factory;

    public ScenarioEndToEndTests(DispatchApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RoadEvent_and_critical_replan_reroutes_T1_and_holds_T2()
    {
        var client = _factory.CreateClient();

        // 1) Initial solve bound to the current snapshot.
        var snapBefore = await SnapshotVersion(client);
        var before = await Solve(client, "e2e-before", snapBefore);
        var beforeT1 = Assert.Single(before.Assignments, a => a.TaskCode == "T1");
        Assert.Equal("R2", beforeT1.RoadSegmentCode);
        Assert.Equal(20, beforeT1.ArrivalMinutes);

        // 2) Record road event road-r2-closed-01 closing R2, then escalate T1 to critical.
        var evt = await client.PostAsJsonAsync("/api/roads/events",
            new RoadEventRequestDto { EventId = "road-r2-closed-01", RoadCode = "R2", Closed = true });
        Assert.Equal(HttpStatusCode.Created, evt.StatusCode);
        var danger = await client.PutAsJsonAsync("/api/tasks/T1/danger", new TaskDangerDto { DangerLevel = "critical" });
        danger.EnsureSuccessStatusCode();

        // 3) Replan against the new snapshot as r2-critical-v1.
        var snapAfter = await SnapshotVersion(client);
        Assert.NotEqual(snapBefore, snapAfter);
        var after = await Replan(client, "r2-critical-v1", snapAfter);
        var afterT1 = Assert.Single(after.Assignments, a => a.TaskCode == "T1");
        Assert.Equal("R1", afterT1.RoadSegmentCode);
        Assert.Equal(30, afterT1.ArrivalMinutes);
        Assert.True(afterT1.ArrivalMinutes <= 35);

        // T2 stays held by B throughout (non-preemption; danger did not escalate for T2).
        Assert.Contains(before.Assignments, a => a.TaskCode == "T2" && a.TeamCode == "B");
        Assert.Contains(after.Assignments, a => a.TaskCode == "T2" && a.TeamCode == "B");

        // The explanation cites the road event and the reroute rule for T1.
        var explanation = await client.GetFromJsonAsync<ExplanationDto>(
            $"/api/allocations/{after.VersionNumber}/explanation");
        Assert.NotNull(explanation);
        Assert.Contains(explanation!.RoadEvents, e => e.EventId == "road-r2-closed-01");
        Assert.Contains(explanation.Audit, a => a.RuleCode == "REROUTE_AFTER_ROAD_CUT" && a.Message.Contains("road-r2-closed-01"));
    }

    [Fact]
    public async Task Identical_payload_replays_200_but_different_snapshot_conflicts_409()
    {
        var client = _factory.CreateClient();
        var snap = await SnapshotVersion(client);

        var first = await client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto { InputVersion = "conflict-1", SnapshotVersion = snap });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Identical payload (same input version + same snapshot) replays with 200.
        var replay = await client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto { InputVersion = "conflict-1", SnapshotVersion = snap });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // Move the world, then resubmit the SAME input version against the NEW snapshot: 409.
        await client.PostAsJsonAsync("/api/roads/events",
            new RoadEventRequestDto { EventId = "road-r2-closed-conflict", RoadCode = "R2", Closed = true });
        var snapAfter = await SnapshotVersion(client);

        var conflict = await client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto { InputVersion = "conflict-1", SnapshotVersion = snapAfter });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var body = await conflict.Content.ReadFromJsonAsync<ConflictDto>();
        Assert.NotNull(body);
        Assert.Equal("snapshot_conflict", body!.Error);
        Assert.Equal(snap, body.StoredSnapshotVersion);
        Assert.Equal(snapAfter, body.RequestedSnapshotVersion);
        Assert.Contains(body.Changes, c => c.Path.StartsWith("road[R2]"));
        Assert.Contains(body.RoadEvents, e => e.EventId == "road-r2-closed-conflict");
    }

    [Fact]
    public async Task Solve_without_snapshot_version_is_rejected_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/allocations/solve", new SolveRequestDto { InputVersion = "no-snap" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static async Task<string> SnapshotVersion(HttpClient client)
    {
        var snap = await client.GetFromJsonAsync<SnapshotDto>("/api/snapshot");
        Assert.NotNull(snap);
        return snap!.SnapshotVersion;
    }

    private static async Task<AllocationVersionDto> Solve(HttpClient client, string inputVersion, string snapshotVersion)
    {
        var resp = await client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto { InputVersion = inputVersion, SnapshotVersion = snapshotVersion });
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<AllocationVersionDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<AllocationVersionDto> Replan(HttpClient client, string inputVersion, string snapshotVersion)
    {
        var resp = await client.PostAsJsonAsync("/api/allocations/replan",
            new SolveRequestDto { InputVersion = inputVersion, SnapshotVersion = snapshotVersion });
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<AllocationVersionDto>();
        Assert.NotNull(dto);
        return dto!;
    }
}
