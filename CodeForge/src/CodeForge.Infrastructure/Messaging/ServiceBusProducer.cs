using Azure.Messaging.ServiceBus;

namespace CodeForge.Infrastructure.Messaging;

public sealed class ServiceBusProducer : IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ServiceBusProducer(string connectionString, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
    }

    public Task SendMessageAsync(
        Guid jobId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var jobIdText = jobId.ToString();

        var message = new ServiceBusMessage(jobIdText)
        {
            MessageId = messageId.ToString(),
            ContentType = "application/json",
            SessionId = jobIdText
        };

        return _sender.SendMessageAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
