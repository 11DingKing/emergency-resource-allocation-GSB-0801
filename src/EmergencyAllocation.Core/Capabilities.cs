namespace EmergencyAllocation.Core;

public static class Capabilities
{
    public const string WaterRescue = "water_rescue";
    public const string SlopeInspection = "slope_inspection";
    public const string FirstAid = "first_aid";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        WaterRescue,
        SlopeInspection,
        FirstAid
    };
}
