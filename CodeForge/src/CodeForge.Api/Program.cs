using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<CodeForgeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddControllers();
builder.Services.AddSingleton(_ => new ServiceBusProducer(
    builder.Configuration["QUEUE_CONNECTION_STRING_SEND"]
        ?? throw new InvalidOperationException("QUEUE_CONNECTION_STRING_SEND is missing."),
    builder.Configuration["QUEUE_NAME"]
        ?? throw new InvalidOperationException("QUEUE_NAME is missing.")));

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<CodeForgeDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();

await app.RunAsync();
