using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmergencyDispatch.Infrastructure.Persistence;

/// <summary>dotnet ef 迁移用设计时工厂。连接串可用环境变量 ERA_CONNECTION 覆盖。</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DispatchDbContext>
{
    public DispatchDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("ERA_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=emergency_dispatch;Username=huangding";
        var options = new DbContextOptionsBuilder<DispatchDbContext>().UseNpgsql(conn).Options;
        return new DispatchDbContext(options);
    }
}
