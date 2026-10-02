using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Routing;
using ConvivenciaPix.V2.Infrastructure.Persistence.InMemory;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Persistence;

/// <summary>
/// The in-memory provider must behave like the SQL repositories it stands in for; these mirror the
/// cases of <see cref="SentRouteRepositoryTests"/> and <see cref="InboundPersistenceTests"/>.
/// </summary>
public sealed class InMemoryStorageTests
{
    private readonly Clock _clock = new();
    private readonly InMemorySentRouteRepository _sentRoutes;
    private readonly InMemoryTxAffinityRepository _affinities;
    private readonly InMemoryInboundMessageRepository _messages;
    private readonly InMemoryDeliveryQueue _queue;
    private readonly InMemoryAuditOutboxRepository _outbox;
    private readonly InMemorySystemBDocumentRepository _documents;

    public InMemoryStorageTests()
    {
        var store = new InMemoryStore(_clock);
        _sentRoutes = new InMemorySentRouteRepository(store);
        _affinities = new InMemoryTxAffinityRepository(store);
        _messages = new InMemoryInboundMessageRepository(store);
        _queue = new InMemoryDeliveryQueue(store);
        _outbox = new InMemoryAuditOutboxRepository(store);
        _documents = new InMemorySystemBDocumentRepository(store);
    }

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static SpiSentRoute Route(string msgId, SystemName system, string? echoHash = null) =>
        new(msgId, system, echoHash is null ? "pacs.008" : "pibr.001", null, echoHash);

    private static SpiInboundMsg Message(string resourceId, string targets = "SystemB") =>
        new(resourceId, "M-" + resourceId, "pacs.008", null, null, RouteSources.DocumentRegistry, targets, [1, 2, 3]);

    private static InboundDelivery[] To(params SystemName[] systems) =>
        systems.Select(system => new InboundDelivery(system, [9, 8, 7])).ToArray();

    private async Task<List<string>> ClaimAsync(SystemName system, string streamId, int max = 100) =>
        (await _queue.ClaimAsync(system, streamId, max, Lease)).Select(item => item.PiResourceId).ToList();

    [Fact]
    public async Task Register_SameMsgIdTwice_FirstSenderKeepsIt()
    {
        (await _sentRoutes.RegisterAsync(Route("M1", SystemName.SystemA))).Should().Be(SystemName.SystemA);
        (await _sentRoutes.RegisterAsync(Route("M1", SystemName.SystemB))).Should().Be(SystemName.SystemA);

        (await _sentRoutes.FindSystemByMsgIdAsync("M1")).Should().Be(SystemName.SystemA);
        (await _sentRoutes.FindSystemByMsgIdAsync("m1")).Should().BeNull("SPI ids are case-sensitive");
    }

    [Fact]
    public async Task MarkForwarded_MakesTheRouteFindableByResourceId()
    {
        await _sentRoutes.RegisterAsync(Route("M1", SystemName.SystemB));

        await _sentRoutes.MarkForwardedAsync("M1", "RES-1", 201);

        (await _sentRoutes.FindSystemByPiResourceIdAsync("RES-1")).Should().Be(SystemName.SystemB);
        (await _sentRoutes.FindSystemByPiResourceIdAsync("RES-2")).Should().BeNull();
    }

    [Fact]
    public async Task ClaimEcho_IdenticalContentFromBothSystems_EachReplyGoesToADifferentSender()
    {
        await _sentRoutes.RegisterAsync(Route("M1", SystemName.SystemA, "hash"));
        await _sentRoutes.RegisterAsync(Route("M2", SystemName.SystemB, "hash"));

        (await _sentRoutes.ClaimEchoAsync("hash")).Should().Be(SystemName.SystemA, "the oldest unanswered echo is answered first");
        (await _sentRoutes.ClaimEchoAsync("hash")).Should().Be(SystemName.SystemB);
        (await _sentRoutes.ClaimEchoAsync("hash")).Should().Be(SystemName.SystemB, "all answered: the most recent sender");
        (await _sentRoutes.ClaimEchoAsync("other")).Should().BeNull();
    }

