using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Quartz;

namespace ConvivenciaPix.Infrastructure.Jobs;

[DisallowConcurrentExecution]
public sealed class SpiSentMsgCleanupJob : IJob
{
    private const int RetentionDays = 30;

    private readonly ISpiSentMsgRepository _repository;
    private readonly ILogger<SpiSentMsgCleanupJob> _logger;

    public SpiSentMsgCleanupJob(ISpiSentMsgRepository repository, ILogger<SpiSentMsgCleanupJob> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        _logger.LogCleanupStarted(cutoff);

        try
        {
            var deleted = await _repository.DeleteOlderThanAsync(cutoff, context.CancellationToken);
            _logger.LogCleanupCompleted(deleted);
        }
        catch (Exception ex)
        {
            _logger.LogCleanupFailed(ex);
            // Re-throw so Quartz marks the trigger as failed and retries on next schedule
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}

internal static partial class SpiSentMsgCleanupJobLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "SpiSentMsg cleanup started. Deleting records older than {Cutoff:O}")]
    public static partial void LogCleanupStarted(this ILogger logger, DateTime cutoff);

    [LoggerMessage(Level = LogLevel.Information, Message = "SpiSentMsg cleanup completed. Deleted {Count} records.")]
    public static partial void LogCleanupCompleted(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "SpiSentMsg cleanup failed.")]
    public static partial void LogCleanupFailed(this ILogger logger, Exception exception);
}
