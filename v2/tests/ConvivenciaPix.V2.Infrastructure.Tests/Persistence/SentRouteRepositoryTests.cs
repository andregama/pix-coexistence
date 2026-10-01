using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Infrastructure.Persistence;
using ConvivenciaPix.V2.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class SentRouteRepositoryTests(SqlServerFixture fixture)
{
    private readonly SentRouteRepository _repository = new(fixture.Db);

    private static string NewId() => "M" + Guid.NewGuid().ToString("N")[..31];

    private static SpiSentRoute Route(string msgId, SystemName system, string? echoHash = null) =>
        new(msgId, system, echoHash is null ? "pacs.008" : "pibr.001", null, echoHash);

    [Fact]
    public async Task Register_ThenFind_ReturnsTheSender()
    {
        var msgId = NewId();

        (await _repository.RegisterAsync(Route(msgId, SystemName.SystemB))).Should().Be(SystemName.SystemB);
        (await _repository.FindSystemByMsgIdAsync(msgId)).Should().Be(SystemName.SystemB);
        (await _repository.FindSystemByMsgIdAsync(NewId())).Should().BeNull();
    }

    [Fact]
    public async Task Register_SameMsgIdTwice_FirstSenderKeepsIt()
    {
        var msgId = NewId();
        await _repository.RegisterAsync(Route(msgId, SystemName.SystemA));

        (await _repository.RegisterAsync(Route(msgId, SystemName.SystemB))).Should().Be(SystemName.SystemA);
    }

    [Fact]
    public async Task Register_ConcurrentlyFromBothSystems_ExactlyOneOwner()
    {
        var msgId = NewId();

        var owners = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            _repository.RegisterAsync(Route(msgId, i % 2 == 0 ? SystemName.SystemA : SystemName.SystemB))));

        owners.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task MsgIds_DifferingOnlyByCase_AreDifferentMessages()
    {
        var lower = NewId().ToLowerInvariant();
        var upper = lower.ToUpperInvariant();
        await _repository.RegisterAsync(Route(lower, SystemName.SystemA));
        await _repository.RegisterAsync(Route(upper, SystemName.SystemB));

        (await _repository.FindSystemByMsgIdAsync(lower)).Should().Be(SystemName.SystemA);
        (await _repository.FindSystemByMsgIdAsync(upper)).Should().Be(SystemName.SystemB);
    }

    [Fact]
    public async Task MarkForwarded_MakesTheRouteFindableByResourceId()
    {
        var msgId = NewId();
        var resourceId = "RES-" + Guid.NewGuid().ToString("N");
        await _repository.RegisterAsync(Route(msgId, SystemName.SystemB));

        await _repository.MarkForwardedAsync(msgId, resourceId, 201);

        (await _repository.FindSystemByPiResourceIdAsync(resourceId)).Should().Be(SystemName.SystemB);
    }

    [Fact]
    public async Task ClaimEcho_IdenticalContentFromBothSystems_EachReplyGoesToADifferentSender()
    {
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await _repository.RegisterAsync(Route(NewId(), SystemName.SystemA, hash));
        await Task.Delay(20);
        await _repository.RegisterAsync(Route(NewId(), SystemName.SystemB, hash));

        var first = await _repository.ClaimEchoAsync(hash);
        var second = await _repository.ClaimEchoAsync(hash);

        first.Should().Be(SystemName.SystemA, "the oldest unanswered echo is answered first");
        second.Should().Be(SystemName.SystemB);
    }

    [Fact]
    public async Task ClaimEcho_AllAnswered_FallsBackToTheMostRecentSender()
    {
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await _repository.RegisterAsync(Route(NewId(), SystemName.SystemB, hash));
        await _repository.ClaimEchoAsync(hash);

        (await _repository.ClaimEchoAsync(hash)).Should().Be(SystemName.SystemB);
        (await _repository.ClaimEchoAsync(new string('f', 64))).Should().BeNull();
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesOnlyExpiredRoutes()
    {
        var old = NewId();
        var recent = NewId();
        await _repository.RegisterAsync(Route(old, SystemName.SystemA));
        await _repository.RegisterAsync(Route(recent, SystemName.SystemA));
        await fixture.ExecuteAsync($"UPDATE dbo.SpiSentRoute SET CreatedAt = DATEADD(DAY, -40, SYSUTCDATETIME()) WHERE MsgId = '{old}'");

        await _repository.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30), 1000);

        (await _repository.FindSystemByMsgIdAsync(old)).Should().BeNull();
        (await _repository.FindSystemByMsgIdAsync(recent)).Should().Be(SystemName.SystemA);
    }
}
