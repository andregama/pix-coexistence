using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Repositories;

namespace ConvivenciaPix.V2.Application.UseCases.PullStream;

public interface IAckStreamUseCase
{
    /// <summary>Explicitly acks the batch delivered under the stream. Idempotent.</summary>
    Task<int> ExecuteAsync(SystemName system, string streamId, CancellationToken cancellationToken = default);
}

public sealed class AckStreamUseCase(IDeliveryQueue queue) : IAckStreamUseCase
{
    public Task<int> ExecuteAsync(SystemName system, string streamId, CancellationToken cancellationToken = default) =>
        queue.AckAsync(system, streamId, cancellationToken);
}
