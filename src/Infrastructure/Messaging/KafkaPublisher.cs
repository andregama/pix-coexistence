using Confluent.Kafka;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.DTOs;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ConvivenciaPix.Infrastructure.Messaging;

public sealed class KafkaPublisher : IKafkaPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaPublisher> _logger;

    public KafkaPublisher(IProducer<string, string> producer, ILogger<KafkaPublisher> logger)
    {
        _producer = producer;
        _logger = logger;
    }

    public async Task PublishAsync(string topic, KafkaEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(envelope);
        var message = new Message<string, string>
        {
            Key = envelope.MessageId,
            Value = json
        };

        if (envelope.CorrelationId is not null)
            message.Headers = new Headers
            {
                { "correlationId", System.Text.Encoding.UTF8.GetBytes(envelope.CorrelationId) }
            };

        var result = await _producer.ProduceAsync(topic, message, cancellationToken);

        _logger.LogMessagePublished(topic, envelope.MessageId, result.TopicPartitionOffset.ToString());
    }

    public void Dispose() => _producer.Dispose();
}

internal static partial class KafkaPublisherLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Published to {Topic} messageId={MessageId} at {Offset}")]
    public static partial void LogMessagePublished(this ILogger logger, string topic, string messageId, string offset);
}
