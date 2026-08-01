using EmergencyDispatch.Api.Contracts;
using EmergencyDispatch.Domain;
using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmergencyDispatch.Api.Controllers;

[ApiController]
[Route("api/allocations")]
public class AllocationsController(AllocationOrchestrator orchestrator) : ControllerBase
{
    /// <summary>初始求解 / 重排。同一 inputVersion 重复或并发提交返回同一方案（幂等）。</summary>
    [HttpPost("solve")]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Solve([FromBody] SolveRequestDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.InputVersion) || dto.InputVersion.Length > 128)
            return BadRequestProblem("inputVersion 必填且不超过 128 字符，用于幂等控制。");

        if (!Enum.TryParse<PlanKind>(dto.Kind, ignoreCase: true, out var kind))
            return BadRequestProblem("kind 必须是 initial 或 replan。");

        if (kind == PlanKind.Replan && string.IsNullOrWhiteSpace(dto.Reason))
            return BadRequestProblem("replan 必须填写 reason（触发原因将记入审计）。");

        SolveOutcome outcome;
        try
        {
            outcome = await orchestrator.SolveAsync(dto.InputVersion, kind, dto.Reason, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Title = "状态冲突", Detail = ex.Message, Status = StatusCodes.Status409Conflict });
        }

        var body = PlanMapper.ToDto(outcome.Plan);

        if (outcome.Plan.Status == PlanStatus.Infeasible)
        {
            // 无可行解：方案仅作审计落库，绝不生效；422 返回不可分配原因
            if (outcome.IsReplay) Response.Headers["X-Idempotent-Replay"] = "true";
            return UnprocessableEntity(body);
        }

        if (outcome.IsReplay)
        {
            Response.Headers["X-Idempotent-Replay"] = "true";
            return Ok(body);
        }

        return CreatedAtAction(nameof(GetById), new { id = body.PlanId }, body);
    }

    /// <summary>当前生效的分配版本。</summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var plan = await orchestrator.LoadCurrentAsync(ct);
        return plan is null ? NotFoundProblem("当前没有生效中的分配方案。") : Ok(PlanMapper.ToDto(plan));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var plan = await orchestrator.LoadPlanByIdAsync(id, ct);
        return plan is null ? NotFoundProblem("方案不存在。") : Ok(PlanMapper.ToDto(plan));
    }

    /// <summary>结果解释：方案 + 每条决定的原因 + 与上一版本的差异，全部来自同一份落库审计数据。</summary>
    [HttpGet("{id:guid}/explanation")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Explanation(Guid id, CancellationToken ct)
    {
        var plan = await orchestrator.LoadPlanByIdAsync(id, ct);
        if (plan is null) return NotFoundProblem("方案不存在。");

        AllocationPlan? previous = plan.SupersedesPlanId is Guid prevId
            ? await orchestrator.LoadPlanByIdAsync(prevId, ct)
            : null;
        var diff = PlanDiffBuilder.Build(previous, plan);

        var notes = plan.Assignments
            .SelectMany(a => a.Reasons)
            .Where(r => r.Code is ReasonCodes.TieBreakTeamCode or ReasonCodes.PreemptionDangerEscalation)
            .Select(r => r.Message)
            .Distinct()
            .ToArray();

        return Ok(new
        {
            plan = PlanMapper.ToDto(plan),
            diff,
            notes,
            rules = new[]
            {
                "执行中任务默认不可抢占（locked_in_progress）",
                "仅当生命安全任务危险等级上升（critical）且存在具备全部能力的替代队伍时允许抢占重排（preemption_danger_escalation），并记录原因",
                "能力为稳定字符串集合，必须全部满足（capability_missing）",
                "车辆高度不得超过道路限高（height_exceeded），道路中断不可通行（road_blocked）",
                "到达时间不得超过任务时限（deadline_exceeded），时长统一为分钟",
                "目标为全局总成本（ETA 之和）最低；并列时按队伍编码字典序决胜（tie_break_team_code）"
            }
        });
    }

    /// <summary>与上一生效版本的分配差异，每项变更附对应规则。</summary>
    [HttpGet("{id:guid}/diff")]
    [ProducesResponseType(typeof(PlanDiffDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Diff(Guid id, CancellationToken ct)
    {
        var plan = await orchestrator.LoadPlanByIdAsync(id, ct);
        if (plan is null) return NotFoundProblem("方案不存在。");

        AllocationPlan? previous = plan.SupersedesPlanId is Guid prevId
            ? await orchestrator.LoadPlanByIdAsync(prevId, ct)
            : null;
        return Ok(PlanDiffBuilder.Build(previous, plan));
    }

    private ObjectResult BadRequestProblem(string detail) =>
        BadRequest(new ProblemDetails { Title = "请求不合法", Detail = detail, Status = StatusCodes.Status400BadRequest });

    private ObjectResult NotFoundProblem(string detail) =>
        NotFound(new ProblemDetails { Title = "未找到", Detail = detail, Status = StatusCodes.Status404NotFound });
}
