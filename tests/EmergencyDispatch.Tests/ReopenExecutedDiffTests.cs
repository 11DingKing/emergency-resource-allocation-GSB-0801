namespace EmergencyDispatch.Tests;

using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure;
using EmergencyDispatch.Solver;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Round-3 scenario with the real solver: reopen R2 via <c>road-r2-open-02</c>, mark T1 as
/// executed, then replan as <c>r2-reopened-v2</c>. An executed task must not be moved even
/// though the reopened road offers a shorter ETA; a stale snapshot that missed the reopen
/// event must conflict; and the round-3 plan stays diff-traceable against round 2.
/// </summary>
public class ReopenExecutedDiffTests
{
    private static AllocationService Service(SqliteTestDatabase db) =>
        new(db.NewContext(), new GreedyAllocationSolver(), NullLogger<AllocationService>.Instance);

    private static async Task<string> SnapshotVersion(SqliteTestDatabase db)
    {
        await using var ctx = db.NewContext();
        return (await new SnapshotService(ctx).BuildAsync()).Version;
    }

    /// <summary>Drive the world to the post-round-2 state: R2 closed, T1 critical, plan v1+v2.</summary>
    private static async Task<(int v1, int v2)> ArrangeThroughRound2(SqliteTestDatabase db)
    {
        var snap0 = await SnapshotVersion(db);
        var r1 = await Service(db).SolveAsync(new SolveRequest { InputVersion = "v1", SnapshotVersion = snap0 });

        await Service(db).RecordRoadEventAsync("road-r2-closed-01", "R2", closed: true);
        await Service(db).SetTaskDangerAsync("T1", DangerLevels.Critical);
        var snap2 = await SnapshotVersion(db);
        var r2 = await Service(db).SolveAsync(
            new SolveRequest { InputVersion = "r2-critical-v1", SnapshotVersion = snap2, IsReplan = true });

        return (r1.Version!.VersionNumber, r2.Version!.VersionNumber);
    }

    [Fact]
    public async Task Executed_task_is_not_moved_when_reopened_road_offers_shorter_eta()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var (_, v2) = await ArrangeThroughRound2(db);

        // Round 2 routed T1 over R1 at 30 min (R2 was cut).
        var round2 = await Service(db).GetVersionAsync(v2);
        var t1Round2 = round2!.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("R1", t1Round2.RoadSegmentCode);
        Assert.Equal(30, t1Round2.ArrivalMinutes);

        // Reopen R2 (road-r2-open-02) and mark T1 executed (pins it to A + V-HIGH from v2).
        await Service(db).RecordRoadEventAsync("road-r2-open-02", "R2", closed: false);
        var marked = await Service(db).MarkTaskExecutedAsync("T1");
        Assert.True(marked);

        // Replan r2-reopened-v2 against the current snapshot.
        var snap3 = await SnapshotVersion(db);
        var r3 = await Service(db).SolveAsync(
            new SolveRequest { InputVersion = "r2-reopened-v2", SnapshotVersion = snap3, IsReplan = true });
        Assert.False(r3.IsConflict);

