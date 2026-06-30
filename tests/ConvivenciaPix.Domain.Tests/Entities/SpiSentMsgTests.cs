using ConvivenciaPix.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Domain.Tests.Entities;

public sealed class SpiSentMsgTests
{
    [Fact]
    public void Create_ValidArgs_SetsAllFields()
    {
        var before = DateTime.UtcNow;
        var msg = SpiSentMsg.Create("E12345678", "pacs.008");

        msg.IdempotentId.Should().Be("E12345678");
        msg.MsgType.Should().Be("pacs.008");
        msg.XmlMsgSystemA.Should().BeNull();
        msg.XmlMsgSystemB.Should().BeNull();
        msg.IsComplete.Should().BeFalse();
        msg.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankIdempotentId_ThrowsArgumentException(string blank)
    {
        var act = () => SpiSentMsg.Create(blank, "pacs.008");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankMsgType_ThrowsArgumentException(string blank)
    {
        var act = () => SpiSentMsg.Create("E12345678", blank);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateFromSystemA_SetsXmlAndMsgId()
    {
        var msg = SpiSentMsg.Create("E12345678", "pacs.008");
        msg.UpdateFromSystemA("MSG-A", "<xml/>", null);

        msg.MsgIdSystemA.Should().Be("MSG-A");
        msg.XmlMsgSystemA.Should().Be("<xml/>");
        msg.SystemAErrorCode.Should().BeNull();
        msg.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void IsComplete_OnlyBothXmlsSet_ReturnsTrue()
    {
        var msg = SpiSentMsg.Create("E12345678", "pacs.008");
        msg.UpdateFromSystemA("MSG-A", "<xmlA/>", null);
        msg.IsComplete.Should().BeFalse();

        msg.UpdateFromSystemB("MSG-B", "<xmlB/>", null);
        msg.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void UpdateFromSystemA_WithErrorCode_SetsErrorCode()
    {
        var msg = SpiSentMsg.Create("E12345678", "pacs.008");
        msg.UpdateFromSystemA("MSG-A", "<xml/>", "RJCT");
        msg.SystemAErrorCode.Should().Be("RJCT");
    }
}
