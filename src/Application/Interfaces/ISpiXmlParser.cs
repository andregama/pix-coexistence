namespace ConvivenciaPix.Application.Interfaces;

public interface ISpiXmlParser
{
    string ExtractMessageId(string xml);
    decimal ExtractAmount(string xml);
    string ExtractPayerId(string xml);
    string ExtractPayeeId(string xml);
    DateTimeOffset ExtractTimestamp(string xml);

    /// <summary>Returns the message type identifier (e.g. "pacs.008", "pain.012", "camt.055").</summary>
    string ExtractMessageType(string xml);

    /// <summary>
    /// Returns the message-level idempotency key as defined in Catálogo de Serviços do SFN v5.12:
    /// pacs.008 → CdtTrfTxInf/PmtId/EndToEndId,
    /// pacs.002 → TxInfAndSts/OrgnlEndToEndId,
    /// pacs.004 → TxInf/RtrId,
    /// trck.002 → Tx/PmtId/EndToEndId (internal-transaction id, shared across A and B),
    /// pibr.001 → EchoReq/GrpHdr/MsgId (SPI Echo has no EndToEndId).
    /// For the Pix Automático / cancellation families (pain.*, camt.*, admi.*, reda.*) the key is
    /// the AppHdr/GrpHdr &lt;MsgId&gt;. This is the value the proxy layer uses for de-duplication
    /// (RF-03) and is NOT necessarily the same as the correlation key — see
    /// <see cref="ExtractCorrelationKey"/>.
    /// </summary>
    string ExtractIdempotentId(string xml, string msgType);

    /// <summary>
    /// Returns the key on which System A and System B are correlated (the <c>SpiSentMsg</c>/
    /// <c>SpiReceivedMsg</c> row key). For most types this is the shared message-level idempotency
    /// key (<see cref="ExtractIdempotentId"/>). For the types where the orchestrator cannot align
    /// the two systems' message-level keys, it is a value guaranteed identical on both sides:
    /// <list type="bullet">
    /// <item>pain.011/pain.012/pain.014 → "&lt;msgType&gt;:&lt;recurrenceId (IdRec)&gt;" (recurrenceId is
    /// shared across A and B for the whole authorization lifecycle).</item>
    /// <item>camt.055/camt.029 → the original payment's OrgnlEndToEndId (references the
    /// already-correlated pacs.008, so it is shared).</item>
    /// <item>camt.025 → the answered trck.002's internal-transaction EndToEndId
    /// (RctDtls/OrgnlPmtId/PrtryId), shared across A and B.</item>
    /// </list>
    /// </summary>
    string ExtractCorrelationKey(string xml, string msgType);

    /// <summary>
    /// Reports which strategy <see cref="ExtractCorrelationKey"/> uses for the given type, for RF-05
    /// monitoring: "MessageKey" (raw idempotency key) or "DerivedKey" (recurrenceId / OrgnlEndToEndId).
    /// </summary>
    string GetCorrelationSource(string msgType);

    /// <summary>
    /// Returns the back-link to the original transaction, or null when not applicable.
    /// pacs.004 → TxInf/OrgnlEndToEndId (EndToEndId of the original pacs.008).
    /// </summary>
    string? ExtractOriginalIdempotentId(string xml, string msgType);
}