    [Fact]
    public async Task SentRoutes_DeleteOlderThan_RemovesOnlyExpiredRoutes()
    {
        await _sentRoutes.RegisterAsync(Route("old", SystemName.SystemA));
        await _sentRoutes.MarkForwardedAsync("old", "RES-old", 201);
        _clock.Advance(TimeSpan.FromDays(40));
        await _sentRoutes.RegisterAsync(Route("recent", SystemName.SystemA));

        (await _sentRoutes.DeleteOlderThanAsync(_clock.UtcNow.AddDays(-30), 1000)).Should().Be(1);

        (await _sentRoutes.FindSystemByMsgIdAsync("old")).Should().BeNull();
        (await _sentRoutes.FindSystemByPiResourceIdAsync("RES-old")).Should().BeNull();
        (await _sentRoutes.FindSystemByMsgIdAsync("recent")).Should().Be(SystemName.SystemA);
    }

    [Fact]
    public async Task Affinity_FirstWriterWins()
    {
        var key = new TxKey(TxKeyKind.E2E, "E2E-1");

        (await _affinities.GetOrAddAsync(key, SystemName.SystemA, "Sent")).Should().Be(SystemName.SystemA);
        (await _affinities.GetOrAddAsync(key, SystemName.SystemB, "Received")).Should().Be(SystemName.SystemA);

        // Same value under another kind is a different key.
        (await _affinities.FindAsync(new TxKey(TxKeyKind.RTR, key.Value))).Should().BeNull();
    }

    [Fact]
    public async Task Persist_StoresMessageDeliveriesAffinityAndAuditTogether()
    {
        var key = new TxKey(TxKeyKind.E2E, "E2E-1");

        var stored = await _messages.PersistAsync(
            Message("RES-1"), To(SystemName.SystemB), [new TxBinding(key, SystemName.SystemB)], new AuditEvent("topic", "RES-1", "{}"));

        stored.Should().BeTrue();
        (await _messages.ExistsAsync("RES-1")).Should().BeTrue();
        (await _affinities.FindAsync(key)).Should().Be(SystemName.SystemB);
        (await ClaimAsync(SystemName.SystemB, "s1")).Should().Equal("RES-1");
        (await ClaimAsync(SystemName.SystemA, "s2")).Should().BeEmpty();

        var published = new List<string>();
        await _outbox.PublishPendingAsync(10, (e, _) => { published.Add(e.Key); return Task.CompletedTask; });
        published.Should().Equal("RES-1");
    }

    [Fact]
    public async Task Persist_Redelivery_ReturnsFalseAndAddsNothing()
    {
        await _messages.PersistAsync(Message("RES-1"), To(SystemName.SystemB), [], null);

        (await _messages.PersistAsync(Message("RES-1", "SystemA"), To(SystemName.SystemA), [], null)).Should().BeFalse();

        (await ClaimAsync(SystemName.SystemA, "s1")).Should().BeEmpty();
        (await ClaimAsync(SystemName.SystemB, "s2")).Should().Equal("RES-1");
    }

    [Fact]
    public async Task EachSystem_ReceivesItsOwnContent_InArrivalOrder()
    {
        await _messages.PersistAsync(
            Message("RES-1", "SystemA,SystemB"),
            [new InboundDelivery(SystemName.SystemA, [0xA]), new InboundDelivery(SystemName.SystemB, [0xB, 0xB])],
            [], null);
        await _messages.PersistAsync(Message("RES-2"), To(SystemName.SystemB), [], null);

        var forA = await _queue.ClaimAsync(SystemName.SystemA, "sA", 100, Lease);
        var forB = await _queue.ClaimAsync(SystemName.SystemB, "sB", 100, Lease);

        forA.Single().Payload.Should().Equal(0xA);
        forB.Select(item => item.PiResourceId).Should().Equal("RES-1", "RES-2");
        forB[0].Payload.Should().Equal(0xB, 0xB);
    }

