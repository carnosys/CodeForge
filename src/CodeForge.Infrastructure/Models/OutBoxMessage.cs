namespace CodeForge.Infrastructure.Models;

public sealed class OutboxMessage
{
    // Also used as the Service Bus MessageId.
    public Guid Id { get; set; } = Guid.NewGuid();

    // Identifies the job that the worker should process.
    public Guid JobId { get; set; }

    public required Job Job {get; set;}

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Null means publication has not been recorded as successful.
    public DateTime? PublishedAt { get; set; }

    // Tracks attempts to SEND the message to Service Bus.
    public int PublishAttemptCount { get; set; }

    public string? LastPublishError { get; set; }

    // Allows delayed retries after publishing fails.
    public DateTime? NextPublishAttemptAt { get; set; }
}