        // Even though R2 is open again and its route (20 min) beats T1's current 30 min, the
        // executed T1 is held on-site and NOT moved: non-preemption of an in-progress task.
        var t1Round3 = r3.Version!.Assignments.Single(a => a.TaskCode == "T1");
        Assert.Equal("ON-SITE", t1Round3.RoadSegmentCode);
        Assert.Equal(0, t1Round3.ArrivalMinutes);
        Assert.Contains(r3.Version.AuditEntries,
            e => e.RuleCode == RuleCodes.NonPreemptionHeld && e.TaskCode == "T1");
        // T2 remains held throughout.
        Assert.Contains(r3.Version.AuditEntries,
            e => e.RuleCode == RuleCodes.NonPreemptionHeld && e.TaskCode == "T2");
    }

    [Fact]
    public async Task Stale_snapshot_missing_reopen_event_conflicts_and_latest_points_to_winner()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await ArrangeThroughRound2(db);

        // Capture the stale snapshot (before the reopen event), then reopen R2 and mark executed.
        var staleSnapshot = await SnapshotVersion(db);
        await Service(db).RecordRoadEventAsync("road-r2-open-02", "R2", closed: false);
        await Service(db).MarkTaskExecutedAsync("T1");
        var winningSnapshot = await SnapshotVersion(db);
        Assert.NotEqual(staleSnapshot, winningSnapshot);

        // The winning replan binds to the current world.
        var winner = await Service(db).SolveAsync(
            new SolveRequest { InputVersion = "r2-reopened-v2", SnapshotVersion = winningSnapshot, IsReplan = true });
        Assert.False(winner.IsConflict);

        // A concurrent submit that reused the same input version but the STALE snapshot (which
        // omits road-r2-open-02) must conflict — never replay — and the diff cites the event.
        var stale = await Service(db).SolveAsync(
            new SolveRequest { InputVersion = "r2-reopened-v2", SnapshotVersion = staleSnapshot, IsReplan = true });
        Assert.True(stale.IsConflict);
        Assert.Null(stale.Version);
        Assert.Equal(winningSnapshot, stale.Conflict!.StoredSnapshotVersion);
        Assert.Equal(staleSnapshot, stale.Conflict.RequestedSnapshotVersion);
        Assert.Contains(stale.Conflict.RoadEvents, e => e.EventId == "road-r2-open-02");
        Assert.Contains(stale.Conflict.Diff.Changes,
            c => c.Path == "road[R2].lastEventId");

        // The current (latest) plan points to the winning world snapshot.
        var latest = await Service(db).GetLatestAsync();
        Assert.Equal(winningSnapshot, latest!.SnapshotVersion);
        Assert.Equal(winner.Version!.VersionNumber, latest.VersionNumber);
    }

    [Fact]
    public async Task Round3_plan_is_diff_traceable_against_round2()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var (_, v2) = await ArrangeThroughRound2(db);

        await Service(db).RecordRoadEventAsync("road-r2-open-02", "R2", closed: false);
        await Service(db).MarkTaskExecutedAsync("T1");
        var snap3 = await SnapshotVersion(db);
        var r3 = await Service(db).SolveAsync(
            new SolveRequest { InputVersion = "r2-reopened-v2", SnapshotVersion = snap3, IsReplan = true });

        var diff = await Service(db).DiffVersionsAsync(r3.Version!.VersionNumber, v2);
        Assert.NotNull(diff);

        // Snapshot-level: R2 reopened (isOpen true) and now attributed to road-r2-open-02.
        Assert.Contains(diff!.SnapshotDiff.Changes, c => c.Path == "road[R2].isOpen" && c.Current == "True");
        Assert.Contains(diff.SnapshotDiff.Changes, c => c.Path == "road[R2].lastEventId" && c.Current == "road-r2-open-02");
        // T1 status flipped to in progress between v2 and v3.
        Assert.Contains(diff.SnapshotDiff.Changes, c => c.Path == "task[T1].status");

        // Assignment-level: T1 moved from R1@30 (v2) to on-site@0 (v3, executed & held).
        var t1 = diff.AssignmentChanges.Single(c => c.TaskCode == "T1");
        Assert.Equal("Changed", t1.Kind);
        Assert.Equal("R1", t1.BeforeRoadCode);
        Assert.Equal(30, t1.BeforeArrivalMinutes);
        Assert.Equal("ON-SITE", t1.AfterRoadCode);
        Assert.Equal(0, t1.AfterArrivalMinutes);

        // T2 unchanged, and the road event is available for traceability.
        var t2 = diff.AssignmentChanges.Single(c => c.TaskCode == "T2");
        Assert.Equal("Unchanged", t2.Kind);
        Assert.Contains(diff.RoadEvents, e => e.EventId == "road-r2-open-02");
    }
}
