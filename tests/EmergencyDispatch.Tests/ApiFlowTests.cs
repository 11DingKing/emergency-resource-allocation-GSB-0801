using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EmergencyDispatch.Tests;

/// <summary>
/// API 全流程：捕获快照 → 初始求解 → 幂等回放 → 道路事件 road-r2-closed-01 → T1 升 critical →
/// 新快照重排 → 同 inputVersion 不同快照 409 → 过期快照 409 → diff/解释引用事件与快照。
/// </summary>
public class ApiFlowTests : IClassFixture<ApiFlowTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("TestDbName", Guid.NewGuid().ToString()); // 每个工厂独立 InMemory 库
        }
    }

    private readonly HttpClient _client;

    public ApiFlowTests(Factory factory) => _client = factory.CreateClient();

    private async Task<SnapshotDto> CaptureAsync()
    {
        var resp = await _client.PostAsync("/api/world/snapshots", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SnapshotDto>())!;
    }

    [Fact]
    public async Task 道路事件与快照绑定的完整流程()
    {
        // 1. 捕获初始世界快照 S1，完成初始求解
        var s1 = await CaptureAsync();
        var solve1 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("day1-0800", "initial", null, s1.Id));
        Assert.Equal(HttpStatusCode.Created, solve1.StatusCode);
        var plan1 = await solve1.Content.ReadFromJsonAsync<PlanDto>();
        Assert.NotNull(plan1);
        Assert.Equal(s1.Id, plan1!.WorldSnapshotId);
        var p1t1 = plan1.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("A", p1t1.TeamCode);
        Assert.Equal("R2", p1t1.RoadCode);
        Assert.Contains(p1t1.Reasons, r => r.Code == "tie_break_team_code");

        // 2. 完全相同的请求 → 幂等回放
        var replay = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("day1-0800", "initial", null, s1.Id));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("X-Idempotent-Replay").Single());

        // 3. 记录道路事件 road-r2-closed-01（幂等）
        var ev1 = await _client.PostAsJsonAsync("/api/roads/events",
            new RoadEventRequestDto("road-r2-closed-01", "R2", true, "积水塌方"));
        Assert.Equal(HttpStatusCode.Created, ev1.StatusCode);
        var evReplay = await _client.PostAsJsonAsync("/api/roads/events",
            new RoadEventRequestDto("road-r2-closed-01", "R2", true, "积水塌方"));
        Assert.Equal(HttpStatusCode.OK, evReplay.StatusCode); // 事件幂等回放

        // 4. T1 升为 critical
        var danger = await _client.PostAsJsonAsync("/api/tasks/T1/danger", new DangerUpdateDto("critical", "水位上涨"));
        Assert.Equal(HttpStatusCode.OK, danger.StatusCode);

        // 5. 捕获新快照 S2（内容变化 → 新快照），重排 r2-critical-v1
        var s2 = await CaptureAsync();
        Assert.NotEqual(s1.Id, s2.Id);
        Assert.NotEqual(s1.WorldDigest, s2.WorldDigest);
        var solve2 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-critical-v1", "replan", "R2 中断且 T1 生命危险升级", s2.Id));
        Assert.Equal(HttpStatusCode.Created, solve2.StatusCode);
        var plan2 = await solve2.Content.ReadFromJsonAsync<PlanDto>();
        Assert.Equal(s2.Id, plan2!.WorldSnapshotId);
        var p2t1 = plan2.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("C", p2t1.TeamCode);
        Assert.Equal("R1", p2t1.RoadCode);
        Assert.Equal(34, p2t1.EtaMinutes);
        // T2 仍在 B：虽然允许抢占，但无需抢占即可行，锁定规则生效
        var p2t2 = plan2.Assignments.Single(a => a.TaskCode == "T2");
        Assert.Equal("B", p2t2.TeamCode);
        Assert.False(p2t2.IsPreemption);
        Assert.Contains(p2t2.Reasons, r => r.Code == "locked_in_progress");

        // 6. 完全相同请求回放；同 inputVersion 配旧快照 S1 → 409 + 字段级差异
        var replay2 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-critical-v1", "replan", "R2 中断且 T1 生命危险升级", s2.Id));
        Assert.Equal(HttpStatusCode.OK, replay2.StatusCode);

        var conflict = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-critical-v1", "replan", "R2 中断且 T1 生命危险升级", s1.Id));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var doc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
        {
            var root = doc.RootElement;
            Assert.Equal("input_version_snapshot_conflict", root.GetProperty("conflict").GetString());
            Assert.Equal(plan2.PlanId.ToString(), root.GetProperty("existingPlanId").GetString());
            var diffs = root.GetProperty("differences").EnumerateArray().ToArray();
            // 差异方向：已绑定方案快照（S2，R2 已中断）→ 本次请求引用快照（S1，R2 未中断）
            Assert.Contains(diffs, d =>
                d.GetProperty("entityType").GetString() == "road" &&
                d.GetProperty("code").GetString() == "R2" &&
                d.GetProperty("field").GetString() == "isBlocked" &&
                d.GetProperty("from").GetString() == "true" &&
                d.GetProperty("to").GetString() == "false");
            Assert.Contains(diffs, d =>
                d.GetProperty("entityType").GetString() == "task" &&
                d.GetProperty("code").GetString() == "T1" &&
                d.GetProperty("field").GetString() == "danger");
        }

        // 7. 过期快照引用（新 inputVersion + 旧快照 S1）→ 409 stale_snapshot
        var stale = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("brand-new-v1", "replan", "引用旧快照", s1.Id));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var doc = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()))
        {
            Assert.Equal("stale_snapshot", doc.RootElement.GetProperty("conflict").GetString());
            Assert.NotEmpty(doc.RootElement.GetProperty("differences").EnumerateArray());
        }

        // 8. diff 必须引用两版快照、T1/T2 与事件 road-r2-closed-01
        var diff = await _client.GetFromJsonAsync<PlanDiffDto>($"/api/allocations/{plan2.PlanId}/diff");
        Assert.NotNull(diff);
        Assert.Equal(s1.Id, diff!.FromSnapshot!.Id);
        Assert.Equal(s2.Id, diff.ToSnapshot!.Id);
        Assert.Equal("reassigned", diff.Changes.Single(c => c.TaskCode == "T1").ChangeType);
        Assert.Equal("unchanged", diff.Changes.Single(c => c.TaskCode == "T2").ChangeType);
        var r2Change = diff.WorldChanges.Single(w => w.EntityType == "road" && w.Code == "R2" && w.Field == "isBlocked");
        Assert.Equal("road-r2-closed-01", r2Change.EventId);
        Assert.Contains(diff.WorldChanges, w => w.EntityType == "task" && w.Code == "T1" && w.Field == "danger");

        // 9. 解释 API 引用事件与快照
        var explanation = await _client.GetFromJsonAsync<JsonElement>($"/api/allocations/{plan2.PlanId}/explanation");
        var roadEvents = explanation.GetProperty("roadEvents").EnumerateArray().ToArray();
        Assert.Contains(roadEvents, e => e.GetProperty("eventId").GetString() == "road-r2-closed-01");
        Assert.Equal(s2.Id.ToString(), explanation.GetProperty("plan").GetProperty("worldSnapshotId").GetString());
    }
}
