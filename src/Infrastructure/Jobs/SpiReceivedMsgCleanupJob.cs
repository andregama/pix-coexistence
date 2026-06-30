using ConvivenciaPix.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Quartz;

namespace ConvivenciaPix.Infrastructure.Jobs;

[DisallowConcurrentExecution]
public sealed class SpiReceivedMsgCleanupJob : IJob
{
    private const int RetentionDays = 30;

    private readonly ISpiReceivedMsgRepository _repository;
    private readonly ILogger<SpiReceivedMsgCleanupJob> _logger;

    public SpiReceivedMsgCleanupJob(ISpiReceivedMsgRepository repository, ILogger<SpiReceivedMsgCleanupJob> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        _logger.LogReceivedCleanupStarted(cutoff);

        try
        {
            var deleted = await _repository.DeleteOlderThanAsync(cutoff, context.CancellationToken);
            _logger.LogReceivedCleanupCompleted(deleted);
        }
        catch (Exception ex)
        {
            _logger.LogReceivedCleanupFailed(ex);
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}

internal static partial class SpiReceivedMsgCleanupJobLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "SpiReceivedMsg cleanup started. Deleting records older than {Cutoff:O}")]
    public static partial void LogReceivedCleanupStarted(this ILogger logger, DateTime cutoff);

    [LoggerMessage(Level = LogLevel.Information, Message = "SpiReceivedMsg cleanup completed. Deleted {Count} records.")]
    public static partial void LogReceivedCleanupCompleted(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "SpiReceivedMsg cleanup failed.")]
    public static partial void LogReceivedCleanupFailed(this ILogger logger, Exception exception);
}
