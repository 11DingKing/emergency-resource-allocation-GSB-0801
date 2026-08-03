using EmergencyAllocation.Domain;
using TaskStatus = EmergencyAllocation.Domain.TaskStatus;

namespace EmergencyAllocation.Infrastructure.Seeding;

public static class SeedData
{
    public static readonly Guid TeamAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TeamBId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid TeamCId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid VehAId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    public static readonly Guid VehBId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    public static readonly Guid VehCId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    public static readonly Guid TaskLifeId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    public static readonly Guid TaskSlopeId = Guid.Parse("00000000-0000-0000-0000-000000000102");

    public static readonly Guid RoadR1Id = Guid.Parse("00000000-0000-0000-0000-000000000201");
    public static readonly Guid RoadR2Id = Guid.Parse("00000000-0000-0000-0000-000000000202");
    public static readonly Guid RoadRBId = Guid.Parse("00000000-0000-0000-0000-000000000203");

    public static IReadOnlyList<Team> Teams() => new List<Team>
    {
        new()
        {
            Id = TeamAId, Code = "A", Name = "Alpha water/first-aid team",
            BaseNodeId = "BASE_A", IsAvailable = true,
            Capabilities =
            {
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamAId, Capability = Capabilities.WaterRescue },
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamAId, Capability = Capabilities.FirstAid }
            },
            Vehicles =
            {
                new Vehicle
                {
                    Id = VehAId, TeamId = TeamAId, Code = "VA", Name = "Alpha 4x4",
                    HeightMeters = 3.4, AverageSpeedMetersPerMinute = 500, IsAvailable = true
                }
            }
        },
        new()
        {
            Id = TeamBId, Code = "B", Name = "Bravo slope/first-aid team",
            BaseNodeId = "BASE_B", IsAvailable = true,
            Capabilities =
            {
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamBId, Capability = Capabilities.SlopeInspection },
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamBId, Capability = Capabilities.FirstAid }
            },
            Vehicles =
            {
                new Vehicle
                {
                    Id = VehBId, TeamId = TeamBId, Code = "VB", Name = "Bravo low van",
                    HeightMeters = 2.6, AverageSpeedMetersPerMinute = 500, IsAvailable = true
                }
            }
        },
        new()
        {
            Id = TeamCId, Code = "C", Name = "Charlie all-hazard team",
            BaseNodeId = "BASE_C", IsAvailable = true,
            Capabilities =
            {
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamCId, Capability = Capabilities.WaterRescue },
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamCId, Capability = Capabilities.SlopeInspection },
                new TeamCapability { Id = Guid.NewGuid(), TeamId = TeamCId, Capability = Capabilities.FirstAid }
            },
            Vehicles =
            {
                new Vehicle
                {
                    Id = VehCId, TeamId = TeamCId, Code = "VC", Name = "Charlie rescue truck",
                    HeightMeters = 3.0, AverageSpeedMetersPerMinute = 500, IsAvailable = true
                }
            }
        }
    };

    public static IReadOnlyList<EmergencyTask> Tasks() => new List<EmergencyTask>
    {
        new()
        {
            Id = TaskLifeId, Code = "T1", Title = "Trapped resident requiring water rescue + first aid",
            LocationNodeId = "SITE_LIFE",
            Severity = TaskSeverity.LifeSafety,
            Status = TaskStatus.Pending,
            DurationMinutes = 45,
            DeadlineMinutes = 35,
            SeverityVersion = 1,
            RequiredCapabilities =
            {
                new TaskCapabilityRequirement { Id = Guid.NewGuid(), TaskId = TaskLifeId, Capability = Capabilities.WaterRescue },
                new TaskCapabilityRequirement { Id = Guid.NewGuid(), TaskId = TaskLifeId, Capability = Capabilities.FirstAid }
            }
        },
        new()
        {
            Id = TaskSlopeId, Code = "T2", Title = "Slope inspection already assigned to B",
            LocationNodeId = "SITE_SLOPE",
            Severity = TaskSeverity.Urgent,
            Status = TaskStatus.InProgress,
            DurationMinutes = 60,
            DeadlineMinutes = null,
            SeverityVersion = 1,
            AssignedTeamId = TeamBId,
            AssignedVehicleId = VehBId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-12),
            RequiredCapabilities =
            {
                new TaskCapabilityRequirement { Id = Guid.NewGuid(), TaskId = TaskSlopeId, Capability = Capabilities.SlopeInspection },
                new TaskCapabilityRequirement { Id = Guid.NewGuid(), TaskId = TaskSlopeId, Capability = Capabilities.FirstAid }
            }
        }
    };

    public static IReadOnlyList<RoadSegment> Roads() => new List<RoadSegment>
    {
        new()
        {
            Id = RoadR1Id, Code = "R1", FromNodeId = "BASE_A", ToNodeId = "SITE_LIFE",
            TravelTimeMinutes = 18, HeightLimitMeters = 3.2, IsOpen = true,
            RoadSnapshotVersion = 1, UpdatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = RoadR2Id, Code = "R2", FromNodeId = "BASE_C", ToNodeId = "SITE_LIFE",
            TravelTimeMinutes = 22, HeightLimitMeters = 4.0, IsOpen = true,
            RoadSnapshotVersion = 1, UpdatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = RoadRBId, Code = "RB", FromNodeId = "BASE_B", ToNodeId = "SITE_SLOPE",
            TravelTimeMinutes = 10, HeightLimitMeters = null, IsOpen = true,
            RoadSnapshotVersion = 1, UpdatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = Guid.NewGuid(), Code = "RC", FromNodeId = "BASE_C", ToNodeId = "SITE_SLOPE",
            TravelTimeMinutes = 14, HeightLimitMeters = 4.0, IsOpen = true,
            RoadSnapshotVersion = 1, UpdatedAt = DateTimeOffset.UtcNow
        }
    };
}
