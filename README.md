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
| **Danger levels** | Stable ordered names `routine < elevated < high < critical`; a **rise** in rank is the only trigger that may justify preemption. |
| **Snapshot binding** | Every solve carries an `inputVersion` **and** a `snapshotVersion` digest of the world it saw. Identical `inputVersion` + identical digest ⇒ replay; identical `inputVersion` + **different** digest ⇒ **409 with a field-level diff**, never a stale replay. |
| **Idempotency** | A unique DB index on `inputVersion` makes re-submitting an identical payload return the stored plan instead of re-solving. |
| **Solver isolation** | The algorithm sits behind `IAllocationSolver` in its own assembly. The API and DB depend only on the interface; tests inject a deterministic double. **No allocation logic lives in any controller.** |
| **Road events** | Road cuts/reopens are recorded as append-only events with a stable `eventId` (e.g. `road-r2-closed-01`), applied to the road and cited by reroute audits, explanations and conflict diffs. |
| **Non-preemption** | In-progress tasks are pinned and protected by default. |
| **Replan gating** | An in-progress task may only be preempted when a task's danger level has **risen** *and* a free team with **all** required capabilities can cover the preempted task; every such move is audited. |
| **Determinism** | Identical input ⇒ identical output, including tie-break order, unassigned reasons and audit trail. |
| **Atomicity** | A version (assignments + unassigned reasons + audit + baselines + bound snapshot) is written inside one serializable transaction. External readers never see a half-applied plan. |
| **Concurrency** | Concurrent submits of the same `inputVersion` are resolved by retry + the unique index: at most one row is created; a same-snapshot loser replays, a different-snapshot loser gets a 409. |

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
  - `T1`: life-safety, requires water-rescue + first-aid, **deadline 35 min**, seeded danger `high`.
  - `T2`: slope inspection, **already in progress by team B** (60 min), protected, danger `routine`.
- **Roads** — `R2` (fast) height limit **3.2 m**, `R1` (high-clearance alternate) height limit **4.0 m**.
- **Routes** — `T1` via `R2` (20 min) or `R1` (30 min); `T2` via `R1` (25 min).
- **RoadEvent** — append-only, stable `eventId` (e.g. `road-r2-closed-01`), records a road cut/reopen.
- **AllocationVersion** — the immutable, persisted result of one solve (idempotent on `inputVersion`,
  bound to a `snapshotVersion`), with its assignments, unassigned reasons, audit entries,
  per-task danger baselines and the canonical snapshot JSON it was solved against.

Stable codes:

- **Danger** — `routine`, `elevated`, `high`, `critical` (ordered).
- **Reasons** — `NO_CAPABLE_TEAM`, `NO_VEHICLE_AVAILABLE`, `NO_FEASIBLE_ROUTE`,
  `DEADLINE_EXCEEDED`, `TEAM_BUSY`, `PREEMPTION_NOT_ALLOWED`.
- **Rules** — `INITIAL_ASSIGNMENT`, `DETERMINISTIC_TIEBREAK`, `REROUTE_AFTER_ROAD_CUT`,
  `TEAM_REASSIGNED`, `NON_PREEMPTION_HELD`, `REPLAN_DANGER_ESCALATED`, `UNASSIGNED`, …

### Snapshot binding, replay vs. conflict

`GET /api/snapshot` returns the canonical world plus a deterministic `snapshotVersion`
(`sha256:…` over the ordered content). A solve/replan **must** send that digest:

- same `inputVersion` + same digest → **replay** the stored plan (`200`);
- same `inputVersion` + different digest → **`409` conflict** with a field-level diff of what
  moved (roads/tasks/routes) plus the road events, referencing the affected entities such as
  `road[R2].isOpen`, `road[R2].lastEventId`, `task[T1].dangerLevel`. The old plan is **never**
  replayed under a changed world.

### Deterministic tie-break

Candidates are ordered by **arrival minutes**, then ordinal `teamCode`, `vehicleCode`,
`roadSegmentCode`. When two options cost the same, the choice is recorded with a
`DETERMINISTIC_TIEBREAK` audit entry so equal-cost optima resolve identically every run.

---

## API

| Method & path | Purpose |
| --- | --- |
| `GET /api/snapshot` | Current world snapshot + `snapshotVersion` digest to bind a solve to. |
| `POST /api/allocations/solve` | Initial solve. Body `{ "inputVersion": "...", "snapshotVersion": "sha256:..." }`. `201` create, `200` replay, `409` snapshot conflict. |
| `POST /api/allocations/replan` | Replan (allows danger-escalated preemption). Same binding + conflict rules. |
| `GET /api/allocations/{versionNumber}` | Fetch a version. |
| `GET /api/allocations/latest` | Fetch the latest version. |
| `GET /api/allocations/{versionNumber}/explanation` | Ordered audit trail + assignments + unassigned reasons + road events. |
| `POST /api/roads/events` | Record a road event. Body `{ "eventId": "road-r2-closed-01", "roadCode": "R2", "closed": true }`. Idempotent on `eventId`. |
| `GET /api/roads` | List road segments and open/closed state. |
| `PUT /api/roads/{code}/state` | Cut/reopen a road directly (no event id). Body `{ "isOpen": false }`. |
| `PUT /api/tasks/{code}/danger` | Set a task's danger level. Body `{ "dangerLevel": "critical" }`. |

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

