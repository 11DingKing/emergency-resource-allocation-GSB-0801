namespace EmergencyDispatch.Infrastructure;

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Persists a capability <see cref="HashSet{String}"/> as a stable, ordered,
/// semicolon-separated string. Ordering makes the stored form deterministic; the
/// accompanying <see cref="Comparer"/> lets EF track set-value changes correctly.
/// </summary>
public sealed class CapabilitySetConverter : ValueConverter<HashSet<string>, string>
{
    public CapabilitySetConverter()
        : base(
            set => Serialize(set),
            value => Deserialize(value))
    {
    }

    public static readonly ValueComparer<HashSet<string>> Comparer = new(
        (a, b) => Serialize(a) == Serialize(b),
        set => Serialize(set).GetHashCode(),
        set => new HashSet<string>(set, StringComparer.Ordinal));

    private static string Serialize(HashSet<string>? set) =>
        set is null || set.Count == 0
            ? string.Empty
            : string.Join(';', set.OrderBy(s => s, StringComparer.Ordinal));

    private static HashSet<string> Deserialize(string? value) =>
        string.IsNullOrEmpty(value)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                value.Split(';', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
}
