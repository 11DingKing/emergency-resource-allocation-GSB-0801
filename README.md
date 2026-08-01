# Emergency Resource Allocation — Dynamic Dispatch Backend

A dynamic dispatch backend for emergency response: when flooding, slope-failure risk and
high-wind damage strike several places at once, the dispatch centre must decide **which team
takes which vehicle to which task** within minutes — while roads can be cut without warning
and tasks already under way must not be casually stolen.

Built with **.NET 10**, **ASP.NET Core**, **Entity Framework Core** and **PostgreSQL**.

---

## Core guarantees

| Concern | How it is met |
| --- | --- |
| **Durations** | Always minutes (`int`), everywhere. |
| **Capabilities** | Stable string set (`water-rescue`, `slope-inspection`, `first-aid`) — never numeric enums. See [`Codes.cs`](src/EmergencyDispatch.Domain/Codes.cs). |
| **Idempotency** | Every solve carries an `inputVersion`; a unique DB index makes re-submitting the same version return the stored plan instead of re-solving. |
| **Solver isolation** | The algorithm sits behind `IAllocationSolver` in its own assembly. The API and DB depend only on the interface; tests inject a deterministic double. **No allocation logic lives in any controller.** |
| **Non-preemption** | In-progress tasks are pinned and protected by default. |
| **Replan gating** | An in-progress task may only be preempted when a task's danger level has **risen** *and* a free team with **all** required capabilities can cover the preempted task; every such move is audited. |
| **Determinism** | Identical input ⇒ identical output, including tie-break order, unassigned reasons and audit trail. |
| **Atomicity** | A version (assignments + unassigned reasons + audit + baselines) is written inside one serializable transaction. External readers never see a half-applied plan. |
| **Concurrency** | Concurrent submits of the same `inputVersion` are resolved by retry + the unique index: exactly one row is created; the loser reads the winner's version. |

## Architecture

```
EmergencyDispatch.Domain          entities, stable capability/reason/rule codes  (no dependencies)
EmergencyDispatch.Solver          IAllocationSolver + pure SolveInput/SolveResult + GreedyAllocationSolver
                                  (depends only on Domain; no EF, no ASP.NET, no I/O)
EmergencyDispatch.Infrastructure  EF Core DbContext (PostgreSQL), migrations, seed,
                                  AllocationService (read → solve → persist, idempotent/atomic/retry)
EmergencyDispatch.Api             thin controllers, DTOs, OpenAPI, startup
EmergencyDispatch.Tests           xUnit: solver, idempotency, concurrency, replan, infeasibility,
                                  tie-break, atomicity, mid-solve snapshot isolation, e2e
```

The solver receives an **immutable snapshot** (`SolveInput`) built from one consistent read,
and returns an immutable `SolveResult`. It performs no I/O, so it cannot observe a database
change that happens while it is running.

---

## Domain model

- **Teams** — `A`: water-rescue + first-aid; `B`: slope-inspection + first-aid; `C`: all three.
- **Vehicles** — `V-HIGH` 3.4 m, `V-LOW` 2.6 m, `V-MID` 3.0 m (shared pool).
- **Tasks** —
  - `T-LIFE`: life-safety, requires water-rescue + first-aid, **deadline 35 min**.
  - `T-SLOPE`: slope inspection, **already in progress by team B** (60 min), protected.
- **Roads** — `R-LOW` height limit **3.2 m**, `R-HIGH` height limit **4.0 m**.
- **Routes** — `T-LIFE` via `R-LOW` (20 min) or `R-HIGH` (30 min); `T-SLOPE` via `R-HIGH` (25 min).
- **AllocationVersion** — the immutable, persisted result of one solve (idempotent on `inputVersion`),
  with its assignments, unassigned reasons, audit entries and per-task danger baselines.

Stable codes:

- **Reasons** — `NO_CAPABLE_TEAM`, `NO_VEHICLE_AVAILABLE`, `NO_FEASIBLE_ROUTE`,
  `DEADLINE_EXCEEDED`, `TEAM_BUSY`, `PREEMPTION_NOT_ALLOWED`.
- **Rules** — `INITIAL_ASSIGNMENT`, `DETERMINISTIC_TIEBREAK`, `REROUTE_AFTER_ROAD_CUT`,
  `TEAM_REASSIGNED`, `NON_PREEMPTION_HELD`, `REPLAN_DANGER_ESCALATED`, `UNASSIGNED`, …

### Deterministic tie-break

Candidates are ordered by **arrival minutes**, then ordinal `teamCode`, `vehicleCode`,
`roadSegmentCode`. When two options cost the same, the choice is recorded with a
`DETERMINISTIC_TIEBREAK` audit entry so equal-cost optima resolve identically every run.

---

## API

| Method & path | Purpose |
| --- | --- |
| `POST /api/allocations/solve` | Initial solve. Body `{ "inputVersion": "..." }`. `201` on create, `200` on idempotent replay. |
| `POST /api/allocations/replan` | Replan (allows danger-escalated preemption). Idempotent on `inputVersion`. |
| `GET /api/allocations/{versionNumber}` | Fetch a version. |
| `GET /api/allocations/latest` | Fetch the latest version. |
| `GET /api/allocations/{versionNumber}/explanation` | Ordered audit trail + assignments + unassigned reasons. |
| `GET /api/roads` | List road segments and open/closed state. |
| `PUT /api/roads/{code}/state` | Cut/reopen a road. Body `{ "isOpen": false }`. |

