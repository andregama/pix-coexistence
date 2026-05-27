using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.PullStream;

public interface IPullStreamUseCase
{
    Task<PullStreamResult> ExecuteAsync(string? streamId, bool multipart, string ispb, CancellationToken cancellationToken);
}

public sealed record PullStreamResult(
    IReadOnlyList<OutboundMessage> Messages,
    string NextStreamId);
