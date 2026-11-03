using Azure.Messaging.ServiceBus;

namespace CodeForge.Api.Messaging;

public sealed class ServiceBusProducer : IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ServiceBusProducer(IConfiguration configuration)
    {
        var connectionString =
            configuration["QUEUE_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("QUEUE_CONNECTION_STRING is missing.");

        var queueName =
            configuration["QUEUE_NAME"]
            ?? throw new InvalidOperationException("QUEUE_NAME is missing.");

        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
    }

    public Task SendMessageAsync(
        string messageBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageBody);

        var message = new ServiceBusMessage(messageBody)
        {
            ContentType = "application/json"
        };

        return _sender.SendMessageAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
