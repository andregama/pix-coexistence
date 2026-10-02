using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Simulation;

namespace ConvivenciaPix.V2.Infrastructure.Gateway;

/// <summary>
/// <see cref="ISpiGateway"/> that reaches no SPI at all: every forward and every pull is answered by
/// the simulator inside this process. Used with <c>Storage:Provider=InMemory</c>.
/// </summary>
public sealed class SimulatedSpiGateway(SpiSimulator simulator) : ISpiGateway
{
    public Task<SpiGatewayResponse> SendAsync(SpiGatewayRequest request, CancellationToken cancellationToken = default)
    {
        var accept = request.Headers
            .FirstOrDefault(header => header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)).Value;
        return simulator.HandleAsync(
            request.Method, request.PathAndQuery, accept, request.ContentType, request.Body, cancellationToken);
    }
}
