using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Gateway;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Gateway;

public sealed class HttpSpiGatewayTests : IDisposable
{
    private readonly WireMockServer _spi = WireMockServer.Start();
    private readonly ServiceProvider _services;
    private readonly HttpSpiGateway _gateway;

    public HttpSpiGatewayTests()
    {
        _services = new ServiceCollection()
            .AddHttpClient(HttpSpiGateway.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan).Services
            .BuildServiceProvider();
        _gateway = new HttpSpiGateway(
            _services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(new SpiGatewayOptions { BaseUrl = _spi.Url! }),
            NullLogger<HttpSpiGateway>.Instance);
    }

    public void Dispose()
    {
        _spi.Dispose();
        _services.Dispose();
    }

    [Fact]
    public async Task Post_SendsBodyAndHeadersAndReturnsTheAnswer()
    {
        _spi.Given(Request.Create().WithPath("/api/v1/in/12345678/msgs").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("PI-ResourceId", "RES-1"));

        var response = await _gateway.SendAsync(new SpiGatewayRequest(
            "POST", "/api/v1/in/12345678/msgs", new Dictionary<string, string> { ["PI-Custom"] = "x" },
            "application/xml; charset=utf-8", "<xml>é</xml>"u8.ToArray(), 5));

        response.StatusCode.Should().Be(201);
        response.Header("PI-ResourceId").Should().Be("RES-1");

        var received = _spi.LogEntries.Single().RequestMessage;
        received.BodyAsBytes.Should().Equal("<xml>é</xml>"u8.ToArray());
        received.Headers!["Content-Type"].Single().Should().Be("application/xml; charset=utf-8");
        received.Headers["PI-Custom"].Single().Should().Be("x");
    }

    [Fact]
    public async Task ErrorAnswer_IsReturnedNotThrown()
    {
        _spi.Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/problem+xml").WithBody("<problem/>"));

        var response = await _gateway.SendAsync(new SpiGatewayRequest(
            "POST", "/api/v1/in/12345678/msgs", new Dictionary<string, string>(), "application/xml", "<x/>"u8.ToArray(), 5));

        response.StatusCode.Should().Be(400);
        response.ContentType.Should().StartWith("application/problem+xml");
        response.Body.Should().Equal("<problem/>"u8.ToArray());
    }

    [Fact]
    public async Task NoAnswerWithinTheTimeout_ThrowsGatewayTimeout_AndIsNotRetried()
    {
        _spi.Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithDelay(TimeSpan.FromSeconds(5)));

        var act = () => _gateway.SendAsync(new SpiGatewayRequest(
            "POST", "/api/v1/in/12345678/msgs", new Dictionary<string, string>(), "application/xml", "<x/>"u8.ToArray(), 1));

        await act.Should().ThrowAsync<SpiGatewayTimeoutException>();
        _spi.LogEntries.Count().Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task CallerCancels_PropagatesCancellation()
    {
        _spi.Given(Request.Create().UsingGet())
            .RespondWith(Response.Create().WithStatusCode(204).WithDelay(TimeSpan.FromSeconds(5)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => _gateway.SendAsync(
            new SpiGatewayRequest("GET", "/api/v1/out/12345678/stream/start", new Dictionary<string, string>(), null, [], 30),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
