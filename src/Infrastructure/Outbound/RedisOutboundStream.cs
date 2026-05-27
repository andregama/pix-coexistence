using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace ConvivenciaPix.Infrastructure.Outbound;

public sealed class OutboundStreamOptions
{
    public int LongPollSeconds { get; set; } = 5;
    public int StreamTtlMinutes { get; set; } = 10;
}

public sealed class RedisOutboundStream : IOutboundStream
{
    private const string QueueKey = "spi:out:queue";
    private const string StreamPrefix = "spi:out:stream:";
    private const string NotifyChannel = "spi:out:notify";

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _streamTtl;
    private readonly ILogger<RedisOutboundStream> _logger;

    public RedisOutboundStream(
        IConnectionMultiplexer redis,
        IOptions<OutboundStreamOptions> options,
        ILogger<RedisOutboundStream> logger)
    {
        _redis = redis;
        _streamTtl = TimeSpan.FromMinutes(options.Value.StreamTtlMinutes);
        _logger = logger;
    }

    public async Task EnqueueAsync(string piResourceId, string signedXml, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var entry = JsonSerializer.Serialize(new QueueEntry(piResourceId, signedXml));
        // Sorted set ordered by enqueue time (microseconds since epoch). Unique member because
        // PiResourceId is embedded in the JSON.
        var score = ScoreNow();
        await db.SortedSetAddAsync(QueueKey, entry, score);

        var sub = _redis.GetSubscriber();
        await sub.PublishAsync(new RedisChannel(NotifyChannel, RedisChannel.PatternMode.Literal), RedisValue.EmptyString);

        _logger.LogDebug("Enqueued outbound message PiResourceId={PiResourceId}", piResourceId);
    }

    public async Task<IReadOnlyList<OutboundMessage>> PeekAsync(int max, TimeSpan longPollTimeout, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var initial = await ReadHeadAsync(db, max);
        if (initial.Count > 0) return initial;

        // Long-poll: subscribe before re-reading, then wait until enqueue or timeout.
        var sub = _redis.GetSubscriber();
        var channel = new RedisChannel(NotifyChannel, RedisChannel.PatternMode.Literal);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(RedisChannel _, RedisValue __) => tcs.TrySetResult(true);

        try
        {
            await sub.SubscribeAsync(channel, Handler);

            var afterSub = await ReadHeadAsync(db, max);
            if (afterSub.Count > 0) return afterSub;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(longPollTimeout);
            try
            {
                await using (cts.Token.Register(() => tcs.TrySetResult(false)))
                    await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* fall through to re-read */ }

            return await ReadHeadAsync(db, max);
        }
        finally
        {
            await sub.UnsubscribeAsync(channel, Handler);
        }
    }

    public async Task SaveStreamAsync(string streamId, IReadOnlyList<string> piResourceIds, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var key = StreamPrefix + streamId;
        var value = string.Join(",", piResourceIds);
        await db.StringSetAsync(key, value, _streamTtl);
    }

    public async Task<IReadOnlyList<string>?> GetAndDeleteStreamAsync(string streamId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var key = StreamPrefix + streamId;
        var value = await db.StringGetDeleteAsync(key);
        if (value.IsNullOrEmpty) return null;
        var csv = value.ToString();
        return csv.Length == 0 ? Array.Empty<string>() : csv.Split(',');
    }

    public async Task CommitAsync(IReadOnlyList<string> piResourceIds, CancellationToken cancellationToken = default)
    {
        if (piResourceIds.Count == 0) return;
        var db = _redis.GetDatabase();
        // Re-read the head so we know the exact JSON members to ZREM. The head we wrote into
        // the stream entry is at most a small window, so reading 1000 here is safe.
        var entries = await db.SortedSetRangeByRankAsync(QueueKey, 0, 999);
        var ids = new HashSet<string>(piResourceIds, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parsed = JsonSerializer.Deserialize<QueueEntry>((string)entry!);
            if (parsed is not null && ids.Contains(parsed.PiResourceId))
                await db.SortedSetRemoveAsync(QueueKey, entry);
        }
    }

    private static async Task<IReadOnlyList<OutboundMessage>> ReadHeadAsync(IDatabase db, int max)
    {
        var entries = await db.SortedSetRangeByRankAsync(QueueKey, 0, max - 1);
        if (entries.Length == 0) return Array.Empty<OutboundMessage>();

        var result = new List<OutboundMessage>(entries.Length);
        foreach (var entry in entries)
        {
            var parsed = JsonSerializer.Deserialize<QueueEntry>((string)entry!);
            if (parsed is not null)
                result.Add(new OutboundMessage(parsed.PiResourceId, parsed.SignedXml));
        }
        return result;
    }

    private static double ScoreNow()
    {
        // Microsecond-resolution timestamp gives unique scores under realistic enqueue rates.
        var now = DateTimeOffset.UtcNow;
        return now.ToUnixTimeMilliseconds() * 1000.0 + (now.Ticks % 10_000) / 10.0;
    }

    private sealed record QueueEntry(string PiResourceId, string SignedXml);
}
