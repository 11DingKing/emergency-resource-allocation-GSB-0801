namespace EmergencyDispatch.Infrastructure;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmergencyDispatch.Domain;

/// <summary>
/// A canonical, immutable projection of the whole dispatch world (roads, tasks, teams,
/// vehicles, routes) at one instant, together with a deterministic content digest. The
/// digest is what a solve request binds to: two requests that carry the same
/// <see cref="Version"/> saw the same world, so re-submitting an identical
/// <c>inputVersion</c> may safely replay the stored plan. A different digest under the same
/// <c>inputVersion</c> is a conflict — the world moved — and must not replay the old plan.
/// </summary>
public sealed record WorldSnapshot
{
    public required string Version { get; init; }
    public required IReadOnlyList<RoadState> Roads { get; init; }
    public required IReadOnlyList<TaskState> Tasks { get; init; }
    public required IReadOnlyList<TeamState> Teams { get; init; }
    public required IReadOnlyList<VehicleState> Vehicles { get; init; }
    public required IReadOnlyList<RouteState> Routes { get; init; }

    public sealed record RoadState
    {
        public required string Code { get; init; }
        public required decimal HeightLimitMeters { get; init; }
        public required bool IsOpen { get; init; }
        public string? LastEventId { get; init; }
    }

    public sealed record TaskState
    {
        public required string Code { get; init; }
        public required IReadOnlyList<string> RequiredCapabilities { get; init; }
        public required int DeadlineMinutes { get; init; }
        public required int ServiceMinutes { get; init; }
        public required string DangerLevel { get; init; }
        public required string Status { get; init; }
        public string? ExecutingTeamCode { get; init; }
        public string? ExecutingVehicleCode { get; init; }
    }

    public sealed record TeamState
    {
        public required string Code { get; init; }
        public required IReadOnlyList<string> Capabilities { get; init; }
    }

    public sealed record VehicleState
    {
        public required string Code { get; init; }
        public required decimal HeightMeters { get; init; }
    }

    public sealed record RouteState
    {
        public required string TaskCode { get; init; }
        public required string RoadCode { get; init; }
        public required int TravelMinutes { get; init; }
    }

    /// <summary>Serializer settings that produce a stable canonical form for digest + diff.</summary>
    public static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
    };

    /// <summary>The canonical JSON of this snapshot (ordering already normalised by the builder).</summary>
    public string ToCanonicalJson() => JsonSerializer.Serialize(this with { Version = string.Empty }, CanonicalJson);

    /// <summary>
    /// Compute the digest ("sha256:...") over the canonical JSON with the version field
    /// blanked, so the digest depends only on content, not on itself.
    /// </summary>
    public static string ComputeDigest(WorldSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot with { Version = string.Empty }, CanonicalJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }
}
