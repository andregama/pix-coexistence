using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.UseCases.ProcessInboundMessage;
using ConvivenciaPix.V2.Domain.Routing;
using ConvivenciaPix.V2.Infrastructure.Inbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Inbound;

public sealed class BacenPullServiceTests
{
    private const string Start = "/api/v1/out/12345678/stream/start";

    /// <summary>Scripted SPI: answers each pull from a queue, then idles with 204.</summary>
    private sealed class ScriptedGateway : ISpiGateway
    {
        private readonly ConcurrentQueue<Func<SpiGatewayRequest, SpiGatewayResponse>> _script = new();
        public ConcurrentQueue<(string Method, string Path)> Calls { get; } = new();

        public ScriptedGateway Then(Func<SpiGatewayRequest, SpiGatewayResponse> answer)
        {
            _script.Enqueue(answer);
            return this;
        }

        public async Task<SpiGatewayResponse> SendAsync(SpiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((request.Method, request.PathAndQuery));
            if (_script.TryDequeue(out var answer))
                return answer(request);

            await Task.Delay(20, cancellationToken);
            return NoContent("/api/v1/out/12345678/stream/idle");
        }
    }

    private sealed class RecordingUseCase(Func<string, bool>? fail = null) : IProcessInboundMessageUseCase
    {
        public ConcurrentQueue<(string PiResourceId, string Body)> Processed { get; } = new();

        public Task<RoutingDecision?> ExecuteAsync(string piResourceId, byte[] payload, CancellationToken cancellationToken = default)
        {
            if (fail?.Invoke(piResourceId) == true)
                throw new InvalidOperationException("database down");
            Processed.Enqueue((piResourceId, Encoding.UTF8.GetString(payload)));
            return Task.FromResult<RoutingDecision?>(null);
        }
    }

    private static SpiGatewayResponse NoContent(string next) =>
        new(204, new Dictionary<string, string[]> { ["PI-Pull-Next"] = [next] }, null, []);

    private static SpiGatewayResponse Single(string next, string resourceId, string xml) =>
        new(200, new Dictionary<string, string[]> { ["PI-Pull-Next"] = [next], ["PI-ResourceId"] = [resourceId] },
            "application/xml; charset=utf-8", Encoding.UTF8.GetBytes(xml));

    private static SpiGatewayResponse Multipart(string next, params (string ResourceId, string Xml)[] parts)
    {
        var body = new StringBuilder();
        foreach (var (resourceId, xml) in parts)
            body.Append($"--b\r\nContent-Type: application/xml\r\nPI-ResourceId: {resourceId}\r\n\r\n{xml}\r\n");
        body.Append("--b--\r\n");
        return new SpiGatewayResponse(200, new Dictionary<string, string[]> { ["PI-Pull-Next"] = [next] },
            "multipart/mixed; boundary=b", Encoding.UTF8.GetBytes(body.ToString()));
    }

