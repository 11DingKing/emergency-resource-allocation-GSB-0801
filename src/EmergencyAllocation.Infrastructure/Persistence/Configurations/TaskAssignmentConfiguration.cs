using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class TaskAssignmentConfiguration : IEntityTypeConfiguration<TaskAssignment>
{
    public void Configure(EntityTypeBuilder<TaskAssignment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.TaskId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.TeamId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.VehicleId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.PreemptionReason).HasMaxLength(1024);
        builder.Property(a => a.RouteNodes)
            .HasConversion(JsonConversion.StringListConverter, JsonConversion.StringListComparer);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(a => a.AllocationVersionId);
    }
}
