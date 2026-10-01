using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Messages;
using ConvivenciaPix.V2.Domain.Routing;
using ConvivenciaPix.V2.Infrastructure.Persistence;
using ConvivenciaPix.V2.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class InboundPersistenceTests(SqlServerFixture fixture)
{
    private readonly InboundMessageRepository _messages = new(fixture.Db);
    private readonly DeliveryQueue _queue = new(fixture.Db);
    private readonly TxAffinityRepository _affinities = new(fixture.Db);
    private readonly AuditOutboxRepository _outbox = new(fixture.Db);

    private static string NewId(string prefix = "RES") => $"{prefix}-{Guid.NewGuid():N}";

    private static readonly byte[] Signed = [9, 8, 7];

    private static InboundDelivery[] To(params SystemName[] systems) =>
        systems.Select(system => new InboundDelivery(system, Signed)).ToArray();

    private static SpiInboundMsg Message(string resourceId, string targets = "SystemB") =>
        new(resourceId, "M-" + resourceId, "pacs.008", null, null, RouteSources.DocumentRegistry, targets, [1, 2, 3, 0, 255]);

    // Each test drains only its own system-scoped rows, so tests in this collection do not interfere:
    // every test acks what it claims under a unique stream id and asserts on its own resource ids.
    private async Task<List<string>> ClaimAllAsync(SystemName system, string streamId, int max = 1000)
    {
        var items = await _queue.ClaimAsync(system, streamId, max, TimeSpan.FromSeconds(60));
        return items.Select(i => i.PiResourceId).ToList();
    }

    [Fact]
    public async Task Persist_StoresMessageDeliveriesAffinityAndAuditTogether()
    {
        var resourceId = NewId();
        var key = new TxKey(TxKeyKind.E2E, NewId("E2E"));

        var stored = await _messages.PersistAsync(
            Message(resourceId), To(SystemName.SystemB), [new TxBinding(key, SystemName.SystemB)], new AuditEvent("v2.spi.audit.received", resourceId, "{}"));

        stored.Should().BeTrue();
        (await _messages.ExistsAsync(resourceId)).Should().BeTrue();
        (await _affinities.FindAsync(key)).Should().Be(SystemName.SystemB);
        (await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().Be(1);
        (await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.SpiAuditOutbox WHERE MessageKey = '{resourceId}'")).Should().Be(1);
    }

    [Fact]
    public async Task Persist_Redelivery_ReturnsFalseAndAddsNothing()
    {
        var resourceId = NewId();
        await _messages.PersistAsync(Message(resourceId), To(SystemName.SystemB), [], null);

        var again = await _messages.PersistAsync(Message(resourceId, "SystemA"), To(SystemName.SystemA), [], null);

        again.Should().BeFalse();
        (await fixture.ScalarAsync<string>($"SELECT Targets FROM dbo.SpiInboundMsg WHERE PiResourceId = '{resourceId}'")).Should().Be("SystemB");
        (await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().Be(1);
    }

    [Fact]
    public async Task Persist_Broadcast_CreatesOneDeliveryPerSystem()
    {
        var resourceId = NewId();

        await _messages.PersistAsync(Message(resourceId, "SystemA,SystemB"), To(SystemName.SystemA, SystemName.SystemB), [], null);

        (await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().Be(2);
    }

    [Fact]
    public async Task Affinity_FirstWriterWins()
    {
        var key = new TxKey(TxKeyKind.E2E, NewId("E2E"));

        (await _affinities.GetOrAddAsync(key, SystemName.SystemA, "Sent")).Should().Be(SystemName.SystemA);
        (await _affinities.GetOrAddAsync(key, SystemName.SystemB, "Received")).Should().Be(SystemName.SystemA);

        // Same value under another kind is a different key.
        (await _affinities.FindAsync(new TxKey(TxKeyKind.RTR, key.Value))).Should().BeNull();
    }

    [Fact]
    public async Task Claim_DeliversThePayloadByteForByteInArrivalOrder()
    {
        var first = NewId();
        var second = NewId();
        await _messages.PersistAsync(Message(first), To(SystemName.SystemB), [], null);
        await _messages.PersistAsync(Message(second), To(SystemName.SystemB), [], null);

        var items = await _queue.ClaimAsync(SystemName.SystemB, NewId("s")[..20], 1000, TimeSpan.FromSeconds(60));

        var mine = items.Where(i => i.PiResourceId == first || i.PiResourceId == second).ToList();
        mine.Select(i => i.PiResourceId).Should().Equal(first, second);
        mine[0].Payload.Should().Equal(Signed);
    }

    [Fact]
    public async Task EachSystem_ReceivesItsOwnContent_WhileTheOriginalIsKept()
    {
        var resourceId = NewId();
        await _messages.PersistAsync(
            Message(resourceId, "SystemA,SystemB"),
            [new InboundDelivery(SystemName.SystemA, [0xA, 0, 0xA]), new InboundDelivery(SystemName.SystemB, [0xB, 0xB])],
            [], null);

        var forA = (await _queue.ClaimAsync(SystemName.SystemA, NewId("s")[..20], 1000, TimeSpan.FromSeconds(60))).Single(i => i.PiResourceId == resourceId);
        var forB = (await _queue.ClaimAsync(SystemName.SystemB, NewId("s")[..20], 1000, TimeSpan.FromSeconds(60))).Single(i => i.PiResourceId == resourceId);

        forA.Payload.Should().Equal(0xA, 0, 0xA);
        forB.Payload.Should().Equal(0xB, 0xB);
        (await fixture.ScalarAsync<byte[]>($"SELECT Payload FROM dbo.SpiInboundMsg WHERE PiResourceId = '{resourceId}'")).Should().Equal(1, 2, 3, 0, 255);
    }

    [Fact]
    public async Task DeliveryWrittenBeforePayloadsWerePerSystem_FallsBackToTheMessageContent()
    {
        var resourceId = NewId();
        await _messages.PersistAsync(Message(resourceId), To(SystemName.SystemB), [], null);
        await fixture.ExecuteAsync($"UPDATE dbo.SpiInboundDelivery SET Payload = NULL WHERE PiResourceId = '{resourceId}'");

        var item = (await _queue.ClaimAsync(SystemName.SystemB, NewId("s")[..20], 1000, TimeSpan.FromSeconds(60))).Single(i => i.PiResourceId == resourceId);

        item.Payload.Should().Equal(1, 2, 3, 0, 255);
    }

    [Fact]
    public async Task QuarantinedMessage_IsStoredWithNoDelivery()
    {
        var resourceId = NewId();

        (await _messages.PersistAsync(Message(resourceId, targets: ""), [], [], null)).Should().BeTrue();

        (await _messages.ExistsAsync(resourceId)).Should().BeTrue();
        (await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().Be(0);
    }

    [Fact]
    public async Task Claim_OnlySeesTheCallingSystemsMessages()
    {
        var resourceId = NewId();
        await _messages.PersistAsync(Message(resourceId, "SystemA"), To(SystemName.SystemA), [], null);

        (await ClaimAllAsync(SystemName.SystemB, NewId("s")[..20])).Should().NotContain(resourceId);
        (await ClaimAllAsync(SystemName.SystemA, NewId("s")[..20])).Should().Contain(resourceId);
    }

    [Fact]
    public async Task Claim_ParallelStreams_NeverReceiveTheSameMessage()
    {
        var ids = Enumerable.Range(0, 60).Select(_ => NewId()).ToList();
        foreach (var id in ids)
            await _messages.PersistAsync(Message(id), To(SystemName.SystemB), [], null);

        var batches = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            ClaimAllAsync(SystemName.SystemB, $"par-{i}-{Guid.NewGuid():N}"[..20], max: 10)));

        var delivered = batches.SelectMany(b => b).Where(ids.Contains).ToList();
        delivered.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Ack_RemovesTheBatchFromTheQueue_AndIsScopedToStreamAndSystem()
    {
        var resourceId = NewId();
        await _messages.PersistAsync(Message(resourceId), To(SystemName.SystemB), [], null);
        var streamId = NewId("s")[..20];
        (await ClaimAllAsync(SystemName.SystemB, streamId)).Should().Contain(resourceId);

        (await _queue.AckAsync(SystemName.SystemA, streamId)).Should().Be(0, "another system cannot ack this stream");
        (await _queue.AckAsync(SystemName.SystemB, "other-stream")).Should().Be(0);
        (await _queue.AckAsync(SystemName.SystemB, streamId)).Should().BeGreaterThan(0);

        (await fixture.ScalarAsync<DateTime?>(
            $"SELECT AckedAt FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().NotBeNull();
    }

    [Fact]
    public async Task LeaseExpiredWithoutAck_MessageIsDeliveredAgain_AndTheLateAckIsIgnored()
    {
        var resourceId = NewId();
        await _messages.PersistAsync(Message(resourceId), To(SystemName.SystemB), [], null);
        var firstStream = NewId("s")[..20];
        (await ClaimAllAsync(SystemName.SystemB, firstStream)).Should().Contain(resourceId);

        // Still leased: not offered to another stream.
        (await ClaimAllAsync(SystemName.SystemB, NewId("s")[..20])).Should().NotContain(resourceId);

        await fixture.ExecuteAsync(
            $"UPDATE dbo.SpiInboundDelivery SET LeaseUntil = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE PiResourceId = '{resourceId}'");

        var secondStream = NewId("s")[..20];
        (await ClaimAllAsync(SystemName.SystemB, secondStream)).Should().Contain(resourceId);

        await _queue.AckAsync(SystemName.SystemB, firstStream);
        (await fixture.ScalarAsync<DateTime?>(
            $"SELECT AckedAt FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().BeNull();
    }

    [Fact]
    public async Task Cleanup_KeepsOldMessagesThatWereNeverDelivered()
    {
        var delivered = NewId();
        var waiting = NewId();
        await _messages.PersistAsync(Message(delivered), To(SystemName.SystemB), [], null);
        await _messages.PersistAsync(Message(waiting), To(SystemName.SystemB), [], null);
        await fixture.ExecuteAsync($"""
            UPDATE dbo.SpiInboundMsg SET ReceivedAt = DATEADD(DAY, -40, SYSUTCDATETIME()) WHERE PiResourceId IN ('{delivered}', '{waiting}');
            UPDATE dbo.SpiInboundDelivery SET AckedAt = SYSUTCDATETIME() WHERE PiResourceId = '{delivered}';
            """);

        await _messages.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30), 1000);

        (await _messages.ExistsAsync(delivered)).Should().BeFalse();
        (await _messages.ExistsAsync(waiting)).Should().BeTrue();
    }

    [Fact]
    public async Task Outbox_PublishesPendingEventsOnce()
    {
        var key = NewId("AUD");
        await _outbox.AddAsync(new AuditEvent("v2.spi.audit.sent", key, """{"a":1}"""));

        var published = new List<AuditEvent>();
        await _outbox.PublishPendingAsync(10_000, (e, _) => { published.Add(e); return Task.CompletedTask; });
        await _outbox.PublishPendingAsync(10_000, (e, _) => { published.Add(e); return Task.CompletedTask; });

        published.Where(e => e.Key == key).Should().ContainSingle().Which.PayloadJson.Should().Be("""{"a":1}""");
    }

    [Fact]
    public async Task Outbox_PublishFails_EventStaysPending()
    {
        // Drain whatever other tests left so the failing event is the one we publish.
        await _outbox.PublishPendingAsync(10_000, (_, _) => Task.CompletedTask);
        var key = NewId("AUD");
        await _outbox.AddAsync(new AuditEvent("v2.spi.audit.sent", key, "{}"));

        var act = () => _outbox.PublishPendingAsync(10_000, (e, _) =>
            e.Key == key ? throw new InvalidOperationException("kafka down") : Task.CompletedTask);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await fixture.ScalarAsync<DateTime?>($"SELECT PublishedAt FROM dbo.SpiAuditOutbox WHERE MessageKey = '{key}'")).Should().BeNull();
    }
}
