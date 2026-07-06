using ConvivenciaPix.Application.Mappers;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace ConvivenciaPix.Application.Tests.Mappers;

public sealed class CdcSourceTests
{
    private static string Envelope(string table) => JsonSerializer.Serialize(new
    {
        op = "c",
        source = new { table },
        after = new { XmlMsg = "<pacs/>", Problem = (string?)null }
    });

    [Fact]
    public void ExtractTable_ReturnsInboundTable() =>
        CdcSource.ExtractTable(Envelope("SpiRecepApiBacen")).Should().Be(CdcSource.InboundTable);

    [Fact]
    public void ExtractTable_ReturnsOutboundTable() =>
        CdcSource.ExtractTable(Envelope("SpiEnvioApiBacen")).Should().Be(CdcSource.OutboundTable);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractTable_ReturnsNull_ForEmptyOrTombstone(string? payload) =>
        CdcSource.ExtractTable(payload).Should().BeNull();

    [Fact]
    public void ExtractTable_ReturnsNull_WhenSourceBlockMissing() =>
        CdcSource.ExtractTable("""{"after":{"XmlMsg":"<pacs/>"}}""").Should().BeNull();

    [Fact]
    public void ExtractTable_ReturnsNull_WhenTableAbsentFromSource() =>
        CdcSource.ExtractTable("""{"source":{"db":"DB_SYSTEMA","schema":"dbo"}}""").Should().BeNull();
}
