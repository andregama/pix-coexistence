namespace ConvivenciaPix.Application.UseCases.CorrelateMessages;

public interface ICorrelateSystemAInboundUseCase
{
    /// <param name="primaryTypes">
    /// Inbound message types that are primary/unsolicited initiations (e.g. a received pacs.008 Pix
    /// credit) rather than responses to an A/B outbound. These skip SpiSentMsg correlation and are
    /// passed through to System B unchanged.
    /// </param>
    /// <param name="correlateByOriginalMsgIdTypes">
    /// Inbound message-level responses (e.g. admi.002 rejections) that reference the original message
    /// by its MsgId. These are correlated via a SpiSentMsg.MsgIdSystemA lookup; when the MsgId is not
    /// found the message is still replicated to System B (unchanged) with a warning, never DLQ'd.
    /// </param>
    Task ExecuteAsync(
        string rawCdcJson,
        IReadOnlySet<string> allowedTypes,
        IReadOnlySet<string> primaryTypes,
        IReadOnlySet<string> correlateByOriginalMsgIdTypes,
        CancellationToken cancellationToken);
}
