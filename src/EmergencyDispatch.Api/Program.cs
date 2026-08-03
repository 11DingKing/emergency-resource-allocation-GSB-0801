using EmergencyDispatch.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Dispatch")
    ?? "Host=localhost;Port=5432;Database=emergency_dispatch;Username=postgres;Password=postgres";

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Registers the allocation service and the default deterministic solver. The concrete
// solver is bound only here (and can be overridden in tests). When "Dispatch:UseExternalDb"
// is set (by the test host), the DbContext registration is skipped so the test can supply
// its own provider — keeping exactly one EF provider active.
builder.Services.AddDispatchServices();
if (!builder.Configuration.GetValue("Dispatch:UseExternalDb", false))
{
    builder.Services.AddDbContext<DispatchDbContext>(options =>
        options.UseNpgsql(connectionString));
}

var app = builder.Build();

// Apply migrations and seed the scenario baseline on startup unless disabled (tests set this).
if (app.Configuration.GetValue("Dispatch:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>Exposed so the test host (WebApplicationFactory) can bootstrap the API.</summary>
public partial class Program { }
