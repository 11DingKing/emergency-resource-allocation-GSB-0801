using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class EmergencyTaskConfiguration : IEntityTypeConfiguration<EmergencyTask>
{
    public void Configure(EntityTypeBuilder<EmergencyTask> builder)
    {
        builder.ToTable("tasks");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasMaxLength(64);
        builder.Property(t => t.Name).HasMaxLength(128).IsRequired();
        builder.Property(t => t.LocationNode).HasMaxLength(64).IsRequired();
        builder.Property(t => t.AssignedTeamId).HasMaxLength(64);
        builder.Property(t => t.RequiredCapabilities)
            .HasConversion(JsonConversion.StringListConverter, JsonConversion.StringListComparer);

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(t => t.AssignedTeamId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
