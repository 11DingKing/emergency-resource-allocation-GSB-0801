using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class AllocationVersionConfiguration : IEntityTypeConfiguration<AllocationVersion>
{
    public void Configure(EntityTypeBuilder<AllocationVersion> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.InputVersion).HasMaxLength(128).IsRequired();
        builder.HasIndex(a => a.InputVersion).IsUnique();
        builder.Property(a => a.SnapshotHash).HasMaxLength(128);
        builder.Property(a => a.RoadSnapshotHash).HasMaxLength(128);
        builder.Property(a => a.TaskSnapshotHash).HasMaxLength(128);
        builder.Property(a => a.TeamSnapshotHash).HasMaxLength(128);
        builder.Property(a => a.VehicleSnapshotHash).HasMaxLength(128);
        builder.Property(a => a.TriggeringRoadEventId).HasMaxLength(64);
        builder.Property(a => a.SolverVersion).HasMaxLength(64);
        builder.Property(a => a.NoFeasibleReason).HasMaxLength(1024);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);

        builder.HasMany(a => a.Assignments)
            .WithOne(x => x.AllocationVersion!)
            .HasForeignKey(x => x.AllocationVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.Explanations)
            .WithOne(x => x.AllocationVersion!)
            .HasForeignKey(x => x.AllocationVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
