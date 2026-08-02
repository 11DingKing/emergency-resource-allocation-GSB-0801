using System.Security.Cryptography;
using System.Text;

namespace EmergencyAllocation.Domain.Services;

public sealed record SnapshotDigests(
    string Road,
    string Task,
    string Team,
    string Vehicle,
    long RoadSnapshotVersion);

public interface ISnapshotDigestService
{
    SnapshotDigests Compute(
        IReadOnlyList<Team> teams,
        IReadOnlyList<EmergencyTask> tasks,
        IReadOnlyList<RoadSegment> roads,
        IReadOnlyList<Vehicle> vehicles);

    string ComputePayloadDigest(string operation, string inputVersion, string? reason, string? roadEventId);
}

public sealed class SnapshotDigestService : ISnapshotDigestService
{
    public SnapshotDigests Compute(
        IReadOnlyList<Team> teams,
        IReadOnlyList<EmergencyTask> tasks,
        IReadOnlyList<RoadSegment> roads,
        IReadOnlyList<Vehicle> vehicles)
    {
        var roadSnap = roads
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => $"{r.Code}|{r.FromNodeId}>{r.ToNodeId}|{r.TravelTimeMinutes}|{(r.HeightLimitMeters?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "-")}|{(r.IsOpen ? 1 : 0)}|{r.RoadSnapshotVersion}");
        var roadDigest = Hash(string.Join("\n", roadSnap));

        var taskSnap = tasks
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .Select(t => $"{t.Code}|{t.Severity}|{t.Status}|V{t.SeverityVersion}|{t.LocationNodeId}|deadline={t.DeadlineMinutes?.ToString() ?? "-"}|team={t.AssignedTeamId?.ToString() ?? "-"}|veh={t.AssignedVehicleId?.ToString() ?? "-"}");
        var taskDigest = Hash(string.Join("\n", taskSnap));

        var teamSnap = teams
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .Select(t => $"{t.Code}|base={t.BaseNodeId}|avail={(t.IsAvailable ? 1 : 0)}|caps={string.Join(",", t.Capabilities.Select(c => c.Capability).OrderBy(x => x, StringComparer.Ordinal))}");
        var teamDigest = Hash(string.Join("\n", teamSnap));

        var vehSnap = vehicles
            .OrderBy(v => v.Code, StringComparer.Ordinal)
            .Select(v => $"{v.Code}|team={v.TeamId}|h={v.HeightMeters.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|avail={(v.IsAvailable ? 1 : 0)}");
        var vehDigest = Hash(string.Join("\n", vehSnap));

        var snapVersion = roads.Count == 0 ? 0L : roads.Max(r => r.RoadSnapshotVersion);

        return new SnapshotDigests(roadDigest, taskDigest, teamDigest, vehDigest, snapVersion);
    }

    public string ComputePayloadDigest(string operation, string inputVersion, string? reason, string? roadEventId)
        => Hash($"{operation}|{inputVersion}|{reason ?? string.Empty}|{roadEventId ?? string.Empty}");

    private static string Hash(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }
}
