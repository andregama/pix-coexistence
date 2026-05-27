using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConvivenciaPix.Infrastructure.Cache;

public sealed class RedisResponseCache : IResponseCache
{
    private const string KeyPrefix = "response:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisResponseCache> _logger;

    public RedisResponseCache(IConnectionMultiplexer redis, ILogger<RedisResponseCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"{KeyPrefix}{key}", value, ttl);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync($"{KeyPrefix}{key}");

        if (value.IsNullOrEmpty)
        {
            _logger.LogCacheMiss(key);
            return null;
        }

        return value.ToString();
    }
}

internal static partial class RedisResponseCacheLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache miss for response key {Key}")]
    public static partial void LogCacheMiss(this ILogger logger, string key);
}
