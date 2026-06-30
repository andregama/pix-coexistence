using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateSystemBOutboundUseCase
{
    Task ExecuteAsync(KafkaEnvelope envelope, IReadOnlySet<string> allowedTypes, CancellationToken cancellationToken);
}
