using EmergencyAllocation.Domain;
using Microsoft.EntityFrameworkCore;

namespace EmergencyAllocation.Infrastructure.Persistence;

public class AllocationDbContext : DbContext
{
    public AllocationDbContext(DbContextOptions<AllocationDbContext> options) : base(options)
    {
    }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamCapability> TeamCapabilities => Set<TeamCapability>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<EmergencyTask> Tasks => Set<EmergencyTask>();
    public DbSet<TaskCapabilityRequirement> TaskCapabilityRequirements => Set<TaskCapabilityRequirement>();
    public DbSet<RoadSegment> RoadSegments => Set<RoadSegment>();
    public DbSet<RoadEvent> RoadEvents => Set<RoadEvent>();
    public DbSet<AllocationVersion> AllocationVersions => Set<AllocationVersion>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AllocationAuditEntry> AllocationAuditEntries => Set<AllocationAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Team>(e =>
        {
            e.ToTable("teams");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(32);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.BaseNodeId).HasColumnName("base_node_id").HasMaxLength(64);
            e.Property(x => x.IsAvailable).HasColumnName("is_available");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Capabilities).WithOne(x => x.Team!).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Vehicles).WithOne(x => x.Team!).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TeamCapability>(e =>
        {
            e.ToTable("team_capabilities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.Capability).HasColumnName("capability").HasMaxLength(64);
            e.HasIndex(x => new { x.TeamId, x.Capability }).IsUnique();
        });

        modelBuilder.Entity<Vehicle>(e =>
        {
            e.ToTable("vehicles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.HeightMeters).HasColumnName("height_meters");
            e.Property(x => x.AverageSpeedMetersPerMinute).HasColumnName("avg_speed_m_per_min");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.IsAvailable).HasColumnName("is_available");
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<EmergencyTask>(e =>
        {
            e.ToTable("emergency_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(256);
            e.Property(x => x.LocationNodeId).HasColumnName("location_node_id").HasMaxLength(64);
            e.Property(x => x.Severity).HasColumnName("severity");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
            e.Property(x => x.DeadlineMinutes).HasColumnName("deadline_minutes");
            e.Property(x => x.SeverityVersion).HasColumnName("severity_version");
            e.Property(x => x.AssignedTeamId).HasColumnName("assigned_team_id");
            e.Property(x => x.AssignedVehicleId).HasColumnName("assigned_vehicle_id");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.AssignedTeam).WithMany().HasForeignKey(x => x.AssignedTeamId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AssignedVehicle).WithMany().HasForeignKey(x => x.AssignedVehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.RequiredCapabilities).WithOne(x => x.Task!).HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskCapabilityRequirement>(e =>
        {
            e.ToTable("task_capability_requirements");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.Capability).HasColumnName("capability").HasMaxLength(64);
            e.HasIndex(x => new { x.TaskId, x.Capability }).IsUnique();
        });

        modelBuilder.Entity<RoadSegment>(e =>
        {
            e.ToTable("road_segments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
            e.Property(x => x.FromNodeId).HasColumnName("from_node_id").HasMaxLength(64);
            e.Property(x => x.ToNodeId).HasColumnName("to_node_id").HasMaxLength(64);
            e.Property(x => x.TravelTimeMinutes).HasColumnName("travel_time_minutes");
            e.Property(x => x.HeightLimitMeters).HasColumnName("height_limit_meters");
            e.Property(x => x.IsOpen).HasColumnName("is_open");
            e.Property(x => x.RoadSnapshotVersion).HasColumnName("snapshot_version");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.InterruptionReason).HasColumnName("interruption_reason").HasMaxLength(256);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.RoadSnapshotVersion);
        });

        modelBuilder.Entity<AllocationVersion>(e =>
        {
            e.ToTable("allocation_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.InputVersion).HasColumnName("input_version").HasMaxLength(64);
            e.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CommittedAt).HasColumnName("committed_at");
            e.Property(x => x.RoadSnapshotVersion).HasColumnName("road_snapshot_version");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(1024);
            e.Property(x => x.RoadDigest).HasColumnName("road_digest").HasMaxLength(64);
            e.Property(x => x.TaskDigest).HasColumnName("task_digest").HasMaxLength(64);
            e.Property(x => x.TeamDigest).HasColumnName("team_digest").HasMaxLength(64);
            e.Property(x => x.VehicleDigest).HasColumnName("vehicle_digest").HasMaxLength(64);
            e.Property(x => x.RequestPayloadDigest).HasColumnName("request_payload_digest").HasMaxLength(128);
            e.Property(x => x.TriggeringRoadEventId).HasColumnName("triggering_road_event_id").HasMaxLength(64);
            e.Property(x => x.TotalCostMinutes).HasColumnName("total_cost_minutes");
            e.Property(x => x.AssignedCount).HasColumnName("assigned_count");
            e.Property(x => x.UnassignedCount).HasColumnName("unassigned_count");
            e.HasIndex(x => new { x.Operation, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Sequence);
            e.HasMany(x => x.Assignments).WithOne(x => x.AllocationVersion!).HasForeignKey(x => x.AllocationVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.AuditEntries).WithOne(x => x.AllocationVersion!).HasForeignKey(x => x.AllocationVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Assignment>(e =>
        {
            e.ToTable("assignments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AllocationVersionId).HasColumnName("allocation_version_id");
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.VehicleId).HasColumnName("vehicle_id");
            e.Property(x => x.Decision).HasColumnName("decision");
            e.Property(x => x.EstimatedTravelMinutes).HasColumnName("estimated_travel_minutes");
            e.Property(x => x.EstimatedTotalMinutes).HasColumnName("estimated_total_minutes");
            e.Property(x => x.MeetsDeadline).HasColumnName("meets_deadline");
            e.Property(x => x.RouteNodeIds).HasColumnName("route_node_ids").HasMaxLength(1024);
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(2048);
            e.Property(x => x.Preempted).HasColumnName("preempted");
            e.Property(x => x.PreviousTeamId).HasColumnName("previous_team_id");
            e.Property(x => x.PreviousVehicleId).HasColumnName("previous_vehicle_id");
            e.HasIndex(x => x.AllocationVersionId);
        });

        modelBuilder.Entity<AllocationAuditEntry>(e =>
        {
            e.ToTable("allocation_audit_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AllocationVersionId).HasColumnName("allocation_version_id");
            e.Property(x => x.Order).HasColumnName("entry_order");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(x => x.TaskCode).HasColumnName("task_code").HasMaxLength(32);
            e.Property(x => x.TeamCode).HasColumnName("team_code").HasMaxLength(32);
            e.Property(x => x.VehicleCode).HasColumnName("vehicle_code").HasMaxLength(32);
            e.Property(x => x.RoadCode).HasColumnName("road_code").HasMaxLength(32);
            e.Property(x => x.RoadEventId).HasColumnName("road_event_id").HasMaxLength(64);
            e.Property(x => x.Message).HasColumnName("message").HasMaxLength(2048);
            e.HasIndex(x => x.AllocationVersionId);
        });

        modelBuilder.Entity<RoadEvent>(e =>
        {
            e.ToTable("road_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EventId).HasColumnName("event_id").HasMaxLength(64);
            e.Property(x => x.RoadCode).HasColumnName("road_code").HasMaxLength(32);
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(256);
            e.Property(x => x.RoadSnapshotVersionBefore).HasColumnName("snapshot_version_before");
            e.Property(x => x.RoadSnapshotVersionAfter).HasColumnName("snapshot_version_after");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.RecordedBy).HasColumnName("recorded_by").HasMaxLength(128);
            e.HasIndex(x => x.EventId).IsUnique();
        });
    }
}
