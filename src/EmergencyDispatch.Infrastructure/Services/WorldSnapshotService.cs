using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmergencyDispatch.Domain;
using EmergencyDispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDispatch.Infrastructure.Services;

/// <summary>单个实体参与求解时的内容摘要。</summary>
public sealed record SnapshotEntryInfo(string EntityType, Guid EntityId, string Code, string Digest, string ContentJson);

/// <summary>字段级差异。EventId 在道路通断变化时归因到具体道路事件。</summary>
public sealed record FieldDiff(string EntityType, string Code, string Field, string? From, string? To, string? EventId);

/// <summary>
/// 世界快照服务：把道路/任务/队伍/车辆的当前内容固化为一组带 SHA-256 摘要的条目。
/// 快照按全局摘要去重（内容寻址）；求解请求必须引用快照，
/// 使"同一输入版本 + 不同世界状态"能够被识别并拒绝（409 + 字段级差异）。
/// </summary>
public sealed class WorldSnapshotService(DispatchDbContext db)
{
    public async Task<WorldSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var teams = await db.Teams.Include(t => t.Vehicle).AsNoTracking().ToListAsync(ct);
        var tasks = await db.Tasks.Include(t => t.CurrentTeam).AsNoTracking().ToListAsync(ct);
        var roads = await db.RoadSegments.AsNoTracking().ToListAsync(ct);

        var entries = BuildEntries(teams, tasks, roads);
        var digest = ComputeWorldDigest(entries);

