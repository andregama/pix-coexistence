namespace Orchestrator.PayerAccount;

/// <summary>Extracts payer (debtor) details from Bacen SPI payment XML.</summary>
public interface IPayerAccountExtractor
{
    /// <summary>
    /// Extracts the payer's branch + account from a pacs.008 <c>DbtrAcct</c>, or <c>null</c> when the
    /// message carries no debtor account.
    /// </summary>
    PayerAccountInfo? ExtractFromPacs008(string pacs008Xml);

    /// <summary>
    /// Reads a pacs.004's <c>TxInf/OrgnlEndToEndId</c> — the EndToEndId of the original pacs.008 that
    /// is being returned — or <c>null</c> when absent. A pacs.004 itself carries no debtor account,
    /// so this back-link is used to locate the original payment.
    /// </summary>
    string? ExtractOriginalEndToEndId(string pacs004Xml);
}
