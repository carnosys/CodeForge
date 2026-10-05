using CodeForge.Infrastructure.Data;
using CodeForge.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

builder.Services.AddDbContext<CodeForgeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<RepositoryProcessor>();
builder.Services.AddHostedService<JobWorker>();

await builder.Build().RunAsync();
