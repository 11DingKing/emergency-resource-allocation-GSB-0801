# EmergencyDispatch — 应急资源动态调度后端

多地并发积水 / 边坡风险 / 大风破坏场景下，调度中心对「队伍 × 车辆 × 任务 × 路段」做动态分配，支持初始求解、重排与结果解释。技术栈：.NET 10 / ASP.NET Core / Entity Framework Core / PostgreSQL。

## 项目结构

```
src/
  EmergencyDispatch.Domain          实体、枚举、求解器契约（IAllocationSolver）、稳定原因码
  EmergencyDispatch.Solver          确定性求解器（纯函数，仅依赖 Domain，不碰 API/数据库）
  EmergencyDispatch.Infrastructure  EF Core + Npgsql、迁移、种子、编排服务（事务/幂等/重试）
  EmergencyDispatch.Api             控制器（薄）、DTO、OpenAPI；不含任何分配算法
tests/
  EmergencyDispatch.Tests           求解器规则 / 编排幂等 / PG 并发 / API 全流程（17 个用例）
```

## 前置条件

- .NET 10 SDK（`dotnet --version` ≥ 10.0.100）
- PostgreSQL 14+，本机监听 `localhost:5432`
- 迁移工具：`dotnet tool install --global dotnet-ef`

## 原生命令

```bash
# 还原 / 构建 / 测试
dotnet restore
dotnet build
dotnet test                                   # InMemory + 求解器用例
ERA_TEST_PG="Host=localhost;Database=emergency_dispatch_test;Username=huangding" \
  dotnet test                                 # 追加 PostgreSQL 并发专项（自动重建测试库）

# 迁移（启动时也会自动 Migrate + 空库种子）
dotnet ef migrations add <Name> --project src/EmergencyDispatch.Infrastructure --startup-project src/EmergencyDispatch.Api
dotnet ef database update  --project src/EmergencyDispatch.Infrastructure --startup-project src/EmergencyDispatch.Api

# 启动（连接串在 src/EmergencyDispatch.Api/appsettings.json，可用环境变量 ConnectionStrings__Dispatch 覆盖）
dotnet run --project src/EmergencyDispatch.Api
# OpenAPI 文档: http://localhost:5080/openapi/v1.json
```

## API 一览

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/teams` `/api/roads` `/api/tasks` | 队伍（能力/车辆）、路段、任务 |
| PATCH | `/api/roads/{code}` | 道路中断/恢复 `{ "isBlocked": true }` |
| POST | `/api/tasks/{code}/danger` | 危险等级 `{ "level": "standard\|elevated\|critical" }` |
| POST | `/api/allocations/solve` | 初始求解 / 重排（幂等） |
| GET | `/api/allocations/current` | 当前生效版本 |
| GET | `/api/allocations/{id}` `/explanation` `/diff` | 方案详情 / 审计解释 / 版本差异 |

## 典型流程

```bash
# 1. 初始求解（T1→A 与 C 并列 30 分钟，字典序决胜取 A；T2 锁定给 B）
curl -X POST localhost:5080/api/allocations/solve -H 'Content-Type: application/json' \
  -d '{"inputVersion":"day1-0800","kind":"initial"}'          # 201

# 2. 同一 inputVersion 重复/并发提交 → 同一方案（200 + X-Idempotent-Replay: true）
curl -X POST localhost:5080/api/allocations/solve -H 'Content-Type: application/json' \
  -d '{"inputVersion":"day1-0800","kind":"initial"}'          # 200 回放

# 3. R2 中断后重排（T1 改派 C，经 R1 ETA 34 ≤ 35）
curl -X PATCH localhost:5080/api/roads/R2 -H 'Content-Type: application/json' -d '{"isBlocked":true}'
curl -X POST localhost:5080/api/allocations/solve -H 'Content-Type: application/json' \
  -d '{"inputVersion":"day1-0810-r2blocked","kind":"replan","reason":"R2 中断"}'  # 201

# 4. 查看差异与解释
curl localhost:5080/api/allocations/{planId}/diff
curl localhost:5080/api/allocations/{planId}/explanation

# 5. 全部道路中断 → 422 + 不可分配原因；方案仅审计落库，当前生效版本不变
```

## 设计要点

- **求解器隔离**：`IAllocationSolver` 定义在 Domain，输入为不可变快照；控制器只做校验与 DTO 映射，算法不进控制器；测试注入同一确定性实现（`CountingSolver` 包装）。
- **确定性**：输入先按编码排序；目标为全局 ETA 总和最小（分钟）；并列时按队伍编码字典序决胜（`tie_break_team_code` 记录进原因）。
- **不可抢占**：执行中任务默认锁定；仅当 replan 且生命安全任务危险等级升至 critical，且存在具备全部所需能力的替代队伍时才允许抢占，并写入 `preemption_danger_escalation` 原因。
- **幂等与并发**：`inputVersion` 有唯一索引；提交在 RepeatableRead 事务内完成（一致性快照），唯一冲突 / 序列化失败 / xmin 乐观并发失败 → 回滚重试 → 幂等回放。方案+分配+任务状态+旧版本作废单事务提交，**半套分配对外不可见**；Infeasible 方案只落库审计，从不生效。
- **一致性**：差异与解释 API 的规则文字直接读取求解时落库的 Reasons（jsonb），tie-break、不可分配原因、分配版本、审计解释同源一致。

## 种子数据

- 队伍：A（water_rescue, first_aid，车 3.4m）/ B（slope_patrol, first_aid，车 2.6m）/ C（三项全能，车 3.0m）
- 道路：R1 滨河公路（限高 3.2m，34 分钟）/ R2 山岭高架（限高 4.0m，30 分钟）
- 任务：T1 生命安全（first_aid，35 分钟到达时限）/ T2 边坡巡查（slope_patrol，60 分钟，B 执行中）
