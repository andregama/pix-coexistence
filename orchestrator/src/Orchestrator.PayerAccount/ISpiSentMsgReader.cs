namespace Orchestrator.PayerAccount;

/// <summary>Reads rows from the coexistence <c>dbo.SpiSentMsg</c> table.</summary>
public interface ISpiSentMsgReader
{
    /// <summary>Returns the row with the given <c>IdempotentId</c> (PK), or <c>null</c> if none exists.</summary>
    Task<SpiSentMsgRow?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default);
}
