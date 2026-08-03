namespace EmergencyDispatch.Infrastructure;

using EmergencyDispatch.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Builds the canonical <see cref="WorldSnapshot"/> from the database (with a deterministic
/// ordering so the digest is stable) and computes field-level diffs between two snapshots.
/// The diff is what a conflicting solve submission returns, so an operator can see exactly
/// which roads/tasks changed relative to the plan already on record.
/// </summary>
public sealed class SnapshotService
{
    private readonly DispatchDbContext _db;

    public SnapshotService(DispatchDbContext db) => _db = db;

    public async Task<WorldSnapshot> BuildAsync(CancellationToken ct = default)
    {
        var teams = await _db.Teams.AsNoTracking().ToListAsync(ct);
        var vehicles = await _db.Vehicles.AsNoTracking().ToListAsync(ct);
        var tasks = await _db.Tasks.AsNoTracking().ToListAsync(ct);
        var roads = await _db.RoadSegments.AsNoTracking().ToListAsync(ct);
        var routes = await _db.Routes.AsNoTracking().ToListAsync(ct);
        var events = await _db.RoadEvents.AsNoTracking().ToListAsync(ct);

        var teamById = teams.ToDictionary(t => t.Id);
        var vehicleById = vehicles.ToDictionary(v => v.Id);
        var roadById = roads.ToDictionary(r => r.Id);
        var taskById = tasks.ToDictionary(t => t.Id);

        // Latest road event per road code, to attribute the current open/closed state.
        var lastEventByRoad = events
            .GroupBy(e => e.RoadCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.RecordedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).First().EventId,
                StringComparer.Ordinal);

        return Normalize(new WorldSnapshot
        {
            Version = string.Empty,
            Roads = roads.Select(r => new WorldSnapshot.RoadState
            {
                Code = r.Code,
                HeightLimitMeters = r.HeightLimitMeters,
                IsOpen = r.IsOpen,
                LastEventId = lastEventByRoad.TryGetValue(r.Code, out var ev) ? ev : null,
            }).ToList(),
            Tasks = tasks.Select(t => new WorldSnapshot.TaskState
            {
                Code = t.Code,
                RequiredCapabilities = t.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                DeadlineMinutes = t.DeadlineMinutes,
                ServiceMinutes = t.ServiceMinutes,
                DangerLevel = DangerLevels.Name(t.DangerLevel),
                Status = t.Status.ToString(),
                ExecutingTeamCode = t.ExecutingTeamId is Guid tid && teamById.TryGetValue(tid, out var tm) ? tm.Code : null,
                ExecutingVehicleCode = t.ExecutingVehicleId is Guid vid && vehicleById.TryGetValue(vid, out var vh) ? vh.Code : null,
            }).ToList(),
            Teams = teams.Select(t => new WorldSnapshot.TeamState
            {
                Code = t.Code,
                Capabilities = t.Capabilities.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            }).ToList(),
            Vehicles = vehicles.Select(v => new WorldSnapshot.VehicleState
            {
                Code = v.Code,
                HeightMeters = v.HeightMeters,
            }).ToList(),
            Routes = routes.Select(r => new WorldSnapshot.RouteState
            {
                TaskCode = taskById[r.TaskId].Code,
                RoadCode = roadById[r.RoadSegmentId].Code,
                TravelMinutes = r.TravelMinutes,
            }).ToList(),
        });
    }

    /// <summary>Order every collection by stable code so the canonical form is deterministic.</summary>
    private static WorldSnapshot Normalize(WorldSnapshot s)
    {
        var normalized = s with
        {
            Roads = s.Roads.OrderBy(r => r.Code, StringComparer.Ordinal).ToList(),
            Tasks = s.Tasks.OrderBy(t => t.Code, StringComparer.Ordinal).ToList(),
            Teams = s.Teams.OrderBy(t => t.Code, StringComparer.Ordinal).ToList(),
            Vehicles = s.Vehicles.OrderBy(v => v.Code, StringComparer.Ordinal).ToList(),
            Routes = s.Routes
                .OrderBy(r => r.TaskCode, StringComparer.Ordinal)
                .ThenBy(r => r.RoadCode, StringComparer.Ordinal)
                .ToList(),
        };
        return normalized with { Version = WorldSnapshot.ComputeDigest(normalized) };
    }

    /// <summary>
    /// Compute a field-level diff describing how <paramref name="current"/> differs from the
    /// <paramref name="stored"/> snapshot that a prior plan was bound to.
    /// </summary>
    public static SnapshotDiff Diff(WorldSnapshot stored, WorldSnapshot current)
    {
        var changes = new List<FieldChange>();

        DiffKeyed(changes, "road", stored.Roads, current.Roads, r => r.Code, (path, a, b) =>
        {
            Compare(changes, $"{path}.isOpen", a?.IsOpen, b?.IsOpen);
            Compare(changes, $"{path}.heightLimitMeters", a?.HeightLimitMeters, b?.HeightLimitMeters);
            Compare(changes, $"{path}.lastEventId", a?.LastEventId, b?.LastEventId);
        });

        DiffKeyed(changes, "task", stored.Tasks, current.Tasks, t => t.Code, (path, a, b) =>
        {
            Compare(changes, $"{path}.dangerLevel", a?.DangerLevel, b?.DangerLevel);
            Compare(changes, $"{path}.status", a?.Status, b?.Status);
            Compare(changes, $"{path}.deadlineMinutes", a?.DeadlineMinutes, b?.DeadlineMinutes);
            Compare(changes, $"{path}.executingTeamCode", a?.ExecutingTeamCode, b?.ExecutingTeamCode);
            Compare(changes, $"{path}.requiredCapabilities",
                a is null ? null : string.Join(",", a.RequiredCapabilities),
                b is null ? null : string.Join(",", b.RequiredCapabilities));
        });

        DiffKeyed(changes, "route", stored.Routes, current.Routes,
            r => $"{r.TaskCode}->{r.RoadCode}", (path, a, b) =>
                Compare(changes, $"{path}.travelMinutes", a?.TravelMinutes, b?.TravelMinutes));

        return new SnapshotDiff
        {
            StoredVersion = stored.Version,
            CurrentVersion = current.Version,
            Changes = changes
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static void DiffKeyed<T>(
        List<FieldChange> changes, string kind,
        IEnumerable<T> stored, IEnumerable<T> current,
        Func<T, string> key, Action<string, T?, T?> compareFields)
        where T : class
    {
        var storedByKey = stored.ToDictionary(key, x => x, StringComparer.Ordinal);
        var currentByKey = current.ToDictionary(key, x => x, StringComparer.Ordinal);
        foreach (var k in storedByKey.Keys.Union(currentByKey.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            storedByKey.TryGetValue(k, out var a);
            currentByKey.TryGetValue(k, out var b);
            var path = $"{kind}[{k}]";
            if (a is null) { changes.Add(new FieldChange { Path = path, Stored = "(absent)", Current = "(present)" }); }
            else if (b is null) { changes.Add(new FieldChange { Path = path, Stored = "(present)", Current = "(absent)" }); }
            compareFields(path, a, b);
        }
    }

    private static void Compare<T>(List<FieldChange> changes, string path, T? stored, T? current)
    {
        var s = stored?.ToString();
        var c = current?.ToString();
        if (!string.Equals(s, c, StringComparison.Ordinal))
        {
            changes.Add(new FieldChange { Path = path, Stored = s ?? "(null)", Current = c ?? "(null)" });
        }
    }
}

/// <summary>A field-level diff between the snapshot a plan was bound to and the current world.</summary>
public sealed record SnapshotDiff
{
    public required string StoredVersion { get; init; }
    public required string CurrentVersion { get; init; }
    public required IReadOnlyList<FieldChange> Changes { get; init; }

    /// <summary>Distinct entity paths that changed, for a quick reference list (e.g. "task[T1]").</summary>
    public IReadOnlyList<string> AffectedEntities =>
        Changes.Select(c => c.Path.Split('.')[0]).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
}

public sealed record FieldChange
{
    public required string Path { get; init; }
    public required string Stored { get; init; }
    public required string Current { get; init; }
}
