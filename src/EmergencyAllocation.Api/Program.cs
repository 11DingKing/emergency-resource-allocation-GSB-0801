using System.Text.Json.Serialization;
using EmergencyAllocation.Infrastructure;
using EmergencyAllocation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? "Host=localhost;Port=5432;Database=emergency_alloc;Username=postgres;Password=postgres";
var useInMemory = builder.Configuration.GetValue<bool>("UseInMemory");
var inMemoryDbName = builder.Configuration.GetValue<string>("InMemoryDbName");

builder.Services.AddAllocationInfrastructure(connectionString, useInMemory: useInMemory,
    seedDemoData: true, inMemoryDatabaseName: inMemoryDbName);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AllocationDbContext>();
    if (useInMemory)
        await db.Database.EnsureCreatedAsync();
    else
        await db.Database.MigrateAsync();
}

app.Run();

public partial class Program { }
