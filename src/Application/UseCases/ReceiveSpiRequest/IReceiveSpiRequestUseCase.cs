using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;

public interface IReceiveSpiRequestUseCase
{
    /// <summary>
    /// Stores each inbound message and publishes it to the processing pipeline (no validation,
    /// no idempotency check — per Bacen SPI manual §2.2.1). Returns the PI-ResourceId assigned
    /// to each message, in input order.
    /// </summary>
    Task<IReadOnlyList<string>> ExecuteAsync(
        IReadOnlyList<SpiInboundMessage> messages,
        string ispb,
        CancellationToken cancellationToken);
}
