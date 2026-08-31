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
    /// <summary>How the row key was derived (RF-05): "MessageKey" or "DerivedKey". See ISpiXmlParser.GetCorrelationSource.</summary>
    public string? CorrelationSource { get; private set; }
    /// <summary>Outbound-stream resource id assigned when the signed message is enqueued for System B to pull.</summary>
    public string? PiResourceId { get; private set; }
    /// <summary>UTC time System B pulled + acked this message. Null until consumed. See <see cref="MarkConsumed"/>.</summary>
    public DateTime? ConsumedAt { get; private set; }
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

    /// <summary>First-wins: records the correlation-key strategy used to key this row.</summary>
    public void SetCorrelationSource(string? source)
    {
        if (CorrelationSource is null && !string.IsNullOrWhiteSpace(source))
            CorrelationSource = source;
    }

    /// <summary>Records the outbound-stream resource id assigned when the signed XML is enqueued for System B.</summary>
    public void SetPiResourceId(string? piResourceId)
    {
        if (!string.IsNullOrWhiteSpace(piResourceId))
            PiResourceId = piResourceId;
    }

    /// <summary>Marks the message as consumed by System B (pulled + acked). First-wins.</summary>
    public void MarkConsumed()
    {
        if (ConsumedAt is null)
        {
            ConsumedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }
    }
}
