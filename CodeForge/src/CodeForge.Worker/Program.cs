using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Messaging;
using CodeForge.Worker;
using CodeForge.Worker.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var connectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<CodeForgeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<RepositoryProcessor>();
builder.Services.AddSingleton(_ => new ServiceBusProducer(
    builder.Configuration["QUEUE_CONNECTION_STRING_SEND"]
        ?? throw new InvalidOperationException("QUEUE_CONNECTION_STRING_SEND is missing."),
    builder.Configuration["QUEUE_NAME"]
        ?? throw new InvalidOperationException("QUEUE_NAME is missing.")));
builder.Services.AddSingleton<ServiceBusConsumer>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<ServiceBusConsumer>());
builder.Services.AddHostedService<OutboxPublisher>();

await builder.Build().RunAsync();
