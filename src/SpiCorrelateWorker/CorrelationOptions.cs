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

    public IReadOnlySet<string> GetAllowedSet() =>
        AllowedMessageTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
