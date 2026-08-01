using EmergencyAllocation.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmergencyAllocation.Infrastructure.Persistence;

public class AllocationDbContext : DbContext
{
    public AllocationDbContext(DbContextOptions<AllocationDbContext> options) : base(options)
    {
    }

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<EmergencyTask> Tasks => Set<EmergencyTask>();
    public DbSet<RoadSegment> RoadSegments => Set<RoadSegment>();
    public DbSet<AllocationVersion> AllocationVersions => Set<AllocationVersion>();
    public DbSet<TaskAssignment> TaskAssignments => Set<TaskAssignment>();
    public DbSet<AuditExplanation> AuditExplanations => Set<AuditExplanation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AllocationDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
