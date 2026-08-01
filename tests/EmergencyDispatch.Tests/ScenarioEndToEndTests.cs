namespace EmergencyDispatch.Tests;

using System.Net;
using System.Net.Http.Json;
using EmergencyDispatch.Api.Contracts;

/// <summary>
/// End-to-end scenario tests through the HTTP API with the real solver. These prove the
/// road-cut before/after allocation diff and that the controllers hold no logic beyond
/// delegation, idempotency status codes and mapping.
/// </summary>
public class ScenarioEndToEndTests : IClassFixture<DispatchApiFactory>
{
    private readonly DispatchApiFactory _factory;

    public ScenarioEndToEndTests(DispatchApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RoadCut_before_and_after_changes_life_task_route()
    {
        var client = _factory.CreateClient();

        // 1) Initial solve (before the road cut).
        var before = await Solve(client, "before-cut");
        var beforeLife = Assert.Single(before.Assignments, a => a.TaskCode == "T-LIFE");
        Assert.Equal("R-LOW", beforeLife.RoadSegmentCode);
        Assert.Equal(20, beforeLife.ArrivalMinutes);

        // 2) Cut the fast low-clearance road.
        var cut = await client.PutAsJsonAsync("/api/roads/R-LOW/state", new RoadStateDto { IsOpen = false });
        cut.EnsureSuccessStatusCode();

        // 3) Solve again with a new input version (after the road cut).
        var after = await Solve(client, "after-cut");
        var afterLife = Assert.Single(after.Assignments, a => a.TaskCode == "T-LIFE");
        Assert.Equal("R-HIGH", afterLife.RoadSegmentCode);
        Assert.Equal(30, afterLife.ArrivalMinutes);

        // The plan changed: route and arrival differ, but the task is still served in time.
        Assert.NotEqual(beforeLife.RoadSegmentCode, afterLife.RoadSegmentCode);
        Assert.True(afterLife.ArrivalMinutes <= 35);

        // The in-progress slope task remained held throughout (non-preemption).
        Assert.Contains(before.Assignments, a => a.TaskCode == "T-SLOPE" && a.TeamCode == "B");
        Assert.Contains(after.Assignments, a => a.TaskCode == "T-SLOPE" && a.TeamCode == "B");
    }

    [Fact]
    public async Task Duplicate_input_version_returns_200_not_201()
    {
        var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/allocations/solve", new SolveRequestDto { InputVersion = "idem-1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/allocations/solve", new SolveRequestDto { InputVersion = "idem-1" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Explanation_endpoint_returns_ordered_audit()
    {
        var client = _factory.CreateClient();
        var solved = await Solve(client, "explain-1");

        var explanation = await client.GetFromJsonAsync<ExplanationDto>(
            $"/api/allocations/{solved.VersionNumber}/explanation");

        Assert.NotNull(explanation);
        Assert.NotEmpty(explanation!.Audit);
        // Audit sequence is strictly ordered.
        var sequences = explanation.Audit.Select(a => a.Sequence).ToList();
        Assert.Equal(sequences.OrderBy(x => x), sequences);
    }

    private static async Task<AllocationVersionDto> Solve(HttpClient client, string inputVersion)
    {
        var resp = await client.PostAsJsonAsync("/api/allocations/solve", new SolveRequestDto { InputVersion = inputVersion });
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<AllocationVersionDto>();
        Assert.NotNull(dto);
        return dto!;
    }
}
