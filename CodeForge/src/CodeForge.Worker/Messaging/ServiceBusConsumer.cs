using Azure.Messaging.ServiceBus;
using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Worker.Messaging;

public sealed class ServiceBusConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSessionProcessor _processor;
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
            configuration["QUEUE_CONNECTION_STRING_LISTEN"]
            ?? throw new InvalidOperationException("QUEUE_CONNECTION_STRING_LISTEN is missing.");

        var queueName =
            configuration["QUEUE_NAME"]
            ?? throw new InvalidOperationException("QUEUE_NAME is missing.");

        _scopeFactory = scopeFactory;
        _repositoryProcessor = repositoryProcessor;
        _logger = logger;

        _client = new ServiceBusClient(connectionString);
        _processor = _client.CreateSessionProcessor(queueName, new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentSessions = 1,
            MaxConcurrentCallsPerSession = 1,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(20)
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

    private async Task ProcessMessageAsync(ProcessSessionMessageEventArgs args)
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

        if (job.Status is JobStatus.Completed)
        {
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        if (job.Status is JobStatus.Failed)
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "Job already permanently failed",
                $"Job {job.Id} is marked as Failed.",
                args.CancellationToken);
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            job.StartedAt ??= DateTime.UtcNow;
            job.FailureReason = null;
            job.ProcessingStage = ProcessingStage.Queued;
            job.SetTestSummary(JobTestSummary.NotRun());
            job.ClearStaticAnalysis();
            await context.SaveChangesAsync(args.CancellationToken);

            using var processingTimeOut = CancellationTokenSource.CreateLinkedTokenSource(args.CancellationToken);
            processingTimeOut.CancelAfter(TimeSpan.FromMinutes(15));

            var result = await _repositoryProcessor.ProcessRepoAsync(
                job.Id,
                job.RepoUrl,
                job.RunTests,
                async (analysis, cancellationToken) =>
                {
                    job.SetTestSummary(analysis.Tests);
                    job.SetCoverageAnalysis(analysis.Coverage);
                    job.SetDotNetArchitecture(analysis.Architecture);
                    job.SetBuildFindings(analysis.BuildFindings);
                    job.SetComplexityAnalysis(analysis.Complexity);
                    job.SetDependencyAnalysis(analysis.Dependencies);
                    job.RepositoryCommitSha = analysis.RepositoryCommitSha;
                    job.WorkerVersion = analysis.WorkerVersion;
                    await context.SaveChangesAsync(cancellationToken);
                },
                async (stage, cancellationToken) =>
                {
                    job.ProcessingStage = stage;
                    await context.SaveChangesAsync(cancellationToken);
                },
                processingTimeOut.Token);

            await JobCompletion.FinalizeAsync(
                job,
                result,
                token => context.SaveChangesAsync(token),
                args.CancellationToken);


        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Job {JobId} failed on delivery {DeliveryCount}",
                job.Id,
                args.Message.DeliveryCount);

            const int maxDeliveryCount = 5;

            if (args.Message.DeliveryCount >= maxDeliveryCount)
            {
                job.Status = JobStatus.Failed;
                job.ProcessingStage = ProcessingStage.Failed;
                job.AnalysisOutcome = JobAnalysisOutcome.InfrastructureFailed;
                job.ReportReady = false;
                job.CompletedAt = DateTime.UtcNow;
                job.FailureReason = JobFailureReason.From(exception);

                await context.SaveChangesAsync(args.CancellationToken);

                await args.DeadLetterMessageAsync(
                    args.Message,
                    "Processing attempts exhausted",
                    exception.Message,
                    args.CancellationToken);

                return;
            }

            // More attempts remain.
            job.Status = JobStatus.Pending;
            job.ProcessingStage = ProcessingStage.Queued;
            job.ReportReady = false;
            job.CompletedAt = null;
            job.FailureReason = null;

            await context.SaveChangesAsync(args.CancellationToken);

            throw; // Processor abandons the message for another delivery.
        }

        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
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
