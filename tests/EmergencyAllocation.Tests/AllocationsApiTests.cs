using System.Net;
using System.Net.Http.Json;
using EmergencyAllocation.Api;
using EmergencyAllocation.Domain.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace EmergencyAllocation.Tests;

public class AllocationsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AllocationsApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = "api-" + Guid.NewGuid();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UseInMemory", "true");
            builder.UseSetting("InMemoryDbName", dbName);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UseInMemory"] = "true",
                    ["InMemoryDbName"] = dbName
                });
            });
        });
    }

    [Fact]
    public async Task Initial_Endpoint_AssignsLifeTaskToC_AndExplainsAudit()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/allocations/initial",
            new { inputVersion = "api-v1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<AllocationVersionDto>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("Committed");
        result.Assignments.Should().Contain(a => a.TaskCode == "T1" && a.TeamCode == "C" && a.MeetsDeadline);
        result.Audit.Should().Contain(a => a.Kind == "candidate-reject" && a.VehicleCode == "VA");
    }

    [Fact]
    public async Task InterruptRoad_ThenRearrange_ReturnsNoFeasibleSolution()
    {
        var client = _factory.CreateClient();

        var interrupt = await client.PostAsJsonAsync("/api/allocations/roads/interrupt",
            new { roadCode = "R2", reason = "flooded", inputVersion = "api-road-v1", eventId = "road-r2-closed-01" });
        interrupt.StatusCode.Should().Be(HttpStatusCode.OK);
        var evt = await interrupt.Content.ReadFromJsonAsync<RoadEventDto>();
        evt.Should().NotBeNull();
        evt!.EventId.Should().Be("road-r2-closed-01");

        var rearrange = await client.PostAsJsonAsync("/api/allocations/rearrange",
            new { inputVersion = "api-rearrange-v1", reason = "R2 flooded", roadEventId = "road-r2-closed-01" });
        rearrange.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await rearrange.Content.ReadFromJsonAsync<AllocationVersionDto>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("NoFeasibleSolution");
        result.TriggeringRoadEventId.Should().Be("road-r2-closed-01");
    }

    [Fact]
    public async Task SameInputVersion_DifferentRoadSnapshot_Returns409WithFieldDiff()
    {
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/allocations/initial",
            new { inputVersion = "conflict-key" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        await client.PostAsJsonAsync("/api/allocations/roads/interrupt",
            new { roadCode = "R1", reason = "crash", inputVersion = "r-road-1", eventId = "evt-r1" });

        var retry = await client.PostAsJsonAsync("/api/allocations/initial",
            new { inputVersion = "conflict-key" });
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await retry.Content.ReadFromJsonAsync<SnapshotConflictDto>();
        conflict.Should().NotBeNull();
        conflict!.FieldDiffs.Should().Contain(f => f.Field == "roadDigest");
    }
}
