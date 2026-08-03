using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasMaxLength(64);
        builder.Property(t => t.Name).HasMaxLength(128).IsRequired();
        builder.Property(t => t.VehicleId).HasMaxLength(64).IsRequired();
        builder.Property(t => t.HomeNode).HasMaxLength(64).IsRequired();
        builder.Property(t => t.CurrentNode).HasMaxLength(64).IsRequired();
        builder.Property(t => t.Capabilities)
            .HasConversion(JsonConversion.StringListConverter, JsonConversion.StringListComparer);

        builder.HasOne(t => t.Vehicle)
            .WithMany()
            .HasForeignKey(t => t.VehicleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
