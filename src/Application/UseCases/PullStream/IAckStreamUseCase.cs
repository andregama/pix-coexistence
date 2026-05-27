namespace ConvivenciaPix.Application.UseCases.PullStream;

public interface IAckStreamUseCase
{
    /// <returns>true if the stream existed and was acked; false if unknown/expired (HTTP 410).</returns>
    Task<bool> ExecuteAsync(string streamId, CancellationToken cancellationToken);
}
