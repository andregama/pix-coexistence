using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface IReceiveSystemBSentUseCase
{
    Task ExecuteAsync(KafkaEnvelope envelope, CancellationToken cancellationToken);
}
