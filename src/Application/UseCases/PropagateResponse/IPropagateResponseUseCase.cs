namespace ConvivenciaPix.Application.UseCases.PropagateResponse;

public interface IPropagateResponseUseCase
{
    Task ExecuteAsync(string rawCdcJson, CancellationToken cancellationToken);
}
