namespace ConvivenciaPix.Application.Interfaces;

public interface IResponseCache
{
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetIdempotencyKeyAsync(string messageId, string response, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<string?> GetIdempotencyKeyAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for a response to be published on the specified channel (IdSystemB).
    /// Returns the response or null if the timeout expires.
    /// </summary>
    Task<string?> WaitForResponseAsync(string idSystemB, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the response to the specified channel (IdSystemB) to unblock waiters.
    /// </summary>
    Task SignalResponseAsync(string idSystemB, string response, CancellationToken cancellationToken = default);
}
