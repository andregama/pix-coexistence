namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateSystemAInboundUseCase
{
    /// <summary>
    /// Correlates a System A inbound (SPI→PSP) message and delivers it to System B. Routing is driven
    /// by <paramref name="types"/> — see <see cref="InboundCorrelationTypes"/>.
    /// </summary>
    Task ExecuteAsync(string rawCdcJson, InboundCorrelationTypes types, CancellationToken cancellationToken);
}
