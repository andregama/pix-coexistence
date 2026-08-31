using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace ConvivenciaPix.Application.UseCases.PullStream;

public sealed class PullStreamOptions
{
    public int LongPollSeconds { get; set; } = 5;
    public int MaxMessagesPerMultipart { get; set; } = 10;
}

public sealed class PullStreamUseCase : IPullStreamUseCase
{
    private readonly IOutboundStream _stream;
    private readonly ISpiReceivedMsgRepository _receivedMsgRepository;
    private readonly TimeSpan _longPoll;
    private readonly int _maxMultipart;
    private readonly ILogger<PullStreamUseCase> _logger;

    public PullStreamUseCase(
        IOutboundStream stream,
        ISpiReceivedMsgRepository receivedMsgRepository,
        IOptions<PullStreamOptions> options,
        ILogger<PullStreamUseCase> logger)
    {
        _stream = stream;
        _receivedMsgRepository = receivedMsgRepository;
        _longPoll = TimeSpan.FromSeconds(options.Value.LongPollSeconds);
        _maxMultipart = options.Value.MaxMessagesPerMultipart;
        _logger = logger;
    }

    public async Task<PullStreamResult> ExecuteAsync(string? streamId, bool multipart, string ispb, CancellationToken cancellationToken)
    {
        // Following a PI-Pull-Next implicitly acks the previous batch (Bacen manual §2.2.2.2).
        if (streamId is not null)
        {
            var previous = await _stream.GetAndDeleteStreamAsync(streamId, cancellationToken);
            if (previous is { Count: > 0 })
            {
                await _stream.CommitAsync(previous, cancellationToken);
                // Implicit ack durably records consumption, mirroring the explicit DELETE ack path.
                var marked = await _receivedMsgRepository.MarkConsumedByResourceIdsAsync(previous, DateTime.UtcNow, cancellationToken);
                _logger.LogDebug("Committed {Count} message(s) from previous stream {StreamId}; marked {Marked} consumed", previous.Count, streamId, marked);
            }
        }

        var max = multipart ? _maxMultipart : 1;
        var messages = await _stream.PeekAsync(max, _longPoll, cancellationToken);

        var nextStreamId = NewStreamId();
        var ids = messages.Select(m => m.PiResourceId).ToArray();
        await _stream.SaveStreamAsync(nextStreamId, ids, cancellationToken);

        return new PullStreamResult(messages, nextStreamId);
    }

    private static string NewStreamId()
    {
        // 12 hex chars matches the manual's examples (e.g. f0d744d792d6).
        var bytes = RandomNumberGenerator.GetBytes(6);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
