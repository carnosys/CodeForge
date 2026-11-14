using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Messaging;
using CodeForge.Infrastructure.Reports;
using CodeForge.Worker;
using CodeForge.Worker.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var connectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<CodeForgeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton(_ => new BlobReportStore(
    new Uri(builder.Configuration["BLOB_SERVICE_URI"]
        ?? throw new InvalidOperationException("BLOB_SERVICE_URI is missing.")),
    builder.Configuration["BLOB_CONTAINER_NAME"] ?? "codeforge-reports"));
builder.Services.AddSingleton<IAnalysisArtifactStore>(services =>
    services.GetRequiredService<BlobReportStore>());
builder.Services.AddSingleton<StaticAnalysisRunner>();
builder.Services.AddSingleton<BuildDiagnosticsRunner>();
builder.Services.AddSingleton<DotNetArchitectureRunner>();
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
