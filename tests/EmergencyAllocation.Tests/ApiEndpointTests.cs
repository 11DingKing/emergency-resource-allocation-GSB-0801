using System.Net;
using System.Net.Http.Json;
using EmergencyAllocation.Core;
using EmergencyAllocation.Core.Solving;
using EmergencyAllocation.Infrastructure.Persistence;
using EmergencyAllocation.Infrastructure.Services.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskStatus = EmergencyAllocation.Core.TaskStatus;

namespace EmergencyAllocation.Tests;

public class ApiEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryAllocationDbContextFactory _dbFactory = new(Guid.NewGuid().ToString());

    public ApiEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDbContextFactory<AllocationDbContext>>();
                services.RemoveAll<DbContextOptions<AllocationDbContext>>();
                services.AddSingleton<IDbContextFactory<AllocationDbContext>>(_dbFactory);
            });
        });
    }

    [Fact]
    public async Task InitialSolve_ThenRoadClosure_Rearrange_EndToEnd()
    {
        var client = _factory.CreateClient();

        var initial = await client.PostAsJsonAsync("/api/allocations/initial",
            new InitialSolveRequest("api-v1"));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialResult = await initial.Content.ReadFromJsonAsync<AllocationResult>();
        Assert.NotNull(initialResult);
        Assert.True(initialResult.IsFeasible);
        var t1Initial = Assert.Single(initialResult.Assignments, a => a.TaskId == "T1");
        Assert.Equal("C", t1Initial.TeamId);
        Assert.Equal(20, t1Initial.EstimatedArrivalMinutes);

        var patchRoad = await client.PatchAsJsonAsync("/api/roads/R1", new RoadUpdateRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, patchRoad.StatusCode);

        var patchTask = await client.PatchAsJsonAsync("/api/tasks/T1",
            new TaskStateUpdateRequest(TaskStatus.InProgress, "C", null));
        Assert.Equal(HttpStatusCode.NoContent, patchTask.StatusCode);

        var rearrange = await client.PostAsJsonAsync("/api/allocations/rearrange",
            new RearrangeRequest("api-v2", initialResult.VersionId, true));
        Assert.Equal(HttpStatusCode.OK, rearrange.StatusCode);
        var after = await rearrange.Content.ReadFromJsonAsync<AllocationResult>();
        Assert.NotNull(after);
        Assert.True(after.IsFeasible);

        var t1After = Assert.Single(after.Assignments, a => a.TaskId == "T1");
        Assert.Equal("A", t1After.TeamId);
        Assert.Equal("ReassignedTo", t1After.Kind);
        Assert.Equal(35, t1After.EstimatedArrivalMinutes);
        Assert.Equal(new[] { "DEPOT", "SLOPE", "WATER" }, t1After.RouteNodes);
        Assert.NotNull(t1After.PreemptionReason);

        var t2After = Assert.Single(after.Assignments, a => a.TaskId == "T2");
        Assert.Equal("B", t2After.TeamId);
        Assert.Equal("Kept", t2After.Kind);

        var explain = await client.GetFromJsonAsync<ExplanationDto[]>($"/api/allocations/{after.VersionId}/explain");
        Assert.NotNull(explain);
        Assert.Contains(explain, e => e.RuleCode == RuleCodes.PreemptionAllowed);
    }

    [Fact]
    public async Task Idempotency_SameInputVersionReturnsSameVersion()
    {
        var client = _factory.CreateClient();

        var r1 = await client.PostAsJsonAsync("/api/allocations/initial", new InitialSolveRequest("idem-v1"));
        var r2 = await client.PostAsJsonAsync("/api/allocations/initial", new InitialSolveRequest("idem-v1"));

        var a1 = await r1.Content.ReadFromJsonAsync<AllocationResult>();
        var a2 = await r2.Content.ReadFromJsonAsync<AllocationResult>();
        Assert.Equal(a1!.VersionId, a2!.VersionId);
    }
}
