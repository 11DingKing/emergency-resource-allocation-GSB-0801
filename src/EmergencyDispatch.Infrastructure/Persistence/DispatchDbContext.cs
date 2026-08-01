using EmergencyDispatch.Domain;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDispatch.Infrastructure.Persistence;

public class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<RoadSegment> RoadSegments => Set<RoadSegment>();
    public DbSet<DispatchTask> Tasks => Set<DispatchTask>();
    public DbSet<AllocationPlan> Plans => Set<AllocationPlan>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<UnassignedTask> UnassignedTasks => Set<UnassignedTask>();
    public DbSet<RoadEvent> RoadEvents => Set<RoadEvent>();
    public DbSet<WorldSnapshot> WorldSnapshots => Set<WorldSnapshot>();
    public DbSet<WorldSnapshotEntry> WorldSnapshotEntries => Set<WorldSnapshotEntry>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var relational = Database.IsRelational();

        mb.Entity<Team>(b =>
        {
            b.HasIndex(t => t.Code).IsUnique();
            if (relational) b.Property(t => t.Capabilities).HasColumnType("text[]");
            b.HasOne(t => t.Vehicle).WithOne(v => v.Team).HasForeignKey<Vehicle>(v => v.TeamId);
        });

        mb.Entity<Vehicle>(b =>
        {
            if (relational) b.Property(v => v.HeightMeters).HasColumnType("numeric(4,1)");
        });

        mb.Entity<RoadSegment>(b =>
        {
            b.HasIndex(r => r.Code).IsUnique();
            if (relational)
            {
                b.Property(r => r.MaxVehicleHeightMeters).HasColumnType("numeric(4,1)");
                b.Property<uint>("xmin").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            }
        });

        mb.Entity<DispatchTask>(b =>
        {
            b.HasIndex(t => t.Code).IsUnique();
            if (relational) b.Property(t => t.RequiredCapabilities).HasColumnType("text[]");
            b.Property(t => t.Kind).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.Danger).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(32);
            b.HasOne(t => t.CurrentTeam).WithMany().HasForeignKey(t => t.CurrentTeamId).OnDelete(DeleteBehavior.Restrict);
            if (relational) b.Property<uint>("xmin").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        mb.Entity<AllocationPlan>(b =>
        {
            // 输入版本参与幂等控制：唯一约束是并发提交的最终防线
            b.HasIndex(p => p.InputVersion).IsUnique();
            if (relational) b.Property(p => p.PlanVersion).UseIdentityAlwaysColumn();
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(32);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            b.HasMany(p => p.Assignments).WithOne().HasForeignKey(a => a.PlanId);
            b.HasMany(p => p.Unassigned).WithOne().HasForeignKey(u => u.PlanId);
            b.HasOne(p => p.WorldSnapshot).WithMany().HasForeignKey(p => p.WorldSnapshotId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<RoadEvent>(b =>
        {
            // 事件 Id 由调用方提供，唯一约束保证同一事件不会重复生效
            b.HasIndex(e => e.EventId).IsUnique();
            b.HasOne(e => e.Road).WithMany().HasForeignKey(e => e.RoadId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<WorldSnapshot>(b =>
        {
            // 内容寻址：相同世界状态只保留一份快照
            b.HasIndex(s => s.WorldDigest).IsUnique();
            if (relational) b.Property(s => s.SnapshotVersion).UseIdentityAlwaysColumn();
            b.HasMany(s => s.Entries).WithOne().HasForeignKey(e => e.WorldSnapshotId);
        });

        mb.Entity<WorldSnapshotEntry>(b =>
        {
            b.Property(e => e.EntityType).HasMaxLength(16);
            if (relational) b.Property(e => e.ContentJson).HasColumnType("jsonb");
            b.HasIndex(e => new { e.WorldSnapshotId, e.EntityType, e.Code }).IsUnique();
        });

        mb.Entity<Assignment>(b =>
        {
            b.HasOne(a => a.Task).WithMany().HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.Team).WithMany().HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.Road).WithMany().HasForeignKey(a => a.RoadId).OnDelete(DeleteBehavior.Restrict);
            if (relational) b.OwnsMany(a => a.Reasons, r => r.ToJson());
            else b.OwnsMany(a => a.Reasons);
        });

        mb.Entity<UnassignedTask>(b =>
        {
            b.HasOne(u => u.Task).WithMany().HasForeignKey(u => u.TaskId).OnDelete(DeleteBehavior.Restrict);
            if (relational) b.OwnsMany(u => u.Reasons, r => r.ToJson());
            else b.OwnsMany(u => u.Reasons);
        });
    }
}
