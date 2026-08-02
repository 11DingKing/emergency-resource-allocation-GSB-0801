using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class RoadEventConfiguration : IEntityTypeConfiguration<RoadEvent>
{
    public void Configure(EntityTypeBuilder<RoadEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(64);
        builder.Property(e => e.RoadSegmentId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(512).IsRequired();
        builder.Property(e => e.RecordedBy).HasMaxLength(128);
        builder.HasIndex(e => new { e.RoadSegmentId, e.OccurredAt });

        builder.HasOne(e => e.RoadSegment)
            .WithMany()
            .HasForeignKey(e => e.RoadSegmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
