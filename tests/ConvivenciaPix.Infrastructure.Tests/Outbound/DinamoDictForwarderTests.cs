using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Infrastructure.Hsm;
using ConvivenciaPix.Infrastructure.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Polly;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Outbound;

public sealed class DinamoDictForwarderTests
{
    private readonly Mock<IDinamoSdkClient> _sdk = new();

    private readonly DinamoOptions _dinamo = new()
    {
        Host = "hsm.local", Port = 4433, UserId = "u", Password = "p"
    };

    private readonly DictProxyOptions _options = new()
    {
        BaseUrl = "https://dict.example/",
        TimeoutSeconds = 15,
        MtlsKeyId = "mtls-key",
        MtlsCertId = "mtls-cert",
        ServerCertChainId = "server-chain",
        UseGzip = true,
        VerifyHostName = true
    };

    private DinamoDictForwarder BuildSut() => new(
        _sdk.Object,
        Options.Create(_dinamo),
        Options.Create(_options),
        ResiliencePipeline.Empty,
        NullLogger<DinamoDictForwarder>.Instance);

    private static readonly IReadOnlyDictionary<string, string[]> NoHeaders =
        new Dictionary<string, string[]>();

    private void SetupSendPix(PixHttpResponse response) =>
        _sdk.Setup(s => s.SendPix(
                It.IsAny<PixHttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<byte[]>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(response);

    [Fact]
    public async Task SendAsync_Post_DispatchesSendPix_WithConfiguredLabelsUrlAndFlags()
    {
        SetupSendPix(new PixHttpResponse(201, NoHeaders, "application/xml", Encoding.UTF8.GetBytes("<ok/>")));

        var request = new DictProxyRequest(
            "POST", "/api/v2/entries", NoHeaders, "application/xml", Encoding.UTF8.GetBytes("<req/>"));

        var result = await BuildSut().SendAsync(request);

        result.StatusCode.Should().Be(201);
        Encoding.UTF8.GetString(result.Body).Should().Be("<ok/>");

        _sdk.Verify(s => s.SendPix(
            PixHttpMethod.Post, "mtls-key", "mtls-cert", "server-chain",
            "https://dict.example/api/v2/entries",
            It.IsAny<IReadOnlyList<string>>(),
            It.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "<req/>"),
            15, true, true), Times.Once);
    }

    [Fact]
    public async Task SendAsync_BracketsConnectAndDisconnect_AroundSend()
    {
        var sequence = new List<string>();
        _sdk.Setup(s => s.Connect("hsm.local", 4433, "u", "p")).Callback(() => sequence.Add("connect"));
        _sdk.Setup(s => s.SendPix(
                It.IsAny<PixHttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<byte[]>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback(() => sequence.Add("send"))
            .Returns(new PixHttpResponse(200, NoHeaders, null, []));
        _sdk.Setup(s => s.Disconnect()).Callback(() => sequence.Add("disconnect"));

        await BuildSut().SendAsync(new DictProxyRequest(
            "GET", "/api/v2/entries/x", NoHeaders, null, []));

        sequence.Should().Equal("connect", "send", "disconnect");
    }

    [Fact]
    public async Task SendAsync_FormatsHeaders_ExcludingHopByHop_IncludingContentType()
    {
        IReadOnlyList<string>? captured = null;
        _sdk.Setup(s => s.SendPix(
                It.IsAny<PixHttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<byte[]>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<PixHttpMethod, string, string, string, string, IReadOnlyList<string>, byte[], int, bool, bool>(
                (_, _, _, _, _, headers, _, _, _, _) => captured = headers)
            .Returns(new PixHttpResponse(200, NoHeaders, null, []));

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = ["Bearer abc"],
            ["Host"] = ["dict.example"],            // hop-by-hop → excluded
            ["Content-Length"] = ["5"],             // hop-by-hop → excluded
            ["Content-Type"] = ["text/plain"]       // Content-* dropped, re-added from ContentType
        };

        await BuildSut().SendAsync(new DictProxyRequest(
            "POST", "/api/v2/entries", headers, "application/xml", Encoding.UTF8.GetBytes("<req/>")));

        captured.Should().Contain("Authorization: Bearer abc");
        captured.Should().Contain("Content-Type: application/xml");
        captured.Should().NotContain(h => h.StartsWith("Host:"));
        captured.Should().NotContain(h => h.StartsWith("Content-Length:"));
        captured.Should().NotContain("Content-Type: text/plain");
    }

    [Fact]
    public async Task SendAsync_MapsResponseHeadersAndContentType()
    {
        var responseHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Correlation-Id"] = ["abc-123"]
        };
        SetupSendPix(new PixHttpResponse(404, responseHeaders, "application/problem+xml", [1, 2, 3]));

        var result = await BuildSut().SendAsync(new DictProxyRequest(
            "GET", "/api/v2/entries/x", NoHeaders, null, []));

        result.StatusCode.Should().Be(404);
        result.ContentType.Should().Be("application/problem+xml");
        result.Headers.Should().ContainKey("X-Correlation-Id");
        result.Body.Should().Equal(1, 2, 3);
    }
}
