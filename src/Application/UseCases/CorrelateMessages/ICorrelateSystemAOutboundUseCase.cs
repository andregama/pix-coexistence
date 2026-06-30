namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateSystemAOutboundUseCase
{
    Task ExecuteAsync(string rawCdcJson, IReadOnlySet<string> allowedTypes, CancellationToken cancellationToken);
}
