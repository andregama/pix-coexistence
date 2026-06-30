namespace ConvivenciaPix.Domain.Entities;

public sealed class SpiReceivedMsg
{
    public string IdempotentId { get; private set; } = null!;
    public string MsgType { get; private set; } = null!;
    public string? MsgId { get; private set; }
    public string? XmlMsgSystemA { get; private set; }
    public string? XmlMsgSystemB { get; private set; }
    public string? OriginalMsgIdempotentId { get; private set; }
    public string? SystemAErrorCode { get; private set; }
    public string? SystemBErrorCode { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public bool IsComplete => XmlMsgSystemA is not null && XmlMsgSystemB is not null;

    private SpiReceivedMsg() { }

    public static SpiReceivedMsg CreateFromSystemA(
        string idempotentId, string msgType, string? msgId,
        string xml, string? errorCode, string? originalIdempotentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(msgType);

        return new SpiReceivedMsg
        {
            IdempotentId = idempotentId,
            MsgType = msgType,
            MsgId = msgId,
            XmlMsgSystemA = xml,
            SystemAErrorCode = errorCode,
            OriginalMsgIdempotentId = originalIdempotentId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static SpiReceivedMsg CreateFromSystemB(
        string idempotentId, string msgType, string? msgId,
        string signedXml, string? errorCode, string? originalIdempotentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(msgType);

        return new SpiReceivedMsg
        {
            IdempotentId = idempotentId,
            MsgType = msgType,
            MsgId = msgId,
            XmlMsgSystemB = signedXml,
            SystemBErrorCode = errorCode,
            OriginalMsgIdempotentId = originalIdempotentId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateFromSystemA(string xml, string? errorCode)
    {
        XmlMsgSystemA = xml;
        SystemAErrorCode = errorCode;
        UpdatedAt = DateTime.UtcNow;
        // MsgId is first-wins: only set if not already populated
        if (MsgId is null)
            UpdatedAt = DateTime.UtcNow;
    }

    public void SetMsgIdIfAbsent(string? msgId)
    {
        if (MsgId is null)
            MsgId = msgId;
    }

    public void SetSystemBXml(string signedXml, string? errorCode = null)
    {
        XmlMsgSystemB = signedXml;
        SystemBErrorCode = errorCode;
        UpdatedAt = DateTime.UtcNow;
    }
}
