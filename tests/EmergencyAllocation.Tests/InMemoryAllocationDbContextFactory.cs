using EmergencyAllocation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EmergencyAllocation.Tests;

public class InMemoryAllocationDbContextFactory : IDbContextFactory<AllocationDbContext>
{
    private readonly string _databaseName;
    private int _saveFailuresRemaining;
    private bool _alwaysFail;
    private bool _armed;

    public InMemoryAllocationDbContextFactory(string databaseName)
    {
        _databaseName = databaseName;
    }

    public void ArmTransientFailures(int times)
    {
        _saveFailuresRemaining = times;
        _alwaysFail = false;
        _armed = true;
    }

    public void ArmPermanentFailure()
    {
        _alwaysFail = true;
        _armed = true;
    }

    public AllocationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AllocationDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new ControllableAllocationDbContext(options, ShouldFail);
    }

    private bool ShouldFail()
    {
        if (!_armed)
        {
            return false;
        }

        if (_alwaysFail)
        {
            return true;
        }

        if (_saveFailuresRemaining > 0)
        {
            _saveFailuresRemaining--;
            return true;
        }

        return false;
    }

    private sealed class ControllableAllocationDbContext : AllocationDbContext
    {
        private readonly Func<bool> _shouldFail;

        public ControllableAllocationDbContext(DbContextOptions<AllocationDbContext> options, Func<bool> shouldFail)
            : base(options)
        {
            _shouldFail = shouldFail;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_shouldFail())
            {
                throw new TimeoutException("Simulated transient commit failure.");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