OpenAPI document is served at `GET /openapi/v1.json` in Development.

---

## Prerequisites

- .NET 10 SDK (`dotnet --version` → `10.0.x`).
- PostgreSQL reachable via the `Dispatch` connection string
  ([`appsettings.json`](src/EmergencyDispatch.Api/appsettings.json)); default:
  `Host=localhost;Port=5432;Database=emergency_dispatch;Username=postgres;Password=postgres`.

> The automated tests do **not** need PostgreSQL — they run on an in-memory SQLite provider
> that exercises the same EF model, transactions and unique indexes.

## Native commands (restore / build / test / start)

```bash
# restore (also restores the local dotnet-ef tool from .config/dotnet-tools.json)
dotnet tool restore
dotnet restore

# build
dotnet build

# test
dotnet test

# run the API (applies migrations + seeds the scenario on startup)
dotnet run --project src/EmergencyDispatch.Api
```

### Database migrations

The initial migration lives in
[`src/EmergencyDispatch.Infrastructure/Migrations`](src/EmergencyDispatch.Infrastructure/Migrations).
It is applied automatically at startup. To manage it manually:

```bash
# create a new migration
dotnet ef migrations add <Name> \
  --project src/EmergencyDispatch.Infrastructure \
  --startup-project src/EmergencyDispatch.Infrastructure

# apply to the database
dotnet ef database update \
  --project src/EmergencyDispatch.Infrastructure \
  --startup-project src/EmergencyDispatch.Infrastructure
```

`dotnet ef` reads the connection string from `DISPATCH_CONNECTION_STRING` (falling back to the
local default) via [`DispatchDbContextFactory`](src/EmergencyDispatch.Infrastructure/DispatchDbContextFactory.cs).

---

## Worked example: one road cut, before and after

The seeded scenario, solved by the real greedy solver (reproduced verbatim by the
`RoadCutDiffDemo` test):

**Before the cut** — `R-LOW` (3.2 m) and `R-HIGH` (4.0 m) both open:

```
version=1 kind=Initial cost=20min
  ASSIGN T-LIFE  -> team A, vehicle V-MID,  road R-LOW,   arrive 20min
  ASSIGN T-SLOPE -> team B, vehicle V-LOW,  road ON-SITE, arrive 0min
  AUDIT #0 [NON_PREEMPTION_HELD]      T-SLOPE: in progress, held with team B; not preemptable.
  AUDIT #1 [DETERMINISTIC_TIEBREAK]   T-LIFE:  equal-cost options at 20 min; picked team A, vehicle V-MID, road R-LOW.
```

**Cut** `R-LOW`: `PUT /api/roads/R-LOW/state {"isOpen": false}`.

**After the cut** — only `R-HIGH` reaches `T-LIFE`:

```
version=2 kind=Replan cost=30min
  ASSIGN T-LIFE  -> team A, vehicle V-HIGH, road R-HIGH,  arrive 30min
  ASSIGN T-SLOPE -> team B, vehicle V-LOW,  road ON-SITE, arrive 0min
  AUDIT #0 [NON_PREEMPTION_HELD]    T-SLOPE: in progress, held with team B; not preemptable.
  AUDIT #1 [DETERMINISTIC_TIEBREAK] T-LIFE:  equal-cost options at 30 min; picked team A, vehicle V-HIGH, road R-HIGH.
```

### The diff, and the rule behind each change

| Change | Before | After | Rule |
| --- | --- | --- | --- |
| `T-LIFE` road | `R-LOW` | `R-HIGH` | Reroute forced — `R-LOW` cut; `R-HIGH` is the only open route (arrival within the 35-min deadline). |
| `T-LIFE` arrival | 20 min | 30 min | Consequence of the reroute; still within deadline. |
| `T-LIFE` vehicle | `V-MID` (3.0 m) | `V-HIGH` (3.4 m) | On the still-tied options for the new road, `DETERMINISTIC_TIEBREAK` picks the ordinally-smallest fitting vehicle for `R-HIGH`'s 4.0 m limit. |
| `T-SLOPE` | team B, on-site | team B, on-site | **Unchanged** — `NON_PREEMPTION_HELD`. The in-progress task keeps its crew; danger did not rise, so no preemption. |

No task went unserved and the protected in-progress task was never disturbed.

---

## Edge cases exercised by the tests ([tests/EmergencyDispatch.Tests](tests/EmergencyDispatch.Tests))

- **No feasible solution** — all roads cut ⇒ `NO_FEASIBLE_ROUTE`; no capable team ⇒
  `NO_CAPABLE_TEAM`; reachable but too slow ⇒ `DEADLINE_EXCEEDED` (distinct reasons).
- **Two equal-cost optima** — resolved by the deterministic tie-break, identically on repeat.
- **Road snapshot changes mid-solve** — the persisted plan reflects the pre-mutation snapshot,
  proving the solver is isolated from concurrent writes.
- **Concurrent submits of the same `inputVersion`** — exactly one version is created; the
  transaction loser retries and returns the committed version.
- **Consistency** — deterministic tie-break, unassignable reasons, allocation version and audit
  explanation all agree; a half-written plan is never externally visible.
