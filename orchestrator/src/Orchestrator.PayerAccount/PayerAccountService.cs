namespace Orchestrator.PayerAccount;

/// <summary>
/// Resolves the payer's branch + account for a <c>SpiSentMsg</c> row.
/// <list type="bullet">
/// <item><b>pacs.008</b>: the payer account is read directly from the message's <c>DbtrAcct</c>.</item>
/// <item><b>pacs.004</b> (return): the return carries no customer account, so its
/// <c>OrgnlEndToEndId</c> (preferring the stored <c>OriginalMsgIdempotentId</c>) is used to look up
/// the original pacs.008 row in <c>SpiSentMsg</c>, and the account is read from there.</item>
/// </list>
/// The XML is taken from System A, falling back to System B when A is null.
/// </summary>
public sealed class PayerAccountService
{
    private readonly ISpiSentMsgReader _reader;
    private readonly IPayerAccountExtractor _extractor;

    public PayerAccountService(ISpiSentMsgReader reader, IPayerAccountExtractor extractor)
    {
        _reader = reader;
        _extractor = extractor;
    }

    /// <summary>
    /// Returns the payer account for the pacs.008/pacs.004 row identified by <paramref name="idempotentId"/>,
    /// or <c>null</c> when the row (or, for a pacs.004, its original pacs.008) is not found in
    /// <c>SpiSentMsg</c> or carries no debtor account.
    /// </summary>
    /// <exception cref="NotSupportedException">The row's MsgType is neither pacs.008 nor pacs.004.</exception>
    public async Task<PayerAccountInfo?> GetPayerAccountAsync(
        string idempotentId, CancellationToken cancellationToken = default)
    {
        var row = await _reader.FindByIdempotentIdAsync(idempotentId, cancellationToken);
        if (row is null)
            return null;

        if (IsFamily(row.MsgType, "pacs.008"))
            return ExtractFromRow(row);

        if (IsFamily(row.MsgType, "pacs.004"))
        {
            // A pacs.004 return has no DbtrAcct; follow the original payment's EndToEndId.
            var originalEndToEndId = row.OriginalMsgIdempotentId;
            if (string.IsNullOrEmpty(originalEndToEndId))
            {
                var xml = PreferSystemA(row);
                originalEndToEndId = xml is null ? null : _extractor.ExtractOriginalEndToEndId(xml);
            }

            if (string.IsNullOrEmpty(originalEndToEndId))
                return null;

            var originalRow = await _reader.FindByIdempotentIdAsync(originalEndToEndId, cancellationToken);
            return originalRow is null ? null : ExtractFromRow(originalRow);
        }

        throw new NotSupportedException(
            $"Payer account extraction supports pacs.008 and pacs.004 only; MsgType was '{row.MsgType}'.");
    }

    private PayerAccountInfo? ExtractFromRow(SpiSentMsgRow row)
    {
        var xml = PreferSystemA(row);
        return xml is null ? null : _extractor.ExtractFromPacs008(xml);
    }

    // System A is the source of truth; fall back to System B when A's XML is absent.
    private static string? PreferSystemA(SpiSentMsgRow row) =>
        !string.IsNullOrEmpty(row.XmlMsgSystemA) ? row.XmlMsgSystemA
        : !string.IsNullOrEmpty(row.XmlMsgSystemB) ? row.XmlMsgSystemB
        : null;

    // MsgType is stored as the family token (e.g. "pacs.008"); match tolerantly.
    private static bool IsFamily(string msgType, string family) =>
        msgType.StartsWith(family, StringComparison.OrdinalIgnoreCase);
}
