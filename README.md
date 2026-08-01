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
| PATCH | `/api/roads/{code}` | 道路中断/恢复（简单覆盖，无事件审计） |
| POST | `/api/roads/events` | 记录道路事件 `{ "eventId": "road-r2-closed-01", "roadCode": "R2", "isBlocked": true }`，eventId 幂等 |
| GET | `/api/roads/events` | 道路事件审计列表 |
| POST | `/api/world/snapshots` | 捕获世界快照（道路/任务/队伍/车辆内容摘要，按摘要去重） |
| GET | `/api/world/snapshots/{id}` | 快照详情（每实体 digest + 内容） |
| POST | `/api/tasks/{code}/danger` | 危险等级 `{ "level": "standard\|elevated\|critical" }` |
| POST | `/api/allocations/solve` | 初始求解 / 重排（幂等 + 快照绑定） |
| GET | `/api/allocations/current` | 当前生效版本 |
| GET | `/api/allocations/{id}` `/explanation` `/diff` | 方案详情 / 审计解释 / 版本差异 |

## 典型流程

```bash
# 1. 捕获世界快照，完成初始求解（请求绑定快照）
S1=$(curl -s -X POST localhost:5080/api/world/snapshots | jq -r .id)
curl -X POST localhost:5080/api/allocations/solve -H 'Content-Type: application/json' \
  -d "{\"inputVersion\":\"day1-0800\",\"kind\":\"initial\",\"worldSnapshotId\":\"$S1\"}"   # 201

# 2. 完全相同（inputVersion + 同一快照）→ 幂等回放（200 + X-Idempotent-Replay: true）
# 3. 记录道路事件（幂等）并把 T1 升为 critical
curl -X POST localhost:5080/api/roads/events -H 'Content-Type: application/json' \
  -d '{"eventId":"road-r2-closed-01","roadCode":"R2","isBlocked":true}'
curl -X POST localhost:5080/api/tasks/T1/danger -H 'Content-Type: application/json' -d '{"level":"critical"}'

# 4. 世界已变化 → 捕获新快照后重排
S2=$(curl -s -X POST localhost:5080/api/world/snapshots | jq -r .id)
curl -X POST localhost:5080/api/allocations/solve -H 'Content-Type: application/json' \
  -d "{\"inputVersion\":\"r2-critical-v1\",\"kind\":\"replan\",\"reason\":\"R2 中断\",\"worldSnapshotId\":\"$S2\"}"  # 201

# 5. 同 inputVersion 配不同快照 → 409 input_version_snapshot_conflict + 字段级差异
#    新 inputVersion 引用过期快照 → 409 stale_snapshot + 字段级差异

# 6. 差异 / 解释：引用两版快照、T1/T2 与事件 road-r2-closed-01
curl localhost:5080/api/allocations/{planId}/diff
curl localhost:5080/api/allocations/{planId}/explanation
```

## 设计要点

- **求解器隔离**：`IAllocationSolver` 定义在 Domain，输入为不可变快照；控制器只做校验与 DTO 映射，算法不进控制器；测试注入同一确定性实现（`CountingSolver` 包装）。
- **确定性**：输入先按编码排序；目标为全局 ETA 总和最小（分钟）；并列时按队伍编码字典序决胜（`tie_break_team_code` 记录进原因）。
- **不可抢占**：执行中任务默认锁定；仅当 replan 且生命安全任务危险等级升至 critical，且存在具备全部所需能力的替代队伍时才允许抢占，并写入 `preemption_danger_escalation` 原因。
- **快照绑定**：`POST /api/world/snapshots` 把道路/任务/队伍/车辆的内容规范化为逐实体 SHA-256 摘要并整体去重（内容寻址）。求解请求必须携带 `worldSnapshotId`；每份方案落库其采用的快照，diff/解释可给出两版快照的字段级差异并把道路通断归因到道路事件（`lastEventId`）。
- **幂等与并发**：幂等键 = `inputVersion` + 世界快照。完全相同的请求 → 回放同一方案；同 `inputVersion` 配不同快照 → `409 input_version_snapshot_conflict`；引用已过期快照 → `409 stale_snapshot`；两者都带字段级差异，绝不把旧方案伪装成成功。`inputVersion` 与事件 `eventId` 均有唯一索引；提交在 RepeatableRead 事务内完成，唯一冲突 / 序列化失败 / xmin 乐观并发失败 → 回滚重试 → 幂等回放。方案+分配+任务状态+旧版本作废单事务提交，**半套分配对外不可见**；Infeasible 方案只落库审计，从不生效。
- **一致性**：差异与解释 API 的规则文字直接读取求解时落库的 Reasons（jsonb），tie-break、不可分配原因、分配版本、世界快照、审计解释同源一致。

## 种子数据

- 队伍：A（water_rescue, first_aid，车 3.4m）/ B（slope_patrol, first_aid，车 2.6m）/ C（三项全能，车 3.0m）
- 道路：R1 滨河公路（限高 3.2m，34 分钟）/ R2 山岭高架（限高 4.0m，30 分钟）
- 任务：T1 生命安全（first_aid，35 分钟到达时限）/ T2 边坡巡查（slope_patrol，60 分钟，B 执行中）
