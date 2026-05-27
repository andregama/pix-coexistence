using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.Application.UseCases.PullStream;

public sealed class AckStreamUseCase : IAckStreamUseCase
{
    private readonly IOutboundStream _stream;
    private readonly ILogger<AckStreamUseCase> _logger;

    public AckStreamUseCase(IOutboundStream stream, ILogger<AckStreamUseCase> logger)
    {
        _stream = stream;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(string streamId, CancellationToken cancellationToken)
    {
        var ids = await _stream.GetAndDeleteStreamAsync(streamId, cancellationToken);
        if (ids is null) return false;
        if (ids.Count > 0)
        {
            await _stream.CommitAsync(ids, cancellationToken);
            _logger.LogDebug("Acked {Count} message(s) for stream {StreamId}", ids.Count, streamId);
        }
        return true;
    }
}
