using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.Application.UseCases.PullStream;

public sealed class AckStreamUseCase : IAckStreamUseCase
{
    private readonly IOutboundStream _stream;
    private readonly ISpiReceivedMsgRepository _receivedMsgRepository;
    private readonly ILogger<AckStreamUseCase> _logger;

    public AckStreamUseCase(
        IOutboundStream stream,
        ISpiReceivedMsgRepository receivedMsgRepository,
        ILogger<AckStreamUseCase> logger)
    {
        _stream = stream;
        _receivedMsgRepository = receivedMsgRepository;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(string streamId, CancellationToken cancellationToken)
    {
        var ids = await _stream.GetAndDeleteStreamAsync(streamId, cancellationToken);
        if (ids is null) return false;
        if (ids.Count > 0)
        {
            await _stream.CommitAsync(ids, cancellationToken);
            // Durably record consumption: the acked stream ids are the PiResourceIds enqueued for System B.
            var marked = await _receivedMsgRepository.MarkConsumedByResourceIdsAsync(ids, DateTime.UtcNow, cancellationToken);
            _logger.LogDebug("Acked {Count} message(s) for stream {StreamId}; marked {Marked} consumed", ids.Count, streamId, marked);
        }
        return true;
    }
}