    [Fact]
    public async Task Claim_RespectsMax_AndDoesNotHandALeasedMessageToAnotherStream()
    {
        await _messages.PersistAsync(Message("RES-1"), To(SystemName.SystemB), [], null);
        await _messages.PersistAsync(Message("RES-2"), To(SystemName.SystemB), [], null);

        (await ClaimAsync(SystemName.SystemB, "s1", max: 1)).Should().Equal("RES-1");
        (await ClaimAsync(SystemName.SystemB, "s2")).Should().Equal("RES-2");
        (await ClaimAsync(SystemName.SystemB, "s3")).Should().BeEmpty();
    }

    [Fact]
    public async Task Ack_RemovesTheBatch_AndIsIdempotent()
    {
        await _messages.PersistAsync(Message("RES-1"), To(SystemName.SystemB), [], null);
        await ClaimAsync(SystemName.SystemB, "s1");

        (await _queue.AckAsync(SystemName.SystemA, "s1")).Should().Be(0, "the stream belongs to System B");
        (await _queue.AckAsync(SystemName.SystemB, "s1")).Should().Be(1);
        (await _queue.AckAsync(SystemName.SystemB, "s1")).Should().Be(0);

        _clock.Advance(Lease * 2);
        (await ClaimAsync(SystemName.SystemB, "s2")).Should().BeEmpty();
    }

    [Fact]
    public async Task ExpiredLease_IsDeliveredAgain_AndTheLateAckDoesNotCount()
    {
        await _messages.PersistAsync(Message("RES-1"), To(SystemName.SystemB), [], null);
        await ClaimAsync(SystemName.SystemB, "s1");

        _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        (await ClaimAsync(SystemName.SystemB, "s2")).Should().Equal("RES-1");

        (await _queue.AckAsync(SystemName.SystemB, "s1")).Should().Be(0);
        (await _queue.AckAsync(SystemName.SystemB, "s2")).Should().Be(1);
    }

    [Fact]
    public async Task Inbound_DeleteOlderThan_KeepsMessagesStillWaitingForASystem()
    {
        await _messages.PersistAsync(Message("acked"), To(SystemName.SystemB), [], null);
        await ClaimAsync(SystemName.SystemB, "s1");
        await _queue.AckAsync(SystemName.SystemB, "s1");
        await _messages.PersistAsync(Message("waiting"), To(SystemName.SystemB), [], null);
        await _messages.PersistAsync(Message("quarantined", targets: ""), [], [], null);
        _clock.Advance(TimeSpan.FromDays(40));

        (await _messages.DeleteOlderThanAsync(_clock.UtcNow.AddDays(-30), 1000)).Should().Be(2);

        (await _messages.ExistsAsync("acked")).Should().BeFalse();
        (await _messages.ExistsAsync("quarantined")).Should().BeFalse();
        (await _messages.ExistsAsync("waiting")).Should().BeTrue();
    }

    [Fact]
    public async Task Outbox_KeepsWhatFailedToPublish_ForTheNextAttempt()
    {
        await _outbox.AddAsync(new AuditEvent("topic", "1", "{}"));
        await _outbox.AddAsync(new AuditEvent("topic", "2", "{}"));

        var failing = () => _outbox.PublishPendingAsync(
            10, (e, _) => e.Key == "2" ? throw new InvalidOperationException("down") : Task.CompletedTask);
        await failing.Should().ThrowAsync<InvalidOperationException>();

        var retried = new List<string>();
        (await _outbox.PublishPendingAsync(10, (e, _) => { retried.Add(e.Key); return Task.CompletedTask; })).Should().Be(1);
        retried.Should().Equal("2");
    }

    [Fact]
    public async Task Documents_AreMatchedInNormalizedForm()
    {
        var added = await _documents.AddAsync("12345", "0001", "111.222.333-44");
        await _documents.AddAsync("67890", "0001", "55566677788");

        (await _documents.LoadAllDocumentsAsync()).Should().BeEquivalentTo("11122233344", "55566677788");
        (await _documents.ListAsync("11122233344", 0, 10)).Should().Equal(added);
        (await _documents.ListAsync(null, 1, 10)).Should().ContainSingle().Which.Documento.Should().Be("55566677788");

        (await _documents.RemoveAsync("111222333-44")).Should().Be(1);
        (await _documents.LoadAllDocumentsAsync()).Should().BeEquivalentTo("55566677788");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTime UtcNow => _now.UtcDateTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
