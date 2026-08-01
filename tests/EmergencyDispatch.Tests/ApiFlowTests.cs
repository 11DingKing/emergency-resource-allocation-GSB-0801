using System.Net;
using System.Net.Http.Json;
using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Infrastructure.Persistence;
using EmergencyDispatch.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EmergencyDispatch.Tests;

/// <summary>
/// API 全流程：初始求解 → 幂等重放 → R2 中断重排 → 差异/解释 → 全路段中断 422 → 当前版本不受影响。
/// 使用 InMemory 提供程序；PostgreSQL 并发专项见 PgConcurrencyTests。
/// </summary>
public class ApiFlowTests : IClassFixture<ApiFlowTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }
    }

    private readonly HttpClient _client;

    public ApiFlowTests(Factory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task 道路中断前后全流程()
    {
        // 1. 初始求解：T1 → A（与 C 并列 30 分钟，字典序决胜），T2 锁定 B
        var solve1 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("seed-v1", "initial", null));
        Assert.Equal(HttpStatusCode.Created, solve1.StatusCode);
        var plan1 = await solve1.Content.ReadFromJsonAsync<PlanDto>();
        Assert.NotNull(plan1);
        var p1t1 = plan1!.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("A", p1t1.TeamCode);
        Assert.Equal("R2", p1t1.RoadCode);
        Assert.Equal(30, p1t1.EtaMinutes);
        Assert.Contains(p1t1.Reasons, r => r.Code == "tie_break_team_code");
        var p1t2 = plan1.Assignments.Single(a => a.TaskCode == "T2");
        Assert.Equal("B", p1t2.TeamCode);
        Assert.Contains(p1t2.Reasons, r => r.Code == "locked_in_progress");

        // 2. 同一输入版本重复提交 → 200 幂等回放，同一方案
        var replay = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("seed-v1", "initial", null));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("X-Idempotent-Replay").Single());
        var replayPlan = await replay.Content.ReadFromJsonAsync<PlanDto>();
        Assert.Equal(plan1.PlanId, replayPlan!.PlanId);

        // 3. R2 山岭高架中断
        var patch = await _client.PatchAsJsonAsync("/api/roads/R2", new RoadUpdateDto(true));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // 4. 重排：T1 → C（A 因限高+中断不可达）
        var solve2 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("r2-blocked-v1", "replan", "R2 山岭高架中断"));
        Assert.Equal(HttpStatusCode.Created, solve2.StatusCode);
        var plan2 = await solve2.Content.ReadFromJsonAsync<PlanDto>();
        var p2t1 = plan2!.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("C", p2t1.TeamCode);
        Assert.Equal("R1", p2t1.RoadCode);
        Assert.Equal(34, p2t1.EtaMinutes);
        Assert.True(plan2.PlanVersion > plan1.PlanVersion);

        // 5. 差异：T1 reassigned，T2 unchanged，且规则可解释
        var diff = await _client.GetFromJsonAsync<PlanDiffDto>($"/api/allocations/{plan2.PlanId}/diff");
        Assert.NotNull(diff);
        Assert.Equal(plan1.PlanVersion, diff!.FromPlanVersion);
        var t1Change = diff.Changes.Single(c => c.TaskCode == "T1");
        Assert.Equal("reassigned", t1Change.ChangeType);
        Assert.Equal("A", t1Change.From!.TeamCode);
        Assert.Equal("C", t1Change.To!.TeamCode);
        Assert.Contains("中断", t1Change.Rule);
        Assert.Contains("限高", t1Change.Rule);
        var t2Change = diff.Changes.Single(c => c.TaskCode == "T2");
        Assert.Equal("unchanged", t2Change.ChangeType);
        Assert.Contains("不可抢占", t2Change.Rule);

        // 6. 全部路段中断 → 422，当前版本不受影响
        await _client.PatchAsJsonAsync("/api/roads/R1", new RoadUpdateDto(true));
        var solve3 = await _client.PostAsJsonAsync("/api/allocations/solve",
            new SolveRequestDto("all-blocked-v1", "replan", "全部道路中断"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, solve3.StatusCode);
        var failed = await solve3.Content.ReadFromJsonAsync<PlanDto>();
        Assert.Equal("infeasible", failed!.Status);
        Assert.Empty(failed.Assignments);
        Assert.Contains(failed.Unassigned.Single(u => u.TaskCode == "T1").Reasons, r => r.Code == "road_blocked");

        var current = await _client.GetFromJsonAsync<PlanDto>("/api/allocations/current");
        Assert.Equal(plan2.PlanId, current!.PlanId); // 半套分配从未生效
    }
}
