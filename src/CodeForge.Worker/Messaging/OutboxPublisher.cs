using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Worker.Messaging;

public sealed class OutboxPublisher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceBusProducer _producer;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceScopeFactory scopeFactory,
        ServiceBusProducer producer,
        ILogger<OutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _producer = producer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Outbox polling failed");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CodeForgeDbContext>();
        var now = DateTime.UtcNow;

        var messageIds = await context.OutboxMessage
            .AsNoTracking()
            .Where(message =>
                message.PublishedAt == null &&
                (message.NextPublishAttemptAt == null ||
                 message.NextPublishAttemptAt <= now))
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(message => message.Id)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var messageId in messageIds)
        {
            await PublishMessageAsync(context, messageId, cancellationToken);
        }
    }

    private async Task PublishMessageAsync(
        CodeForgeDbContext context,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var claimExpiresAt = now.Add(ClaimDuration);

        // Claim the row atomically so another worker instance does not publish it
        // at the same time. An expired claim becomes eligible for a later retry.
        var claimedRows = await context.OutboxMessage
            .Where(message =>
                message.Id == messageId &&
                message.PublishedAt == null &&
                (message.NextPublishAttemptAt == null ||
                 message.NextPublishAttemptAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    message => message.PublishAttemptCount,
                    message => message.PublishAttemptCount + 1)
                .SetProperty(
                    message => message.NextPublishAttemptAt,
                    claimExpiresAt),
                cancellationToken);

        if (claimedRows == 0)
        {
            return;
        }

        var outboxMessage = await context.OutboxMessage.SingleAsync(
            message => message.Id == messageId,
            cancellationToken);

        try
        {
            await _producer.SendMessageAsync(
                outboxMessage.JobId,
                outboxMessage.Id,
                cancellationToken);

            outboxMessage.PublishedAt = DateTime.UtcNow;
            outboxMessage.LastPublishError = null;
            outboxMessage.NextPublishAttemptAt = null;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Published outbox message {MessageId} for job {JobId}",
                outboxMessage.Id,
                outboxMessage.JobId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            outboxMessage.LastPublishError = exception.Message;
            outboxMessage.NextPublishAttemptAt = DateTime.UtcNow.Add(
                GetRetryDelay(outboxMessage.PublishAttemptCount));
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                exception,
                "Failed to publish outbox message {MessageId} for job {JobId}; attempt {AttemptCount}",
                outboxMessage.Id,
                outboxMessage.JobId,
                outboxMessage.PublishAttemptCount);
        }
    }

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 6);
        var delaySeconds = Math.Min(300, 5 * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(delaySeconds);
    }

}
