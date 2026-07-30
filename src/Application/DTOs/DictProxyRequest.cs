namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// A DICT request captured from System B, ready to be re-signed and forwarded to the real DICT API.
/// <paramref name="PathAndQuery"/> is the relative path plus query string (no scheme/host); it is
/// appended to the configured DICT base URL. <paramref name="Headers"/> carries the request headers
/// to forward (hop-by-hop and host headers are excluded by the forwarder).
/// </summary>
public sealed record DictProxyRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string[]> Headers,
    string? ContentType,
    byte[] Body);
