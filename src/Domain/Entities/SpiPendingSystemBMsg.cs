using ConvivenciaPix.Domain.ValueObjects;

namespace ConvivenciaPix.Domain.Entities;

public sealed class SpiPendingSystemBMsg
{
    public Guid Id { get; private set; }
    public string IdSystemB { get; private set; }
    public string MessageId { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public decimal Amount { get; private set; }
    public string PayerId { get; private set; }
    public string PayeeId { get; private set; }
    public string RawXmlBase64 { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private SpiPendingSystemBMsg()
    {
        IdSystemB = null!; MessageId = null!; PayerId = null!; PayeeId = null!; RawXmlBase64 = null!;
    }

    private SpiPendingSystemBMsg(Guid id, string idSystemB, string messageId,
        DateTimeOffset timestamp, decimal amount, string payerId, string payeeId,
        string rawXmlBase64, DateTime createdAt)
    {
        Id = id;
        IdSystemB = idSystemB;
        MessageId = messageId;
        Timestamp = timestamp;
        Amount = amount;
        PayerId = payerId;
        PayeeId = payeeId;
        RawXmlBase64 = rawXmlBase64;
        CreatedAt = createdAt;
    }

    public static SpiPendingSystemBMsg Create(string idSystemB, string messageId,
        DateTimeOffset timestamp, decimal amount, string payerId, string payeeId, string rawXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idSystemB);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payeeId);

        return new SpiPendingSystemBMsg(
            Guid.NewGuid(), idSystemB, messageId,
            timestamp, amount, payerId, payeeId,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rawXml)),
            DateTime.UtcNow);
    }

    public string DecodeRawXml() =>
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(RawXmlBase64));
}
