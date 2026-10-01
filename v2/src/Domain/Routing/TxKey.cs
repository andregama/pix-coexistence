namespace ConvivenciaPix.V2.Domain.Routing;

/// <summary>Kind of business key that ties several SPI messages to one transaction.</summary>
public enum TxKeyKind
{
    /// <summary>EndToEndId of a payment (pacs.008 and everything that references it).</summary>
    E2E,
    /// <summary>RtrId of a payment return (pacs.004).</summary>
    RTR,
    /// <summary>Recurrence id of a Pix Automático authorisation (MndtId).</summary>
    REC
}

public readonly record struct TxKey(TxKeyKind Kind, string Value);
