using ConvivenciaPix.Domain.Entities;
using FluentAssertions;

namespace ConvivenciaPix.Domain.Tests.Entities;

public sealed class SpiDiscrepancyTests
{
    [Fact]
    public void Create_ValidArgs_SetsAllFields()
    {
        var d = SpiDiscrepancy.Create("A-001", "B-001", "Orchestrator", "Amount", "100.00", "99.00");

        d.IdSystemA.Should().Be("A-001");
        d.IdSystemB.Should().Be("B-001");
        d.CorrelationSource.Should().Be("Orchestrator");
        d.Field.Should().Be("Amount");
        d.SystemAValue.Should().Be("100.00");
        d.SystemBValue.Should().Be("99.00");
    }

    [Fact]
    public void Create_NullSystemValues_Allowed()
    {
        var act = () => SpiDiscrepancy.Create("A-001", "B-001", "Heuristic", "PayerId", null, null);
        act.Should().NotThrow();
        var d = act();
        d.SystemAValue.Should().BeNull();
        d.SystemBValue.Should().BeNull();
    }

    [Fact]
    public void Create_AssignsNewGuidId()
    {
        var a = SpiDiscrepancy.Create("A-001", "B-001", "Orchestrator", "Amount", "1", "2");
        var b = SpiDiscrepancy.Create("A-002", "B-002", "Orchestrator", "Amount", "1", "2");
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void Create_SetsDetectedAtWithinOneSecondOfUtcNow()
    {
        var d = SpiDiscrepancy.Create("A-001", "B-001", "Orchestrator", "Amount", "1", "2");
        d.DetectedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }
}
