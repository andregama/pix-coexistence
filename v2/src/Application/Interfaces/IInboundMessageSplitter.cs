namespace ConvivenciaPix.V2.Application.Interfaces;

public interface IInboundMessageSplitter
{
    /// <summary>
    /// Returns a copy of the message that carries only the transactions at
    /// <paramref name="transactionIndexes"/> (positions as reported by the parser). The result is
    /// unsigned content: it must be signed before it is delivered.
    /// </summary>
    byte[] Extract(byte[] xml, IReadOnlyCollection<int> transactionIndexes);
}
