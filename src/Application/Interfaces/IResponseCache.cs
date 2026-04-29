namespace ConvivenciaPix.Application.Interfaces;

public interface IResponseCache
{
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetIdempotencyKeyAsync(string messageId, string response, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<string?> GetIdempotencyKeyAsync(string messageId, CancellationToken cancellationToken = default);
}
