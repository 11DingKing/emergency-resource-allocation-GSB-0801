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
