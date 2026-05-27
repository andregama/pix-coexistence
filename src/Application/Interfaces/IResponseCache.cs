namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Generic key/value cache. In the current pipeline it carries the System B raw request XML
/// (key "request:{idSystemB}") so the proxy worker can include it in the comparison event.
/// Idempotency and response delivery moved out of this cache as part of aligning the proxy
/// with the Bacen SPI manual §2.2 send-ack + stream-pull model.
/// </summary>
public interface IResponseCache
{
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
}
