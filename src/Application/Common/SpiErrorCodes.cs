namespace ConvivenciaPix.Application.Common;

/// <summary>
/// Internal error markers stamped onto <c>SystemAErrorCode</c> for conditions that are not Bacen
/// error codes but must be queryable in analytics (e.g. a rejected transfer).
/// </summary>
public static class SpiErrorCodes
{
    /// <summary>
    /// Marker written to <c>SystemAErrorCode</c> when a pacs.002 carries <c>TxSts=RJCT</c> (System A
    /// rejected the transfer). Lets analytics detect rejections on the indexed error column instead of
    /// parsing/comparing <c>TxStatus</c>, and surfaces rejections in the existing error analytics.
    /// </summary>
    public const string RejectedTransfer = "REJECTED_TRANSFER";
}
