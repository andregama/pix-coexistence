using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.Interfaces;

public interface IKafkaPublisher
{
    Task PublishAsync(string topic, KafkaEnvelope envelope, CancellationToken cancellationToken = default);
}
