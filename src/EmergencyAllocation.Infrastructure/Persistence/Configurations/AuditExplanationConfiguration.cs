using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmergencyAllocation.Infrastructure.Persistence.Configurations;

public class AuditExplanationConfiguration : IEntityTypeConfiguration<AuditExplanation>
{
    public void Configure(EntityTypeBuilder<AuditExplanation> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.RuleCode).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(2048).IsRequired();
        builder.Property(a => a.RelatedTaskId).HasMaxLength(64);
        builder.Property(a => a.RelatedTeamId).HasMaxLength(64);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(a => a.AllocationVersionId);
    }
}
