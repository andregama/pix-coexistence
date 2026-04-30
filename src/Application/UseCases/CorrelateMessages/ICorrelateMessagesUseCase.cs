using ConvivenciaPix.Domain.ValueObjects;

namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateMessagesUseCase
{
    Task ExecuteAsync(string rawCdcJson, CancellationToken cancellationToken);
}

public sealed record CorrelateMessagesResult(
    string IdSystemA,
    string IdSystemB,
    CorrelationSource Source,
    bool AlreadyCorrelated);
