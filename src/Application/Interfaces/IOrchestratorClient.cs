namespace ConvivenciaPix.Application.Interfaces;

public interface IOrchestratorClient
{
    /// <summary>
    /// Queries orchestrator data to find the System B ID corresponding to a System A transaction.
    /// Returns null when the orchestrator has no record for the given ID (triggers heuristic fallback).
    /// </summary>
    Task<OrchestratorResult?> FindCorrelationAsync(string idSystemA, CancellationToken cancellationToken = default);
}

public sealed record OrchestratorResult(string IdSystemA, string IdSystemB);
