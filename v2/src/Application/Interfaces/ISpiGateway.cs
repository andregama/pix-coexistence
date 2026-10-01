namespace ConvivenciaPix.V2.Application.Interfaces;

/// <summary>A request to the real SPI. <paramref name="Headers"/> never include Content-* headers.</summary>
public sealed record SpiGatewayRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    byte[] Body,
    int TimeoutSeconds,
    bool IsPull = false);

public sealed record SpiGatewayResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    string? ContentType,
    byte[] Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var values) && values.Length > 0 ? string.Join(",", values) : null;
}

/// <summary>Thrown when the real SPI did not answer within the request's timeout.</summary>
public sealed class SpiGatewayTimeoutException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The only connection to the real SPI. Implementations never retry: a retried POST could be
/// rejected as a duplicate after the first attempt was in fact accepted.
/// </summary>
public interface ISpiGateway
{
    Task<SpiGatewayResponse> SendAsync(SpiGatewayRequest request, CancellationToken cancellationToken = default);
}
