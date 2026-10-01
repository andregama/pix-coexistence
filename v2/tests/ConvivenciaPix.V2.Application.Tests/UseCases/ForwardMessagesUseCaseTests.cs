using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Application.UseCases.ForwardMessages;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConvivenciaPix.V2.Application.Tests.UseCases;

public sealed class ForwardMessagesUseCaseTests
{
    private static readonly byte[] Body = "<xml/>"u8.ToArray();
    private static readonly TxKey Key = new(TxKeyKind.E2E, "E2E-1");

    private readonly Mock<ISpiMessageParser> _parser = new();
    private readonly Mock<ISentRouteRepository> _sentRoutes = new();
    private readonly Mock<ITxAffinityRepository> _affinities = new();
    private readonly Mock<IAuditOutboxRepository> _outbox = new();
    private readonly Mock<ISpiGateway> _gateway = new();
    private readonly Mock<IProxyMetrics> _metrics = new();
    private readonly List<string> _calls = [];

    public ForwardMessagesUseCaseTests()
    {
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>()))
            .Returns(ParsedSpiMessage.Single("pacs.008", "M-1", [], [Key], null, null, null));

        _sentRoutes.Setup(r => r.RegisterAsync(It.IsAny<SpiSentRoute>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("register"))
            .ReturnsAsync((SpiSentRoute route, CancellationToken _) => route.SystemName);

        _gateway.Setup(g => g.SendAsync(It.IsAny<SpiGatewayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("forward"))
            .ReturnsAsync(new SpiGatewayResponse(
                201, new Dictionary<string, string[]> { ["PI-ResourceId"] = ["RES-1"] }, null, []));
    }

    private ForwardMessagesUseCase UseCase() => new(
        _parser.Object, _sentRoutes.Object, _affinities.Object, _outbox.Object, _gateway.Object, _metrics.Object,
        Options.Create(new ForwardOptions { TimeoutSeconds = 7 }), NullLogger<ForwardMessagesUseCase>.Instance);

    private static ForwardRequest Request(SystemName system = SystemName.SystemB) =>
        new(system, "12345678", "application/xml", Body, [Body], new Dictionary<string, string>());

    [Fact]
    public async Task RecordsTheRouteBeforeForwarding()
    {
        await UseCase().ExecuteAsync(Request());

        _calls.Should().Equal("register", "forward");
        _sentRoutes.Verify(r => r.RegisterAsync(
            It.Is<SpiSentRoute>(s => s.MsgId == "M-1" && s.SystemName == SystemName.SystemB && s.TxKey == "E2E-1"),
            It.IsAny<CancellationToken>()));
        _affinities.Verify(a => a.GetOrAddAsync(Key, SystemName.SystemB, "Sent", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ForwardsTheBodyUntouchedAndReturnsTheSpiAnswerVerbatim()
    {
        var answer = "<problem/>"u8.ToArray();
        _gateway.Setup(g => g.SendAsync(It.IsAny<SpiGatewayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpiGatewayResponse(400, new Dictionary<string, string[]>(), "application/problem+xml", answer));

        var result = await UseCase().ExecuteAsync(Request());

        result.StatusCode.Should().Be(400);
        result.ContentType.Should().Be("application/problem+xml");
        result.Body.Should().BeSameAs(answer);
        _gateway.Verify(g => g.SendAsync(
            It.Is<SpiGatewayRequest>(r =>
                r.Method == "POST" && r.PathAndQuery == "/api/v1/in/12345678/msgs"
                && ReferenceEquals(r.Body, Body) && r.ContentType == "application/xml" && r.TimeoutSeconds == 7),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task RouteCannotBeRecorded_AnswersUnavailableWithoutForwarding()
    {
        _sentRoutes.Setup(r => r.RegisterAsync(It.IsAny<SpiSentRoute>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var result = await UseCase().ExecuteAsync(Request());

        result.StatusCode.Should().Be(503);
        _gateway.Verify(g => g.SendAsync(It.IsAny<SpiGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SpiTimesOut_AnswersGatewayTimeout()
    {
        _gateway.Setup(g => g.SendAsync(It.IsAny<SpiGatewayRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpiGatewayTimeoutException("timeout"));

        (await UseCase().ExecuteAsync(Request())).StatusCode.Should().Be(504);
    }

    [Fact]
    public async Task SpiUnreachable_AnswersBadGateway()
    {
        _gateway.Setup(g => g.SendAsync(It.IsAny<SpiGatewayRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        (await UseCase().ExecuteAsync(Request())).StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task StoresTheResourceIdAssignedByTheSpi()
    {
        await UseCase().ExecuteAsync(Request());

        _sentRoutes.Verify(r => r.MarkForwardedAsync("M-1", "RES-1", 201, It.IsAny<CancellationToken>()));
        _outbox.Verify(o => o.AddAsync(It.Is<AuditEvent>(e => e.Key == "M-1"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task OutcomeCannotBeRecorded_StillReturnsTheSpiAnswer()
    {
        _sentRoutes.Setup(r => r.MarkForwardedAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        (await UseCase().ExecuteAsync(Request())).StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task MsgIdAlreadySentByTheOtherSystem_KeepsTheFirstOwnerAndStillForwards()
    {
        _sentRoutes.Setup(r => r.RegisterAsync(It.IsAny<SpiSentRoute>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemName.SystemA);

        var result = await UseCase().ExecuteAsync(Request(SystemName.SystemB));

        result.StatusCode.Should().Be(201);
        _metrics.Verify(m => m.SentConflict(SystemName.SystemB, "pacs.008"));
        _affinities.Verify(
            a => a.GetOrAddAsync(It.IsAny<TxKey>(), It.IsAny<SystemName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EchoRequest_RecordsTheContentHash()
    {
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>()))
            .Returns(ParsedSpiMessage.Single("pibr.001", "M-ECHO", [], [], null, null, "ping"));

        await UseCase().ExecuteAsync(Request());

        _sentRoutes.Verify(r => r.RegisterAsync(
            It.Is<SpiSentRoute>(s => s.EchoDataHash == EchoHash.Compute("ping")), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task MessageWithoutId_IsForwardedWithoutARoute()
    {
        _parser.Setup(p => p.Parse(It.IsAny<ReadOnlyMemory<byte>>())).Returns(ParsedSpiMessage.Unparseable);

        var result = await UseCase().ExecuteAsync(Request());

        result.StatusCode.Should().Be(201);
        _sentRoutes.Verify(r => r.RegisterAsync(It.IsAny<SpiSentRoute>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
