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
/// 第三轮：road-r2-open-02 恢复 R2 + T1 标记已执行 → r2-reopened-v2 重排。
/// 已执行任务不因 ETA 变短被移动；遗漏重开事件的旧快照必须 409；当前方案指向获胜快照；两轮 diff 均可追溯。
/// </summary>
public class Round3FlowTests : IClassFixture<Round3FlowTests.Factory>
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

    public Round3FlowTests(Factory factory) => _client = factory.CreateClient();

    private async Task<SnapshotDto> CaptureAsync()
    {
        var resp = await _client.PostAsync("/api/world/snapshots", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SnapshotDto>())!;
    }

    [Fact]
    public async Task 重开与已执行的第三轮重排()
    {
        // 前置：复现第 1、2 轮状态（T1 → C，T2 → B 执行中）
        var s1 = await CaptureAsync();
        var solve1 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("day1-0800", "initial", null, s1.Id));
        Assert.Equal(HttpStatusCode.Created, solve1.StatusCode);
        var plan1 = await solve1.Content.ReadFromJsonAsync<PlanDto>();

        await _client.PostAsJsonAsync("/api/roads/events", new RoadEventRequestDto("road-r2-closed-01", "R2", true, "积水塌方"));
        await _client.PostAsJsonAsync("/api/tasks/T1/danger", new DangerUpdateDto("critical", "水位上涨"));
        var s2 = await CaptureAsync();
        var solve2 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-critical-v1", "replan", "R2 中断且 T1 升级", s2.Id));
        Assert.Equal(HttpStatusCode.Created, solve2.StatusCode);
        var plan2 = await solve2.Content.ReadFromJsonAsync<PlanDto>();
        Assert.Equal("C", plan2!.Assignments.Single(a => a.TaskCode == "T1").TeamCode);

        // ③ 事件 road-r2-open-02 恢复 R2（幂等），T1 标记为已执行
        var open = await _client.PostAsJsonAsync("/api/roads/events",
            new RoadEventRequestDto("road-r2-open-02", "R2", false, "排水完成，恢复通行"));
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        var openReplay = await _client.PostAsJsonAsync("/api/roads/events",
            new RoadEventRequestDto("road-r2-open-02", "R2", false, "排水完成，恢复通行"));
        Assert.Equal(HttpStatusCode.OK, openReplay.StatusCode);

        var exec = await _client.PostAsJsonAsync("/api/tasks/T1/execution", new TaskExecutionDto("in_progress"));
        Assert.Equal(HttpStatusCode.OK, exec.StatusCode);
        var execTask = await exec.Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal("InProgress", execTask!.Status);
        Assert.Equal("C", execTask.CurrentTeamCode);

        // ④ 捕获 S3，以 r2-reopened-v2 重排：T1 锁定给 C（不因 R2 恢复后 ETA 更短被移动）
        var s3 = await CaptureAsync();
        var solve3 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-reopened-v2", "replan", "R2 恢复通行（road-r2-open-02），T1 已执行", s3.Id));
        Assert.Equal(HttpStatusCode.Created, solve3.StatusCode);
        var plan3 = await solve3.Content.ReadFromJsonAsync<PlanDto>();
        Assert.Equal(s3.Id, plan3!.WorldSnapshotId);
        var p3t1 = plan3.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("C", p3t1.TeamCode);
        Assert.Null(p3t1.RoadCode);
        Assert.Equal(0, p3t1.EtaMinutes);
        Assert.False(p3t1.IsPreemption);
        Assert.Contains(p3t1.Reasons, r => r.Code == "locked_in_progress");
        var p3t2 = plan3.Assignments.Single(a => a.TaskCode == "T2");
        Assert.Equal("B", p3t2.TeamCode);
        Assert.False(p3t2.IsPreemption);

        // ⑤ 完全相同请求回放；同 inputVersion 配遗漏 road-r2-open-02 的旧快照 S2 → 409
        var replay = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-reopened-v2", "replan", "R2 恢复通行（road-r2-open-02），T1 已执行", s3.Id));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var conflict = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-reopened-v2", "replan", "R2 恢复通行（road-r2-open-02），T1 已执行", s2.Id));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var doc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
        {
            var root = doc.RootElement;
            Assert.Equal("input_version_snapshot_conflict", root.GetProperty("conflict").GetString());
            Assert.Equal(plan3.PlanId.ToString(), root.GetProperty("existingPlanId").GetString());
            var diffs = root.GetProperty("differences").EnumerateArray().ToArray();
            // 已绑定 S3（R2 已恢复）→ 请求引用 S2（R2 仍中断，遗漏 road-r2-open-02）
            Assert.Contains(diffs, d =>
                d.GetProperty("entityType").GetString() == "road" &&
                d.GetProperty("code").GetString() == "R2" &&
                d.GetProperty("field").GetString() == "isBlocked" &&
                d.GetProperty("from").GetString() == "false" &&
                d.GetProperty("to").GetString() == "true");
            Assert.Contains(diffs, d =>
                d.GetProperty("entityType").GetString() == "road" &&
                d.GetProperty("code").GetString() == "R2" &&
                d.GetProperty("field").GetString() == "lastEventId" &&
                d.GetProperty("from").GetString() == "road-r2-open-02");
        }

        // ⑥ 当前方案只能指向获胜的世界快照 S3
        var current = await _client.GetFromJsonAsync<PlanDto>("/api/allocations/current");
        Assert.Equal(plan3.PlanId, current!.PlanId);
        Assert.Equal(s3.Id, current.WorldSnapshotId);

        // ⑦ 第 3 轮 diff：S2→S3，T1 未移动，worldChanges 归因 road-r2-open-02
        var diff3 = await _client.GetFromJsonAsync<PlanDiffDto>($"/api/allocations/{plan3.PlanId}/diff");
        Assert.NotNull(diff3);
        Assert.Equal(plan2.PlanVersion, diff3!.FromPlanVersion);
        Assert.Equal(s2.Id, diff3.FromSnapshot!.Id);
        Assert.Equal(s3.Id, diff3.ToSnapshot!.Id);
        var t1Change = diff3.Changes.Single(c => c.TaskCode == "T1");
        Assert.Equal("unchanged", t1Change.ChangeType);
        Assert.Equal("C", t1Change.To!.TeamCode);
        Assert.Contains("不可抢占", t1Change.Rule);
        var reopen = diff3.WorldChanges.Single(w => w.EntityType == "road" && w.Code == "R2" && w.Field == "isBlocked");
        Assert.Equal("true", reopen.From);
        Assert.Equal("false", reopen.To);
        Assert.Equal("road-r2-open-02", reopen.EventId);
        Assert.Contains(diff3.WorldChanges, w => w.EntityType == "task" && w.Code == "T1" && w.Field == "status"
            && w.From == "Assigned" && w.To == "InProgress");

        // ⑧ 第 2 轮 diff 仍可追溯（road-r2-closed-01）
        var diff2 = await _client.GetFromJsonAsync<PlanDiffDto>($"/api/allocations/{plan2!.PlanId}/diff");
        Assert.NotNull(diff2);
        Assert.Equal(s1.Id, diff2!.FromSnapshot!.Id);
        Assert.Equal(s2.Id, diff2.ToSnapshot!.Id);
        Assert.Equal("reassigned", diff2.Changes.Single(c => c.TaskCode == "T1").ChangeType);
        Assert.Contains(diff2.WorldChanges, w => w.EntityType == "road" && w.Code == "R2"
            && w.Field == "isBlocked" && w.EventId == "road-r2-closed-01");
    }
}
