using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConvivenciaPix.V2.Infrastructure.Registry;

public sealed class RegistryOptions
{
    /// <summary>How often the snapshot is reloaded. Other applications may also write to the table.</summary>
    public int RefreshSeconds { get; set; } = 5;
}

/// <summary>
/// Full in-memory snapshot of the System B documents. Most lookups are misses (the customer is not
/// on System B), so a snapshot avoids a cross-database query for every received transfer, and the
/// last good snapshot keeps routing working while the registry database is unreachable.
/// </summary>
public sealed class SystemBDocumentRegistry(ISystemBDocumentRepository repository) : ISystemBDocumentRegistry
{
    private volatile IReadOnlyCollection<string>? _snapshot;

    public bool IsLoaded => _snapshot is not null;

    public bool Contains(string document)
    {
        var snapshot = _snapshot ?? throw new RegistryUnavailableException();
        return snapshot.Contains(CustomerDocument.Normalize(document));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default) =>
        _snapshot = await repository.LoadAllDocumentsAsync(cancellationToken);
}

public sealed class RegistryRefreshService(
    ISystemBDocumentRegistry registry,
    IOptions<RegistryOptions> options,
    ILogger<RegistryRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.RefreshSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await registry.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogRegistryRefreshFailed(ex, registry.IsLoaded);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

internal static partial class RegistryLogMessages
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Could not refresh the System B document registry (snapshot available: {HasSnapshot})")]
    public static partial void LogRegistryRefreshFailed(this ILogger logger, Exception ex, bool hasSnapshot);
}
