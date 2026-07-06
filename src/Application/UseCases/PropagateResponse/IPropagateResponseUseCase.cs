using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.PropagateResponse;

public interface IPropagateResponseUseCase
{
    Task ExecuteAsync(SystemBInboundReadyDto ready, CancellationToken cancellationToken);
}
