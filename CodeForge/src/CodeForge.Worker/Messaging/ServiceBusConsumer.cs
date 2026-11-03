using Azure.Messaging.ServiceBus;
using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Worker.Messaging;

public sealed class ServiceBusConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusProcessor _processor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RepositoryProcessor _repositoryProcessor;
    private readonly ILogger<ServiceBusConsumer> _logger;

    public ServiceBusConsumer(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        RepositoryProcessor repositoryProcessor,
        ILogger<ServiceBusConsumer> logger)
    {
        var connectionString =
            configuration["QUEUE_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("QUEUE_CONNECTION_STRING is missing.");

        var queueName =
            configuration["QUEUE_NAME"]
            ?? throw new InvalidOperationException("QUEUE_NAME is missing.");

        _scopeFactory = scopeFactory;
        _repositoryProcessor = repositoryProcessor;
        _logger = logger;

        _client = new ServiceBusClient(connectionString);
        _processor = _client.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10)
        });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _processor.StartProcessingAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _processor.StopProcessingAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        if (!Guid.TryParse(args.Message.Body.ToString(), out var jobId))
        {
            _logger.LogWarning(
                "Dead-lettering Service Bus message {MessageId}: body is not a valid job ID",
                args.Message.MessageId);

            await args.DeadLetterMessageAsync(
                args.Message,
                "Invalid job ID",
                "The message body must contain a valid job ID.",
                args.CancellationToken);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CodeForgeDbContext>();
        var job = await context.Job.SingleOrDefaultAsync(
            candidate => candidate.Id == jobId,
            args.CancellationToken);

        if (job is null)
        {
            _logger.LogWarning(
                "Dead-lettering Service Bus message {MessageId}: job {JobId} was not found",
                args.Message.MessageId,
                jobId);

            await args.DeadLetterMessageAsync(
                args.Message,
                "Job not found",
                $"No job exists with ID {jobId}.",
                args.CancellationToken);
            return;
        }

        if (job.Status is JobStatus.Completed or JobStatus.Failed)
        {
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            job.StartedAt ??= DateTime.UtcNow;
            await context.SaveChangesAsync(args.CancellationToken);

            await _repositoryProcessor.ProcessRepoAsync(
                job.RepoUrl,
                args.CancellationToken);

            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(args.CancellationToken);

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Job {JobId} failed", job.Id);

            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(args.CancellationToken);

            await args.DeadLetterMessageAsync(
                args.Message,
                "Job processing failed",
                exception.Message,
                args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Service Bus error from {ErrorSource} in namespace {Namespace}",
            args.ErrorSource,
            args.FullyQualifiedNamespace);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _processor.ProcessMessageAsync -= ProcessMessageAsync;
        _processor.ProcessErrorAsync -= ProcessErrorAsync;
        await _processor.DisposeAsync();
        await _client.DisposeAsync();
    }
}
