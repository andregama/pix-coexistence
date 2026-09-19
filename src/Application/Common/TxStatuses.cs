namespace ConvivenciaPix.Application.Common;

/// <summary>
/// pacs.002 transaction-status (TxSts) codes used by the coexistence flow.
/// </summary>
public static class TxStatuses
{
    /// <summary>Transfer rejected by the responder.</summary>
    public const string Rejected = "RJCT";

    /// <summary>
    /// Statuses that mean the responder accepted the transfer (settlement in process / completed).
    /// Used by analytics to decide whether a received transfer was accepted by System A.
    /// </summary>
    public static readonly IReadOnlySet<string> Accepted =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ACCC", "ACSP", "ACSC" };
}