## Worked example: record a road event, escalate danger, replan

The seeded scenario, solved by the real greedy solver (reproduced verbatim by the
`RoadCutDiffDemo` test). Flow: solve → `POST /api/roads/events road-r2-closed-01` (close `R2`)
→ `PUT /api/tasks/T1/danger {"dangerLevel":"critical"}` → replan as `r2-critical-v1`.

**Before** — `R2` (3.2 m) and `R1` (4.0 m) both open:

```
version=1 kind=Initial cost=20min snapshot=sha256:9175b2ff…
  ASSIGN T1 -> team A, vehicle V-MID,  road R2,      arrive 20min
  ASSIGN T2 -> team B, vehicle V-LOW,  road ON-SITE, arrive 0min
  AUDIT #0 [NON_PREEMPTION_HELD]    T2: Task T2 is in progress and held with team B; not preemptable.
  AUDIT #1 [DETERMINISTIC_TIEBREAK] T1: equal-cost options at 20 min; picked team A, vehicle V-MID, road R2.
```

**After** — `road-r2-closed-01` closed `R2`, `T1` escalated `high → critical`:

```
version=2 kind=Replan cost=30min snapshot=sha256:155f75a6…
  ASSIGN T1 -> team A, vehicle V-HIGH, road R1,      arrive 30min
  ASSIGN T2 -> team B, vehicle V-LOW,  road ON-SITE, arrive 0min
  AUDIT #0 [NON_PREEMPTION_HELD]     T2: Task T2 is in progress and held with team B; not preemptable.
  AUDIT #1 [DETERMINISTIC_TIEBREAK]  T1: equal-cost options at 30 min; picked team A, vehicle V-HIGH, road R1.
  AUDIT #2 [REROUTE_AFTER_ROAD_CUT]  T1: rerouted onto road R1 (30 min) because faster road R2 (20 min) was closed by event road-r2-closed-01.
```

### The diff, and the rule behind each change

| Change | Before | After | Rule (references) |
| --- | --- | --- | --- |
| `T1` road | `R2` | `R1` | `REROUTE_AFTER_ROAD_CUT` — `R2` closed by **`road-r2-closed-01`**; `R1` is the only open route, still within `T1`'s 35-min deadline. |
| `T1` arrival | 20 min | 30 min | Consequence of the reroute; still within deadline. |
| `T1` vehicle | `V-MID` (3.0 m) | `V-HIGH` (3.4 m) | `DETERMINISTIC_TIEBREAK` — ordinally-smallest vehicle that fits `R1`'s 4.0 m limit. |
| `T2` | team B, on-site | team B, on-site | **Unchanged** — `NON_PREEMPTION_HELD`. `T2` keeps its crew; only `T1`'s danger rose, and `T1` did not need `T2`'s team, so no preemption. |

`T1`'s escalation to `critical` is recorded, but preemption of `T2` is **not** triggered: the
round-1 conditions require that the escalated task can only be served by preempting an
in-progress task's crew. Here `T1` is served by the free team `A`, so `T2` stays put.

If the same `inputVersion` (`r2-critical-v1`) is later re-submitted against the **old**
snapshot digest, the API returns **`409`** with a field-level diff citing `road[R2].isOpen`,
`road[R2].lastEventId = road-r2-closed-01` and `task[T1].dangerLevel: high → critical` — it does
**not** replay the stale plan.

---

## Edge cases exercised by the tests ([tests/EmergencyDispatch.Tests](tests/EmergencyDispatch.Tests))

- **No feasible solution** — all roads cut ⇒ `NO_FEASIBLE_ROUTE`; no capable team ⇒
  `NO_CAPABLE_TEAM`; reachable but too slow ⇒ `DEADLINE_EXCEEDED` (distinct reasons).
- **Two equal-cost optima** — resolved by the deterministic tie-break, identically on repeat.
- **Snapshot binding** — identical `inputVersion` + identical digest replays (`200`); identical
  `inputVersion` + different digest returns `409` with a field-level diff and road events.
- **Road snapshot changes mid-solve** — the persisted plan reflects the pre-mutation snapshot,
  proving the solver is isolated from concurrent writes.
- **Concurrent submits, same `inputVersion`** — same snapshot ⇒ exactly one row, loser replays;
  **different snapshots ⇒ the stale request never creates or replays a plan** (409).
- **Road events + danger escalation** — a reroute cites the `road-r2-closed-01` event; a danger
  rise to `critical` is recorded and gates (but here does not trigger) `T2` preemption.
- **Consistency** — deterministic tie-break, unassignable reasons, allocation version, snapshot
  digest and audit explanation all agree; a half-written plan is never externally visible.
