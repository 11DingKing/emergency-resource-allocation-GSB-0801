using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class RoadSegmentConfiguration : IEntityTypeConfiguration<RoadSegment>
{
    public void Configure(EntityTypeBuilder<RoadSegment> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasMaxLength(64);
        builder.Property(r => r.Name).HasMaxLength(128).IsRequired();
        builder.Property(r => r.FromNode).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ToNode).HasMaxLength(64).IsRequired();
        builder.Property(r => r.HeightLimitMeters).HasPrecision(4, 1);
        builder.HasIndex(r => new { r.FromNode, r.ToNode });
    }
}
