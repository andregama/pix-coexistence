using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Gateway;
using ConvivenciaPix.V2.Infrastructure.Hsm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Gateway;

public sealed class DinamoSpiGatewayTests
{
    private static readonly byte[] Body = "<x/>"u8.ToArray();

    private readonly Mock<IDinamoSdkClient> _sdk = new();

    private DinamoSpiGateway Gateway(int maxConcurrentForwards = 4) => new(
        _sdk.Object,
        Options.Create(new SpiGatewayOptions
        {
            BaseUrl = "https://spi.example/",
            MtlsKeyId = "key",
            MtlsCertId = "cert",
            ServerCertChainId = "chain",
            UseGzip = true,
            VerifyHostName = true,
            MaxConcurrentForwards = maxConcurrentForwards
        }),
        NullLogger<DinamoSpiGateway>.Instance);

    private static SpiGatewayRequest Post(int timeoutSeconds = 30) => new(
        "POST", "/api/v1/in/12345678/msgs", new Dictionary<string, string> { ["Accept"] = "application/xml" },
        "application/xml", Body, timeoutSeconds);

    private void SdkReturns(Func<PixHttpResponse> respond) =>
        _sdk.Setup(s => s.SendPix(
                It.IsAny<PixHttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(respond);

    [Fact]
    public async Task SendsThroughTheHsmWithTheConfiguredIdentityAndDinamoStyleHeaders()
    {
        SdkReturns(() => new PixHttpResponse(
            201, new Dictionary<string, string[]> { ["PI-ResourceId"] = ["RES-1"] }, null, []));

        var response = await Gateway().SendAsync(Post());

        response.StatusCode.Should().Be(201);
        response.Header("pi-resourceid").Should().Be("RES-1");
        _sdk.Verify(s => s.SendPix(
            PixHttpMethod.Post, "key", "cert", "chain", "https://spi.example/api/v1/in/12345678/msgs",
            It.Is<IReadOnlyList<string>>(h => h.SequenceEqual(new[] { "Accept: application/xml", "Content-Type: application/xml" })),
            Body, 30, true, true), Times.Once);
    }

    [Fact]
    public async Task HsmFailure_IsNotRetried()
    {
        SdkReturns(() => throw new InvalidOperationException("hsm error"));

        var act = () => Gateway().SendAsync(Post());

        await act.Should().ThrowAsync<InvalidOperationException>();
        _sdk.Invocations.Should().HaveCount(1);
    }

    [Fact]
    public async Task FailureAfterTheTimeoutElapsed_IsReportedAsGatewayTimeout()
    {
        SdkReturns(() =>
        {
            Thread.Sleep(1200);
            throw new InvalidOperationException("generic sdk failure");
        });

        var act = () => Gateway().SendAsync(Post(timeoutSeconds: 1));

        await act.Should().ThrowAsync<SpiGatewayTimeoutException>();
    }

    [Fact]
    public async Task Forwards_AreLimited_ButPullsAreNot()
    {
        var running = 0;
        var peak = 0;
        SdkReturns(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref running);
            return new PixHttpResponse(201, new Dictionary<string, string[]>(), null, []);
        });
        var gateway = Gateway(maxConcurrentForwards: 2);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => gateway.SendAsync(Post())));
        peak.Should().BeLessThanOrEqualTo(2);

        peak = 0;
        var pull = new SpiGatewayRequest("GET", "/api/v1/out/12345678/stream/start", new Dictionary<string, string>(), null, [], 30, IsPull: true);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gateway.SendAsync(pull)));
        peak.Should().BeGreaterThan(2);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
