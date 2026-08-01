namespace EmergencyDispatch.Infrastructure;

using EmergencyDispatch.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core context for the dispatch domain. Capability sets are stored as ordered,
/// semicolon-joined stable strings via value converters so both PostgreSQL and the SQLite
/// test provider observe identical semantics. The unique index on
/// <see cref="AllocationVersion.InputVersion"/> is the database-level guarantee behind
/// idempotency and the concurrent-submit contract.
/// </summary>
public class DispatchDbContext : DbContext
{
    public DispatchDbContext(DbContextOptions<DispatchDbContext> options) : base(options)
    {
    }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<MissionTask> Tasks => Set<MissionTask>();
    public DbSet<RoadSegment> RoadSegments => Set<RoadSegment>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<AllocationVersion> AllocationVersions => Set<AllocationVersion>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<UnassignedReason> UnassignedReasons => Set<UnassignedReason>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AllocationBaseline> AllocationBaselines => Set<AllocationBaseline>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var caps = new CapabilitySetConverter();
        var capsComparer = CapabilitySetConverter.Comparer;

        modelBuilder.Entity<Team>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Capabilities)
                .HasConversion(caps)
                .Metadata.SetValueComparer(capsComparer);
        });

        modelBuilder.Entity<Vehicle>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.HeightMeters).HasColumnType("numeric(4,2)");
        });

        modelBuilder.Entity<MissionTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.RequiredCapabilities)
                .HasConversion(caps)
                .Metadata.SetValueComparer(capsComparer);
            e.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<RoadSegment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.HeightLimitMeters).HasColumnType("numeric(4,2)");
        });

        modelBuilder.Entity<Route>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TaskId, x.RoadSegmentId }).IsUnique();
        });

        modelBuilder.Entity<AllocationVersion>(e =>
        {
            e.HasKey(x => x.Id);
            // The idempotency and concurrency contract: at most one row per input version.
            e.HasIndex(x => x.InputVersion).IsUnique();
            e.HasIndex(x => x.VersionNumber).IsUnique();
            e.Property(x => x.InputVersion).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasMany(x => x.Assignments).WithOne().HasForeignKey(a => a.AllocationVersionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.UnassignedReasons).WithOne().HasForeignKey(a => a.AllocationVersionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.AuditEntries).WithOne().HasForeignKey(a => a.AllocationVersionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Baselines).WithOne().HasForeignKey(a => a.AllocationVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Assignment>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<UnassignedReason>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<AuditEntry>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<AllocationBaseline>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AllocationVersionId, x.TaskId }).IsUnique();
        });
    }
}
