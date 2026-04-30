using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.ValueObjects;
using FluentAssertions;

namespace ConvivenciaPix.Domain.Tests.Entities;

public sealed class SpiSentMsgTests
{
    [Fact]
    public void Create_ValidArgs_SetsAllFields()
    {
        var before = DateTime.UtcNow;
        var msg = SpiSentMsg.Create("A-001", "B-001", CorrelationSource.Orchestrator);

        msg.IdSystemA.Should().Be("A-001");
        msg.IdSystemB.Should().Be("B-001");
        msg.CorrelationSource.Should().Be(CorrelationSource.Orchestrator);
        msg.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankIdSystemA_ThrowsArgumentException(string blank)
    {
        var act = () => SpiSentMsg.Create(blank, "B-001", CorrelationSource.Orchestrator);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankIdSystemB_ThrowsArgumentException(string blank)
    {
        var act = () => SpiSentMsg.Create("A-001", blank, CorrelationSource.Orchestrator);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_NullCorrelationSource_ThrowsArgumentNullException()
    {
        var act = () => SpiSentMsg.Create("A-001", "B-001", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_SetsCreatedAtWithinOneSecondOfUtcNow()
    {
        var msg = SpiSentMsg.Create("A-001", "B-001", CorrelationSource.Heuristic);
        msg.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }
}
