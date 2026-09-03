namespace Orchestrator.PayerAccount;

/// <summary>
/// The payer's (debtor's) account, as carried in a Bacen SPI pacs.008 <c>DbtrAcct</c>:
/// <see cref="Branch"/> is the agência (DbtrAcct/Id/Othr/Issr) and <see cref="Account"/> is the
/// account number (DbtrAcct/Id/Othr/Id).
/// </summary>
/// <param name="Branch">The branch / agência. May be empty when the message omits Issr.</param>
/// <param name="Account">The account number.</param>
public sealed record PayerAccountInfo(string Branch, string Account);
