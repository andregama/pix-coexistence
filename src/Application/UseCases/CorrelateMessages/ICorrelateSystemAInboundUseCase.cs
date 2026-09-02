namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateSystemAInboundUseCase
{
    /// <param name="primaryTypes">
    /// Inbound message types that are primary/unsolicited initiations (e.g. a received pacs.008 Pix
    /// credit) rather than responses to an A/B outbound. These skip SpiSentMsg correlation and are
    /// passed through to System B unchanged.
    /// </param>
    Task ExecuteAsync(
        string rawCdcJson,
        IReadOnlySet<string> allowedTypes,
        IReadOnlySet<string> primaryTypes,
        CancellationToken cancellationToken);
}
