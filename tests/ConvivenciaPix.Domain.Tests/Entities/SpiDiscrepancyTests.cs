using ConvivenciaPix.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Domain.Tests.Entities;

public sealed class SpiDiscrepancyTests
{
    [Fact]
    public void Create_ValidArgs_SetsAllFields()
    {
        var d = SpiDiscrepancy.Create("E12345678", "pacs.008", "Amount", "100.00", "99.00");

        d.IdempotentId.Should().Be("E12345678");
        d.MsgType.Should().Be("pacs.008");
        d.Field.Should().Be("Amount");
        d.SystemAValue.Should().Be("100.00");
        d.SystemBValue.Should().Be("99.00");
    }

    [Fact]
    public void Create_NullSystemValues_Allowed()
    {
        var act = () => SpiDiscrepancy.Create("E12345678", "pacs.008", "PayerId", null, null);
        act.Should().NotThrow();
        var d = act();
        d.SystemAValue.Should().BeNull();
        d.SystemBValue.Should().BeNull();
    }

    [Fact]
    public void Create_AssignsNewGuidId()
    {
        var a = SpiDiscrepancy.Create("E12345678", "pacs.008", "Amount", "1", "2");
        var b = SpiDiscrepancy.Create("E12345679", "pacs.004", "Amount", "1", "2");
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void Create_SetsDetectedAtWithinOneSecondOfUtcNow()
    {
        var d = SpiDiscrepancy.Create("E12345678", "pacs.008", "Amount", "1", "2");
        d.DetectedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }
}
