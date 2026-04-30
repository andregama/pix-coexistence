namespace ConvivenciaPix.Infrastructure.Orchestrator;

public sealed class OrchestratorOptions
{
    /// <summary>Base URL of the orchestrator REST API, e.g. https://orchestrator.internal.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Per-request timeout in seconds (passed to AddStandardResilienceHandler).</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>API key sent as X-Api-Key header on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;
}