        var existing = await db.WorldSnapshots.Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.WorldDigest == digest, ct);
        if (existing is not null)
            return existing;

        var snapshot = new WorldSnapshot
        {
            Id = Guid.NewGuid(),
            WorldDigest = digest,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Entries = entries.Select(e => new WorldSnapshotEntry
            {
                Id = Guid.NewGuid(),
                EntityType = e.EntityType,
                EntityId = e.EntityId,
                Code = e.Code,
                Digest = e.Digest,
                ContentJson = e.ContentJson
            }).ToList()
        };
        foreach (var e in snapshot.Entries) e.WorldSnapshotId = snapshot.Id;

        // 关系库由 identity 列生成版本号；非关系提供程序（测试）显式递增
        if (!db.Database.IsRelational())
            snapshot.SnapshotVersion = (await db.WorldSnapshots.Select(s => (long?)s.SnapshotVersion).MaxAsync(ct) ?? 0) + 1;

        db.WorldSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    public Task<WorldSnapshot?> LoadAsync(Guid id, CancellationToken ct) =>
        db.WorldSnapshots.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>计算当前世界状态的内容条目（不落库），用于过期快照校验。</summary>
    public async Task<List<SnapshotEntryInfo>> BuildCurrentEntriesAsync(CancellationToken ct)
    {
        var teams = await db.Teams.Include(t => t.Vehicle).AsNoTracking().ToListAsync(ct);
        var tasks = await db.Tasks.Include(t => t.CurrentTeam).AsNoTracking().ToListAsync(ct);
        var roads = await db.RoadSegments.AsNoTracking().ToListAsync(ct);
        return BuildEntries(teams, tasks, roads);
    }

    public static string ComputeWorldDigest(IEnumerable<SnapshotEntryInfo> entries) =>
        Sha256(string.Join("|", entries
            .OrderBy(e => e.EntityType, StringComparer.Ordinal)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .Select(e => $"{e.EntityType}:{e.Code}:{e.Digest}")));

    /// <summary>由实体构建规范化内容条目（键序固定、集合排序，保证摘要稳定）。</summary>
    public static List<SnapshotEntryInfo> BuildEntries(List<Team> teams, List<DispatchTask> tasks, List<RoadSegment> roads)
    {
        var entries = new List<SnapshotEntryInfo>();

        foreach (var t in teams.OrderBy(t => t.Code, StringComparer.Ordinal))
            entries.Add(Make("team", t.Id, t.Code, new SortedDictionary<string, object?>
            {
                ["capabilities"] = t.Capabilities.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                ["code"] = t.Code,
                ["name"] = t.Name
            }));

        foreach (var t in teams.Where(t => t.Vehicle is not null).OrderBy(t => t.Code, StringComparer.Ordinal))
            entries.Add(Make("vehicle", t.Vehicle!.Id, t.Code, new SortedDictionary<string, object?>
            {
                ["heightMeters"] = t.Vehicle!.HeightMeters,
                ["name"] = t.Vehicle!.Name,
                ["teamCode"] = t.Code
            }));

        foreach (var t in tasks.OrderBy(t => t.Code, StringComparer.Ordinal))
            entries.Add(Make("task", t.Id, t.Code, new SortedDictionary<string, object?>
            {
                ["code"] = t.Code,
                ["currentTeamCode"] = t.CurrentTeam?.Code,
                ["danger"] = t.Danger.ToString(),
                ["deadlineMinutes"] = t.DeadlineMinutes,
                ["durationMinutes"] = t.DurationMinutes,
                ["kind"] = t.Kind.ToString(),
                ["requiredCapabilities"] = t.RequiredCapabilities.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                ["status"] = t.Status.ToString(),
                ["title"] = t.Title
            }));

        foreach (var r in roads.OrderBy(r => r.Code, StringComparer.Ordinal))
            entries.Add(Make("road", r.Id, r.Code, new SortedDictionary<string, object?>
            {
                ["code"] = r.Code,
                ["isBlocked"] = r.IsBlocked,
                ["lastEventId"] = r.LastEventId,
                ["maxVehicleHeightMeters"] = r.MaxVehicleHeightMeters,
                ["name"] = r.Name,
                ["travelMinutes"] = r.TravelMinutes
            }));

        return entries;
    }

    public static SnapshotEntryInfo ToInfo(WorldSnapshotEntry e) =>
        new(e.EntityType, e.EntityId, e.Code, e.Digest, e.ContentJson);

    /// <summary>字段级差异：from → to。道路通断变化归因到 to 侧快照中的 lastEventId。</summary>
    public static IReadOnlyList<FieldDiff> Diff(IEnumerable<SnapshotEntryInfo> from, IEnumerable<SnapshotEntryInfo> to)
    {
        var fromByKey = from.ToDictionary(e => (e.EntityType, e.Code));
        var toByKey = to.ToDictionary(e => (e.EntityType, e.Code));
        var diffs = new List<FieldDiff>();

        foreach (var key in fromByKey.Keys.Union(toByKey.Keys)
                     .OrderBy(k => k.EntityType, StringComparer.Ordinal)
                     .ThenBy(k => k.Code, StringComparer.Ordinal))
        {
            var hasFrom = fromByKey.TryGetValue(key, out var f);
            var hasTo = toByKey.TryGetValue(key, out var t);

            if (!hasFrom)
            {
                diffs.Add(new FieldDiff(key.EntityType, key.Code, "(entity)", null, "存在", null));
                continue;
            }
            if (!hasTo)
            {
                diffs.Add(new FieldDiff(key.EntityType, key.Code, "(entity)", "存在", null, null));
                continue;
            }
            if (f!.Digest == t!.Digest)
                continue;

            var fFields = ParseFields(f.ContentJson);
            var tFields = ParseFields(t.ContentJson);
            string? roadEventId = key.EntityType == "road" && tFields.TryGetValue("lastEventId", out var ev) ? ev : null;

            foreach (var field in fFields.Keys.Union(tFields.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                fFields.TryGetValue(field, out var fv);
                tFields.TryGetValue(field, out var tv);
                if (fv == tv) continue;
                diffs.Add(new FieldDiff(key.EntityType, key.Code, field, fv, tv,
                    field is "isBlocked" or "lastEventId" ? roadEventId : null));
            }
        }
        return diffs;
    }

    private static SnapshotEntryInfo Make(string type, Guid id, string code, SortedDictionary<string, object?> content)
    {
        var json = JsonSerializer.Serialize(content);
        return new SnapshotEntryInfo(type, id, code, Sha256(json), json);
    }

    private static Dictionary<string, string?> ParseFields(string contentJson)
    {
        using var doc = JsonDocument.Parse(contentJson);
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            map[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => prop.Value.GetRawText()
            };
        return map;
    }

    private static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
