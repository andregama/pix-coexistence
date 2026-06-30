namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateSystemAInboundUseCase
{
    Task ExecuteAsync(string rawCdcJson, IReadOnlySet<string> allowedTypes, CancellationToken cancellationToken);
}
