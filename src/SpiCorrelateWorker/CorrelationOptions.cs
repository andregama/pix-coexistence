namespace ConvivenciaPix.SpiCorrelateWorker;

public sealed class CorrelationOptions
{
    // admi.004 (SystemEventNotification) is intentionally excluded: it is a system-wide broadcast
    // with no per-transaction counterpart, so it is not correlated (mirrors the pibr Echo handling).
    public string AllowedMessageTypes { get; set; } =
        "pacs.002,pacs.004,pacs.008," +
        "pain.009,pain.011,pain.012,pain.013,pain.014," +
        "camt.014,camt.025,camt.029,camt.055,reda.041,admi.002," +
        "trck.002";

    // Primary/unsolicited inbound initiations received by System A (SPI→PSP) that answer no
    // prior A/B message, so they must NOT be correlated against SpiSentMsg. They are passed through
    // to System B unchanged (signed + delivered) so B also processes the incoming transaction:
    //   pacs.008 — incoming Pix credit; pain.009 — mandate initiation request received;
    //   pain.013 — payment-activation request received.
    // Response types (pacs.002, pacs.004 returns, camt.025, pain.012/014, camt.029, admi.002)
    // stay on the correlate+transform path because they reference an A-outbound message.
    public string PrimaryInboundMessageTypes { get; set; } = "pacs.008,pain.009,pain.013";

    // Inbound message-level responses that reference the original message by its MsgId (not by a
    // transaction/correlation key), so they are correlated via a SpiSentMsg.MsgIdSystemA lookup:
    //   admi.002 — message rejection, referencing the rejected message via RltdRef/Ref.
    // When the referenced MsgId is not found in SpiSentMsg the message is still replicated to
    // System B (unchanged) with a warning, rather than dead-lettered.
    public string CorrelateByOriginalMsgIdMessageTypes { get; set; } = "admi.002";

    public IReadOnlySet<string> GetAllowedSet() => ToSet(AllowedMessageTypes);

    public IReadOnlySet<string> GetPrimaryInboundSet() => ToSet(PrimaryInboundMessageTypes);

    public IReadOnlySet<string> GetCorrelateByOriginalMsgIdSet() => ToSet(CorrelateByOriginalMsgIdMessageTypes);

    private static IReadOnlySet<string> ToSet(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
