namespace EmergencyDispatch.Domain;

/// <summary>任务类型。生命安全任务的危险等级上升是允许抢占重排的前提。</summary>
public enum TaskKind
{
    Generic = 0,
    LifeSafety = 1,
    SlopeOperation = 2
}

/// <summary>生命危险等级。仅 Critical 视为"生命危险等级上升"。</summary>
public enum DangerLevel
{
    Standard = 0,
    Elevated = 1,
    Critical = 2
}

public enum DispatchTaskStatus
{
    Pending = 0,
    Assigned = 1,
    InProgress = 2,
    Completed = 3
}

public enum PlanKind
{
    Initial = 0,
    Replan = 1
}

/// <summary>
/// Committed = 当前生效；Superseded = 已被新版本取代；Infeasible = 求解失败（仅审计，从不生效）。
/// </summary>
public enum PlanStatus
{
    Committed = 0,
    Superseded = 1,
    Infeasible = 2
}
