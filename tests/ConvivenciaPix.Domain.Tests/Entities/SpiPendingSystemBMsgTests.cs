using ConvivenciaPix.Domain.Entities;
using Xunit;
using FluentAssertions;

namespace ConvivenciaPix.Domain.Tests.Entities;

public sealed class SpiPendingSystemBMsgTests
{
    private static readonly DateTimeOffset SampleTimestamp = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidArgs_SetsAllFields()
    {
        var msg = SpiPendingSystemBMsg.Create(
            "B-001", "MSG-001", SampleTimestamp, 100.50m, "PAYER1", "PAYEE1", "<xml/>");

        msg.IdSystemB.Should().Be("B-001");
        msg.MessageId.Should().Be("MSG-001");
        msg.Timestamp.Should().Be(SampleTimestamp);
        msg.Amount.Should().Be(100.50m);
        msg.PayerId.Should().Be("PAYER1");
        msg.PayeeId.Should().Be("PAYEE1");
        msg.Id.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankIdSystemB_ThrowsArgumentException(string blank)
    {
        var act = () => SpiPendingSystemBMsg.Create(blank, "MSG-001", SampleTimestamp, 1m, "P1", "P2", "<x/>");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankMessageId_ThrowsArgumentException(string blank)
    {
        var act = () => SpiPendingSystemBMsg.Create("B-001", blank, SampleTimestamp, 1m, "P1", "P2", "<x/>");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankPayerId_ThrowsArgumentException(string blank)
    {
        var act = () => SpiPendingSystemBMsg.Create("B-001", "MSG-001", SampleTimestamp, 1m, blank, "P2", "<x/>");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankPayeeId_ThrowsArgumentException(string blank)
    {
        var act = () => SpiPendingSystemBMsg.Create("B-001", "MSG-001", SampleTimestamp, 1m, "P1", blank, "<x/>");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_AssignsNewGuidId()
    {
        var a = SpiPendingSystemBMsg.Create("B-001", "MSG-001", SampleTimestamp, 1m, "P1", "P2", "<x/>");
        var b = SpiPendingSystemBMsg.Create("B-002", "MSG-002", SampleTimestamp, 1m, "P1", "P2", "<x/>");
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void DecodeRawXml_ReturnsOriginalXml()
    {
        const string xml = "<Document><MsgId>test-123</MsgId></Document>";
        var msg = SpiPendingSystemBMsg.Create("B-001", "MSG-001", SampleTimestamp, 1m, "P1", "P2", xml);
        msg.DecodeRawXml().Should().Be(xml);
    }
}
