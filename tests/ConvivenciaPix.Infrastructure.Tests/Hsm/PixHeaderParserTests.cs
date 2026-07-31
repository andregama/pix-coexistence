using ConvivenciaPix.Infrastructure.Hsm;
using FluentAssertions;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Hsm;

public sealed class PixHeaderParserTests
{
    [Fact]
    public void Parse_SkipsStatusLine_AndExtractsHeadersAndContentType()
    {
        var block = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\nX-Correlation-Id: abc-123\r\n\r\n");

        var (headers, contentType) = PixHeaderParser.Parse(block);

        contentType.Should().Be("application/xml");
        headers.Should().ContainKey("X-Correlation-Id");
        headers["X-Correlation-Id"].Should().ContainSingle().Which.Should().Be("abc-123");
        headers.Should().NotContainKey("HTTP/1.1 200 OK");
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        var (headers, contentType) = PixHeaderParser.Parse(null);

        headers.Should().BeEmpty();
        contentType.Should().BeNull();
    }
}
