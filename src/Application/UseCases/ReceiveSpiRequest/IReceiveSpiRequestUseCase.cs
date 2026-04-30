namespace ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;

public interface IReceiveSpiRequestUseCase
{
    Task<ReceiveSpiRequestResult> ExecuteAsync(string rawXml, CancellationToken cancellationToken);
}

public sealed record ReceiveSpiRequestResult(
    string? ResponseXml = null,
    bool IsError = false,
    string? ErrorReason = null,
    string? ErrorCode = null);
