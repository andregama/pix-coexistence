using ConvivenciaPix.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ConvivenciaPix.Infrastructure.Orchestrator;

/// <summary>
/// Production IOrchestratorClient — resolves System A / System B ID mappings
/// via the orchestrator's REST API.
///
/// Contract: GET /api/correlations/{idSystemA}
///   200 → { "idSystemA": "...", "idSystemB": "..." }
///   404 → no mapping found (triggers heuristic fallback in the correlate worker)
///   Other → throws, bubbles to the consumer's DLQ routing
///
/// Registered for non-Development environments. StubOrchestratorClient is used in Development.
/// </summary>
public sealed class HttpOrchestratorClient : IOrchestratorClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpOrchestratorClient> _logger;

    public HttpOrchestratorClient(HttpClient http, ILogger<HttpOrchestratorClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<OrchestratorResult?> FindCorrelationAsync(
        string idSystemA, CancellationToken cancellationToken = default)
    {
        var url = $"api/correlations/{Uri.EscapeDataString(idSystemA)}";

        _logger.LogOrchestratorRequest(idSystemA);

        var response = await _http.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogOrchestratorNotFound(idSystemA);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<OrchestratorCorrelationResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                $"Orchestrator returned an empty body for IdSystemA={idSystemA}");

        _logger.LogOrchestratorFound(idSystemA, dto.IdSystemB);
        return new OrchestratorResult(dto.IdSystemA, dto.IdSystemB);
    }
}

internal sealed class OrchestratorCorrelationResponse
{
    [JsonPropertyName("idSystemA")]
    public string IdSystemA { get; set; } = string.Empty;

    [JsonPropertyName("idSystemB")]
    public string IdSystemB { get; set; } = string.Empty;
}

internal static partial class HttpOrchestratorClientLogMessages
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Querying orchestrator for IdSystemA={IdSystemA}")]
    public static partial void LogOrchestratorRequest(this ILogger logger, string idSystemA);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Orchestrator returned 404 for IdSystemA={IdSystemA} — heuristic fallback will be used")]
    public static partial void LogOrchestratorNotFound(this ILogger logger, string idSystemA);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orchestrator correlation found. IdSystemA={IdSystemA} → IdSystemB={IdSystemB}")]
    public static partial void LogOrchestratorFound(this ILogger logger, string idSystemA, string idSystemB);
}