    private static async Task RunAsync(ISpiGateway gateway, IProcessInboundMessageUseCase useCase, Func<bool> until, bool enabled = true)
    {
        var service = new BacenPullService(
            gateway, useCase,
            Options.Create(new InboundPullOptions { Enabled = enabled, Ispb = "12345678", Streams = 1, ErrorBackoffMs = 100 }),
            NullLogger<BacenPullService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoresEveryMessageOfTheBatch_ThenFollowsPullNext()
    {
        var gateway = new ScriptedGateway()
            .Then(_ => Multipart("/api/v1/out/12345678/stream/s1", ("RES-1", "<a/>"), ("RES-2", "<b/>")))
            .Then(_ => Single("/api/v1/out/12345678/stream/s2", "RES-3", "<c/>"));
        var useCase = new RecordingUseCase();

        await RunAsync(gateway, useCase, () => useCase.Processed.Count >= 3 && gateway.Calls.Count >= 3);

        useCase.Processed.Should().Equal(("RES-1", "<a/>"), ("RES-2", "<b/>"), ("RES-3", "<c/>"));
        gateway.Calls.Take(3).Should().Equal(
            ("GET", Start),
            ("GET", "/api/v1/out/12345678/stream/s1"),
            ("GET", "/api/v1/out/12345678/stream/s2"));
    }

    [Fact]
    public async Task MessageCannotBeStored_BatchIsNotAcknowledged_AndTheStreamRestarts()
    {
        var failures = 1;
        var gateway = new ScriptedGateway()
            .Then(_ => Multipart("/api/v1/out/12345678/stream/s1", ("RES-1", "<a/>"), ("RES-2", "<b/>")))
            // Bacen redelivers the unacknowledged batch on the new stream.
            .Then(_ => Multipart("/api/v1/out/12345678/stream/s9", ("RES-1", "<a/>"), ("RES-2", "<b/>")));
        var useCase = new RecordingUseCase(id => id == "RES-2" && Interlocked.Decrement(ref failures) >= 0);

        await RunAsync(gateway, useCase, () => useCase.Processed.Any(p => p.PiResourceId == "RES-2"));

        var pulls = gateway.Calls.Where(c => c.Method == "GET").Select(c => c.Path).ToList();
        pulls.Take(2).Should().Equal([Start, Start], "the failed batch's PI-Pull-Next must never be followed");
        pulls.Should().NotContain("/api/v1/out/12345678/stream/s1");
        useCase.Processed.Select(p => p.PiResourceId).Should().Contain("RES-2");
    }

    [Fact]
    public async Task UnexpectedStatus_RestartsTheStream()
    {
        var gateway = new ScriptedGateway()
            .Then(_ => new SpiGatewayResponse(503, new Dictionary<string, string[]> { ["PI-Pull-Next"] = ["/ignored"] }, null, []))
            .Then(_ => NoContent("/api/v1/out/12345678/stream/s1"));
        var useCase = new RecordingUseCase();

        await RunAsync(gateway, useCase, () => gateway.Calls.Count >= 3);

        gateway.Calls.Take(3).Select(c => c.Path).Should().Equal(Start, Start, "/api/v1/out/12345678/stream/s1");
    }

    [Fact]
    public async Task OnShutdown_ConfirmsTheLastBatchByClosingTheStream()
    {
        var gateway = new ScriptedGateway().Then(_ => Single("/api/v1/out/12345678/stream/s1", "RES-1", "<a/>"));
        var useCase = new RecordingUseCase();

        await RunAsync(gateway, useCase, () => useCase.Processed.Count >= 1 && gateway.Calls.Count >= 2);

        gateway.Calls.Last().Method.Should().Be("DELETE");
    }

    [Fact]
    public async Task MessageWithoutResourceId_GetsAStableContentDerivedId()
    {
        SpiGatewayResponse Answer(SpiGatewayRequest _) => new(
            200, new Dictionary<string, string[]> { ["PI-Pull-Next"] = ["/api/v1/out/12345678/stream/s1"] },
            "application/xml", "<same/>"u8.ToArray());
        var gateway = new ScriptedGateway().Then(Answer).Then(Answer);
        var useCase = new RecordingUseCase();

        await RunAsync(gateway, useCase, () => useCase.Processed.Count >= 2);

        var ids = useCase.Processed.Select(p => p.PiResourceId).ToList();
        ids[0].Should().StartWith("sha-").And.HaveLength(64);
        ids[1].Should().Be(ids[0]);
    }

    [Fact]
    public async Task Disabled_NeverCallsTheSpi()
    {
        var gateway = new ScriptedGateway();

        var started = DateTime.UtcNow;
        await RunAsync(gateway, new RecordingUseCase(), () => DateTime.UtcNow - started > TimeSpan.FromMilliseconds(300), enabled: false);

        gateway.Calls.Should().BeEmpty();
    }
}
