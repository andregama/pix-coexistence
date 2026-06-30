namespace ConvivenciaPix.Domain.Entities;

public sealed class SpiSentMsg
{
    public string IdempotentId { get; private set; } = null!;
    public string MsgType { get; private set; } = null!;
    public string? MsgIdSystemA { get; private set; }
    public string? MsgIdSystemB { get; private set; }
    public string? XmlMsgSystemA { get; private set; }
    public string? XmlMsgSystemB { get; private set; }
    public string? OriginalMsgIdempotentId { get; private set; }
    public string? SystemAErrorCode { get; private set; }
    public string? SystemBErrorCode { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public bool IsComplete => XmlMsgSystemA is not null && XmlMsgSystemB is not null;

    private SpiSentMsg() { }

    public static SpiSentMsg Create(string idempotentId, string msgType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(msgType);

        return new SpiSentMsg
        {
            IdempotentId = idempotentId,
            MsgType = msgType,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateFromSystemA(string? msgId, string xml, string? errorCode)
    {
        MsgIdSystemA = msgId;
        XmlMsgSystemA = xml;
        SystemAErrorCode = errorCode;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateFromSystemB(string? msgId, string xml, string? errorCode)
    {
        MsgIdSystemB = msgId;
        XmlMsgSystemB = xml;
        SystemBErrorCode = errorCode;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetOriginalMsgIdempotentId(string? originalId)
    {
        OriginalMsgIdempotentId = originalId;
    }
}
