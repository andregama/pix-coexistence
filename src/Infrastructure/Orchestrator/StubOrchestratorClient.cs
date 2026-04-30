using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.Infrastructure.Orchestrator;

/// <summary>
/// Development stub — always returns null, forcing heuristic fallback.
/// Production: replace with an HTTP client that calls the orchestrator API to resolve
/// the System A / System B ID mapping from the transaction database.
/// </summary>
public sealed class StubOrchestratorClient : IOrchestratorClient
{
    private readonly ILogger<StubOrchestratorClient> _logger;

    public StubOrchestratorClient(ILogger<StubOrchestratorClient> logger)
        => _logger = logger;

    public Task<OrchestratorResult?> FindCorrelationAsync(string idSystemA, CancellationToken cancellationToken = default)
    {
        _logger.LogOrchestratorStub(idSystemA);
        return Task.FromResult<OrchestratorResult?>(null);
    }
}

internal static partial class StubOrchestratorClientLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "StubOrchestratorClient: no correlation found for IdSystemA={IdSystemA} (stub always returns null)")]
    public static partial void LogOrchestratorStub(this ILogger logger, string idSystemA);
}
