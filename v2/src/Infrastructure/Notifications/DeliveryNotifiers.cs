using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Domain;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConvivenciaPix.V2.Infrastructure.Notifications;

/// <summary>Notifier for a single process (tests, or a deployment without Redis).</summary>
public class InMemoryDeliveryNotifier : IDeliveryNotifier
{
    private readonly Signal[] _signals = Enum.GetValues<SystemName>().Select(_ => new Signal()).ToArray();

    public virtual Task NotifyAsync(SystemName system, CancellationToken cancellationToken = default)
    {
        Pulse(system);
        return Task.CompletedTask;
    }

    public virtual Task NextNotification(SystemName system) => _signals[(int)system].Next;

    protected void Pulse(SystemName system) => _signals[(int)system].Pulse();

    private sealed class Signal
    {
        private TaskCompletionSource _source = New();

        public Task Next => Volatile.Read(ref _source).Task;

        public void Pulse() => Interlocked.Exchange(ref _source, New()).TrySetResult();

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Notifier across processes: the inbound worker publishes on a Redis channel and every API
/// instance wakes its waiting pull streams. Redis is used for nothing else, and its failure only
/// means streams fall back to polling the SQL queue.
/// </summary>
public sealed class RedisDeliveryNotifier : InMemoryDeliveryNotifier, IAsyncDisposable
{
    private const string ChannelPrefix = "v2:spi:out:";

    private readonly string _connectionString;
    private readonly ILogger<RedisDeliveryNotifier> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnectionMultiplexer? _redis;
    private long _nextAttemptTicks;

    public RedisDeliveryNotifier(string connectionString, ILogger<RedisDeliveryNotifier> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public override async Task NotifyAsync(SystemName system, CancellationToken cancellationToken = default)
    {
        Pulse(system);

        var redis = await ConnectAsync();
        if (redis is null) return;
        await redis.GetSubscriber().PublishAsync(Channel(system), RedisValue.EmptyString);
    }

    public override Task NextNotification(SystemName system)
    {
        // Subscribe in the background on first use; until then the caller's poll interval applies.
        if (_redis is null)
            _ = ConnectAsync();
        return base.NextNotification(system);
    }

    private async Task<IConnectionMultiplexer?> ConnectAsync()
    {
        if (_redis is not null) return _redis;
        if (Environment.TickCount64 < Interlocked.Read(ref _nextAttemptTicks)) return null;

        await _connectLock.WaitAsync();
        try
        {
            if (_redis is not null) return _redis;

            var options = ConfigurationOptions.Parse(_connectionString);
            options.AbortOnConnectFail = false;
            var redis = await ConnectionMultiplexer.ConnectAsync(options);

            var subscriber = redis.GetSubscriber();
            foreach (var system in Enum.GetValues<SystemName>())
            {
                var target = system;
                await subscriber.SubscribeAsync(Channel(target), (_, _) => Pulse(target));
            }

            return _redis = redis;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _nextAttemptTicks, Environment.TickCount64 + 10_000);
            _logger.LogRedisUnavailable(ex);
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static RedisChannel Channel(SystemName system) =>
        new($"{ChannelPrefix}{system}:notify", RedisChannel.PatternMode.Literal);

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
        _connectLock.Dispose();
    }
}

internal static partial class DeliveryNotifierLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis is unavailable; pull streams fall back to polling")]
    public static partial void LogRedisUnavailable(this ILogger logger, Exception ex);
}
