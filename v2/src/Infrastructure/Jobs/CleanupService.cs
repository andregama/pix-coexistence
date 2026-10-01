using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Infrastructure.Jobs;

public sealed class CleanupOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Retention of sent routes and of fully delivered inbound messages.</summary>
    public int MessageRetentionDays { get; set; } = 30;

    /// <summary>Longer than the messages: a return can arrive long after the original payment.</summary>
    public int AffinityRetentionDays { get; set; } = 90;

    public int OutboxRetentionDays { get; set; } = 7;
    public int BatchSize { get; set; } = 5000;
}

/// <summary>Deletes rows past their retention, in small batches so the message path is never blocked.</summary>
public sealed class CleanupService : BackgroundService
{
    private readonly ISentRouteRepository _sentRoutes;
    private readonly ITxAffinityRepository _affinities;
    private readonly IInboundMessageRepository _inbound;
    private readonly IAuditOutboxRepository _outbox;
    private readonly CleanupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(
        ISentRouteRepository sentRoutes,
        ITxAffinityRepository affinities,
        IInboundMessageRepository inbound,
        IAuditOutboxRepository outbox,
        IOptions<CleanupOptions> options,
        TimeProvider time,
        ILogger<CleanupService> logger)
    {
        _sentRoutes = sentRoutes;
        _affinities = affinities;
        _inbound = inbound;
        _outbox = outbox;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogCleanupFailed(ex);
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var messageCutoff = now.AddDays(-_options.MessageRetentionDays);
        var batch = Math.Max(1, _options.BatchSize);

        var sent = await DrainAsync(ct => _sentRoutes.DeleteOlderThanAsync(messageCutoff, batch, ct), batch, cancellationToken);
        var inbound = await DrainAsync(ct => _inbound.DeleteOlderThanAsync(messageCutoff, batch, ct), batch, cancellationToken);
        var affinities = await DrainAsync(
            ct => _affinities.DeleteOlderThanAsync(now.AddDays(-_options.AffinityRetentionDays), batch, ct), batch, cancellationToken);
        var outbox = await DrainAsync(
            ct => _outbox.DeletePublishedOlderThanAsync(now.AddDays(-_options.OutboxRetentionDays), batch, ct), batch, cancellationToken);

        _logger.LogCleanupDone(sent, inbound, affinities, outbox);
    }

    private static async Task<int> DrainAsync(Func<CancellationToken, Task<int>> deleteBatch, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await deleteBatch(cancellationToken);
            total += deleted;
        } while (deleted >= batchSize && !cancellationToken.IsCancellationRequested);
        return total;
    }
}

internal static partial class CleanupLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup removed {Sent} sent routes, {Inbound} inbound messages, {Affinities} affinities, {Outbox} outbox events")]
    public static partial void LogCleanupDone(this ILogger logger, int sent, int inbound, int affinities, int outbox);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cleanup failed; it will be retried")]
    public static partial void LogCleanupFailed(this ILogger logger, Exception ex);
}
