using CodeForge.Infrastructure.Data;
using CodeForge.Worker;
using CodeForge.Worker.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var connectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<CodeForgeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<RepositoryProcessor>();
builder.Services.AddSingleton<ServiceBusConsumer>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<ServiceBusConsumer>());

await builder.Build().RunAsync();
