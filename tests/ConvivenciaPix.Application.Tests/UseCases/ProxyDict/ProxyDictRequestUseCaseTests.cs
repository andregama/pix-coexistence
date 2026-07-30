using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.ProxyDict;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.ProxyDict;

public sealed class ProxyDictRequestUseCaseTests
{
    private readonly Mock<IHsmService> _hsmMock = new();
    private readonly Mock<IDictForwarder> _forwarderMock = new();
    private readonly Mock<ISpiMetrics> _metricsMock = new();
    private readonly ProxyDictRequestUseCase _sut;

    private static readonly IReadOnlyDictionary<string, string[]> NoHeaders =
        new Dictionary<string, string[]>();

    public ProxyDictRequestUseCaseTests()
    {
        _sut = new ProxyDictRequestUseCase(
            _hsmMock.Object,
            _forwarderMock.Object,
            _metricsMock.Object,
            NullLogger<ProxyDictRequestUseCase>.Instance);
    }

    private void SetupSign(string signed) =>
        _hsmMock.Setup(h => h.SignDictXmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signed);

    private void SetupForward(DictProxyResponse response) =>
        _forwarderMock.Setup(f => f.SendAsync(It.IsAny<DictProxyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

    private static DictProxyRequest XmlRequest(string body) => new(
        Method: "POST",
        PathAndQuery: "/api/v2/entries",
        Headers: NoHeaders,
        ContentType: "application/xml",
        Body: Encoding.UTF8.GetBytes(body));

    private static DictProxyResponse XmlResponse(string body, string? contentType = "application/xml") => new(
        StatusCode: 200,
        Headers: NoHeaders,
        ContentType: contentType,
        Body: Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task ExecuteAsync_ReSignsRequestBody_BeforeForwarding()
    {
        SetupSign("<signed/>");
        SetupForward(XmlResponse("<resp/>"));

        await _sut.ExecuteAsync(XmlRequest("<req/>"), CancellationToken.None);

        // The request re-sign happens on the original request body...
        _hsmMock.Verify(h => h.SignDictXmlAsync("<req/>", It.IsAny<CancellationToken>()), Times.Once);
        // ...and the forwarded request carries the re-signed body, not the original.
        _forwarderMock.Verify(f => f.SendAsync(
            It.Is<DictProxyRequest>(r => Encoding.UTF8.GetString(r.Body) == "<signed/>"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ReSignsResponseBody_BeforeReturning()
    {
        _hsmMock.SetupSequence(h => h.SignDictXmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<signed-req/>")
            .ReturnsAsync("<signed-resp/>");
        SetupForward(XmlResponse("<resp/>"));

        var result = await _sut.ExecuteAsync(XmlRequest("<req/>"), CancellationToken.None);

        Encoding.UTF8.GetString(result.Body).Should().Be("<signed-resp/>");
        _hsmMock.Verify(h => h.SignDictXmlAsync("<resp/>", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRequestBody_DoesNotSignRequest()
    {
        SetupSign("<signed/>");
        SetupForward(new DictProxyResponse(204, NoHeaders, ContentType: null, Body: Array.Empty<byte>()));

        var request = new DictProxyRequest("GET", "/api/v2/entries/x", NoHeaders, ContentType: null, Body: Array.Empty<byte>());
        await _sut.ExecuteAsync(request, CancellationToken.None);

        // No XML body on the request and no XML body on the response → HSM is never invoked.
        _hsmMock.Verify(h => h.SignDictXmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NonXmlResponse_PassesBodyThroughUntouched()
    {
        SetupSign("<signed/>");
        SetupForward(XmlResponse("{\"ok\":true}", contentType: "application/json"));

        var result = await _sut.ExecuteAsync(XmlRequest("<req/>"), CancellationToken.None);

        // Request is re-signed (XML), but the JSON response is returned verbatim.
        Encoding.UTF8.GetString(result.Body).Should().Be("{\"ok\":true}");
        _hsmMock.Verify(h => h.SignDictXmlAsync("<req/>", It.IsAny<CancellationToken>()), Times.Once);
        _hsmMock.Verify(h => h.SignDictXmlAsync("{\"ok\":true}", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsUpstreamStatusAndLatency()
    {
        SetupSign("<signed/>");
        SetupForward(XmlResponse("<resp/>"));

        await _sut.ExecuteAsync(XmlRequest("<req/>"), CancellationToken.None);

        _metricsMock.Verify(m => m.RecordDictUpstreamStatus(200), Times.Once);
        _metricsMock.Verify(m => m.RecordDictProxyLatency(It.IsAny<double>()), Times.Once);
    }
}
