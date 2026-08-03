namespace EmergencyAllocation.Domain;

public static class Capabilities
{
    public const string WaterRescue = "water-rescue";
    public const string SlopeInspection = "slope-inspection";
    public const string FirstAid = "first-aid";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        WaterRescue,
        SlopeInspection,
        FirstAid
    };
}
