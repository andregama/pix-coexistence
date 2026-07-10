namespace ConvivenciaPix.Application.Interfaces;

public interface ISpiXmlParser
{
    string ExtractMessageId(string xml);
    decimal ExtractAmount(string xml);
    string ExtractPayerId(string xml);
    string ExtractPayeeId(string xml);
    DateTimeOffset ExtractTimestamp(string xml);

    /// <summary>Returns the message type identifier (e.g. "pacs.008", "pacs.004", "pacs.002").</summary>
    string ExtractMessageType(string xml);

    /// <summary>
    /// Returns the idempotency key as defined in Catálogo de Serviços do SFN v5.12:
    /// pacs.008 → CdtTrfTxInf/PmtId/EndToEndId,
    /// pacs.002 → TxInfAndSts/OrgnlEndToEndId,
    /// pacs.004 → TxInf/RtrId.
    /// </summary>
    string ExtractIdempotentId(string xml, string msgType);

    /// <summary>
    /// Returns the back-link to the original transaction, or null when not applicable.
    /// pacs.004 → TxInf/OrgnlEndToEndId (EndToEndId of the original pacs.008).
    /// </summary>
    string? ExtractOriginalIdempotentId(string xml, string msgType);
}
