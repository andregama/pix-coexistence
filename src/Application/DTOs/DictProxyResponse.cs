namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// The response from the real DICT API, returned verbatim to System B except that a signed XML body
/// is re-signed by the proxy before delivery. <paramref name="Headers"/> excludes hop-by-hop and
/// content-length headers, which the host recomputes.
/// </summary>
public sealed record DictProxyResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    string? ContentType,
    byte[] Body);
