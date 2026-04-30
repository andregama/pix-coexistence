using ConvivenciaPix.Application.DTOs;
using FluentAssertions;
using System.Text;
using System.Text.Json;

namespace ConvivenciaPix.Application.Tests.DTOs;

public sealed class KafkaEnvelopeTests
{
    [Fact]
    public void JsonRoundTrip_PreservesAllFields()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var original = new KafkaEnvelope(
            MessageId: "msg-123",
            PayloadBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("<xml/>")),
            Timestamp: timestamp,
            CorrelationId: "corr-456");

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<KafkaEnvelope>(json);

        deserialized.Should().NotBeNull();
        deserialized!.MessageId.Should().Be(original.MessageId);
        deserialized.PayloadBase64.Should().Be(original.PayloadBase64);
        deserialized.Timestamp.Should().BeCloseTo(original.Timestamp, TimeSpan.FromMilliseconds(1));
        deserialized.CorrelationId.Should().Be(original.CorrelationId);
    }

    [Fact]
    public void PayloadBase64_EncodeDecodeUtf8_RoundTrip()
    {
        const string xml = "<Document><MsgId>TEST-001</MsgId></Document>";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        var envelope = new KafkaEnvelope(
            MessageId: "msg-1",
            PayloadBase64: encoded,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: null);

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.PayloadBase64));
        decoded.Should().Be(xml);
    }

    [Fact]
    public void Deserialization_MissingOptionalFields_DoesNotThrow()
    {
        const string json = """{"MessageId":"m1","PayloadBase64":"dGVzdA==","Timestamp":"2024-01-01T00:00:00Z"}""";
        var act = () => JsonSerializer.Deserialize<KafkaEnvelope>(json);
        act.Should().NotThrow();
        var result = act();
        result!.CorrelationId.Should().BeNull();
    }
}
