using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConvivenciaPix.Infrastructure.Cache;

public sealed class RedisResponseCache : IResponseCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisResponseCache> _logger;

    private const string ResponsePrefix = "response:";
    private const string IdempotencyPrefix = "idempotency:";

    public RedisResponseCache(IConnectionMultiplexer redis, ILogger<RedisResponseCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"{ResponsePrefix}{key}", value, ttl);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync($"{ResponsePrefix}{key}");

        if (value.IsNullOrEmpty)
        {
            // Risk mitigation: log cache misses at debug level so Redis eviction alerts
            // can be correlated with application-level miss rate spikes.
            _logger.LogCacheMiss(key);
            return null;
        }

        return value.ToString();
    }

    public async Task SetIdempotencyKeyAsync(string messageId, string response, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        // NX (set if not exists) prevents overwriting an already-processed response
        await db.StringSetAsync($"{IdempotencyPrefix}{messageId}", response, ttl, When.NotExists);
    }

    public async Task<string?> GetIdempotencyKeyAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync($"{IdempotencyPrefix}{messageId}");
        return value.IsNullOrEmpty ? null : value.ToString();
    }
}

internal static partial class RedisResponseCacheLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache miss for response key {Key}")]
    public static partial void LogCacheMiss(this ILogger logger, string key);
}
