using ConvivenciaPix.V2.Infrastructure.Parsing;
using FluentAssertions;
using System.Text;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Parsing;

public sealed class SpiMultipartTests
{
    private static (byte[] Body, string ContentType) Multipart(params (string Xml, string? ResourceId)[] parts)
    {
        var body = new StringBuilder();
        foreach (var (xml, resourceId) in parts)
        {
            body.Append("--b1\r\nContent-Type: application/xml; charset=utf-8\r\n");
            if (resourceId is not null) body.Append("PI-ResourceId: ").Append(resourceId).Append("\r\n");
            body.Append("\r\n").Append(xml).Append("\r\n");
        }
        body.Append("--b1--\r\n");
        return (Encoding.UTF8.GetBytes(body.ToString()), "multipart/mixed; boundary=\"b1\"");
    }

    [Fact]
    public async Task ReadsEveryPartWithItsResourceIdAndExactBytes()
    {
        var (body, contentType) = Multipart(("<a>é</a>", "RES-1"), ("<b/>", "RES-2"));

        var parts = await SpiMultipart.ReadPartsAsync(body, contentType);

        parts.Should().HaveCount(2);
        parts![0].PiResourceId.Should().Be("RES-1");
        parts[0].Body.Should().Equal(Encoding.UTF8.GetBytes("<a>é</a>"));
        parts[1].PiResourceId.Should().Be("RES-2");
    }

    [Fact]
    public async Task PartWithoutResourceId_HasNone()
    {
        var (body, contentType) = Multipart(("<a/>", null));

        (await SpiMultipart.ReadPartsAsync(body, contentType))!.Single().PiResourceId.Should().BeNull();
    }

    [Fact]
    public async Task MoreThanTheAllowedParts_IsRefused()
    {
        var (body, contentType) = Multipart(Enumerable.Range(0, 11).Select(i => ($"<m{i}/>", (string?)null)).ToArray());

        (await SpiMultipart.ReadPartsAsync(body, contentType)).Should().BeNull();
    }

    [Theory]
    [InlineData("multipart/mixed")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public async Task MissingBoundary_IsRefused(string? contentType) =>
        (await SpiMultipart.ReadPartsAsync("--b1--"u8.ToArray(), contentType)).Should().BeNull();

    [Theory]
    [InlineData("application/xml", true, false)]
    [InlineData("text/xml; charset=utf-8", true, false)]
    [InlineData("multipart/mixed; boundary=x", false, true)]
    [InlineData("application/json", false, false)]
    [InlineData(null, false, false)]
    public void RecognisesTheSpiMediaTypes(string? contentType, bool xml, bool multipart)
    {
        SpiMultipart.IsXml(contentType).Should().Be(xml);
        SpiMultipart.IsMultipart(contentType).Should().Be(multipart);
    }
}
