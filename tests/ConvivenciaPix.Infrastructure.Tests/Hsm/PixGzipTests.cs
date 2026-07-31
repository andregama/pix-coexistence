using ConvivenciaPix.Infrastructure.Hsm;
using FluentAssertions;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Hsm;

public sealed class PixGzipTests
{
    [Fact]
    public void Compress_ProducesGzip_ThatDecompressesToOriginal()
    {
        var original = Encoding.UTF8.GetBytes(new string('x', 5000) + "<CreateEntry/>");

        var compressed = PixGzip.Compress(original);

        compressed.Should().NotBeEmpty();
        // Gzip magic number.
        compressed[0].Should().Be(0x1f);
        compressed[1].Should().Be(0x8b);

        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        output.ToArray().Should().Equal(original);
    }
}
