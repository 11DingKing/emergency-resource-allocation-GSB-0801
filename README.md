# Emergency Resource Allocation API

Dynamic emergency dispatch backend built with **.NET 10**, **ASP.NET Core**, **Entity Framework Core** and **PostgreSQL**. It manages teams, their capabilities, vehicles, emergency tasks, road segments and immutable allocation versions with a fully auditable explanation trail.

## Architecture

```
src/
  EmergencyAllocation.Domain/         # Entities, solver interface, pure deterministic solver, DTOs
  EmergencyAllocation.Infrastructure/ # EF Core (PostgreSQL), migrations, allocation service, seed data
  EmergencyAllocation.Api/            # Thin ASP.NET Core controllers, OpenAPI
tests/
  EmergencyAllocation.Tests/          # xUnit + FluentAssertions (solver, service, HTTP API)
```

The solver is hidden behind `IAllocationSolver` and receives an immutable `SolverRequest`. No EF Core entities cross that boundary, so tests can inject a deterministic or fake solver. Allocation algorithms live entirely in the domain layer — **controllers contain no allocation logic**.

## Rules encoded

- Time is always measured in minutes.
- Capabilities are stable string constants (`water-rescue`, `slope-inspection`, `first-aid`).
- Each solve request carries an `inputVersion`; the service builds an idempotency key `{operation}:{inputVersion}`.
- Tasks that are already `InProgress` are **kept by default and cannot be preempted**.
- A started task is only reassigned when:
  1. The operation is `rearrange`,
  2. The task's severity is `LifeSafety`, and
  3. Another team has **all** required capabilities and a reachable route.
  The reason is always written to the audit trail.
- Each team/vehicle can serve at most one task per version.
- Candidate selection is fully deterministic:
  1. Deadline-met first,
  2. Lower travel time,
  3. Lower vehicle height,
  4. Team code ascending,
  5. Vehicle code ascending.
- Height limits and road closures are evaluated per vehicle.
- Allocation versions are written in a single serializable transaction. External callers only ever see the fully committed version — a half-written assignment is never observable. Concurrent calls with the same idempotency key are de-duplicated; transient Npgsql serialization failures are retried.
- When the solver throws, a `Failed` version is recorded but task assignments remain untouched.

## API

| Method & path | Purpose |
| --- | --- |
| `POST /api/allocations/initial` | Compute the initial allocation for pending tasks. |
| `POST /api/allocations/rearrange` | Re-run after a road interruption or severity change. |
| `GET  /api/allocations/versions/latest` | Latest committed version. |
| `GET  /api/allocations/versions/{id}` | One version with full assignments and audit trail. |
| `POST /api/allocations/roads/interrupt` | Close a road and bump the road snapshot version. |
| `POST /api/allocations/roads/reopen` | Reopen a road and bump the road snapshot version. |
| `GET  /api/catalog/teams` / `tasks` / `roads` | Inspect seed/current state. |

OpenAPI document is exposed in Development at `/openapi/v1.json`.

## Seed data

- **Team A** — `water-rescue`, `first-aid`; vehicle `VA` height `3.4 m`.
- **Team B** — `slope-inspection`, `first-aid`; vehicle `VB` height `2.6 m`, already on a 60-minute slope task.
- **Team C** — all three capabilities; vehicle `VC` height `3.0 m`.
- Task **LIFE-001** — requires water rescue + first aid, must arrive within **35 minutes**.
- Task **SLOPE-009** — 60-minute slope task already being executed by B (non-preemptible).
- Roads **R1** (limit 3.2 m) and **R2** (limit 4.0 m), plus **RB** for the active slope task.

## Native commands

From the repository root:

```bash
# 1. Restore dependencies
dotnet restore

# 2. Build everything
dotnet build

# 3. Run automated tests
dotnet test

# 4. Apply PostgreSQL migrations (requires a running Postgres)
dotnet ef database update \
  --project src/EmergencyAllocation.Infrastructure \
  --startup-project src/EmergencyAllocation.Api

# 5. Start the API (http://localhost:5087, https://localhost:7264)
dotnet run --project src/EmergencyAllocation.Api
```

For a quick local start without PostgreSQL, the API also supports an in-memory store:

```bash
dotnet run --project src/EmergencyAllocation.Api -- --UseInMemory=true
```

Default PostgreSQL connection string (override via `ConnectionStrings__Postgres` or `appsettings.json`):

```
Host=localhost;Port=5432;Database=emergency_alloc;Username=postgres;Password=postgres
```

## Quick scenario: before and after a road interruption

Initial state:

```http
POST /api/allocations/initial
{ "inputVersion": "init-v1" }
```

Result: `LIFE-001 → Team C / VC` via `R2` (22 min, meets the 35-minute deadline); `SLOPE-009 → Team B / VB` is kept.

Then `R2` floods:

```http
POST /api/allocations/roads/interrupt
{ "roadCode": "R2", "reason": "Flooded", "inputVersion": "road-v2" }

POST /api/allocations/rearrange
{ "inputVersion": "rearrange-v1", "reason": "R2 flooded" }
```

Result and explanation: `LIFE-001` becomes **Unassigned**, because:

- `VA` (3.4 m) cannot use `R1` (3.2 m limit),
- `VB` is locked to the in-progress slope task and also lacks `water-rescue`,
- `VC`'s only path through `R2` is closed,
- `SLOPE-009` remains on B (non-preemptible unless its severity rises to LifeSafety **and** a fully-capable replacement exists).

The response includes an `audit` array with a `candidate-reject` entry per rejected candidate, a `no-feasible-solution` summary, and the `roadSnapshotVersion = 2`, so every assignment decision can be traced back to the exact rule that produced it.
