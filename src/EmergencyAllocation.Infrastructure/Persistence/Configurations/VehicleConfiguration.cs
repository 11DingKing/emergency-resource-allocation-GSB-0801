using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasMaxLength(64);
        builder.Property(v => v.Name).HasMaxLength(128).IsRequired();
        builder.Property(v => v.HeightMeters).HasPrecision(4, 1);
    }
}
