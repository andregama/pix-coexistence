namespace ConvivenciaPix.SpiCorrelateWorker;

public sealed class CorrelationOptions
{
    public string AllowedMessageTypes { get; set; } = "pacs.002,pacs.004,pacs.008";

    public IReadOnlySet<string> GetAllowedSet() =>
        AllowedMessageTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
