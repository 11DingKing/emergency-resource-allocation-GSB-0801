using EmergencyDispatch.Domain.Solving;
using EmergencyDispatch.Infrastructure.Persistence;
using EmergencyDispatch.Infrastructure.Seeding;
using EmergencyDispatch.Infrastructure.Services;
using EmergencyDispatch.Solver;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<DispatchDbContext>(options =>
{
    if (builder.Environment.IsEnvironment("Testing"))
        options.UseInMemoryDatabase("dispatch-tests");
    else
        options.UseNpgsql(builder.Configuration.GetConnectionString("Dispatch"));
});

// 求解器通过接口注入，与 API、数据库隔离；测试可替换为任意确定性实现
builder.Services.AddSingleton<IAllocationSolver, DeterministicAllocationSolver>();
builder.Services.AddScoped<AllocationOrchestrator>();
builder.Services.AddScoped<WorldSnapshotService>();

var app = builder.Build();

app.MapOpenApi();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
    await DbSeeder.SeedIfEmptyAsync(db);
}

app.Run();

// 供 WebApplicationFactory 集成测试使用
public partial class Program;
