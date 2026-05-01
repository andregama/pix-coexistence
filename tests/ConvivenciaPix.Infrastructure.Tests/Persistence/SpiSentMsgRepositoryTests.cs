using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.ValueObjects;
using ConvivenciaPix.Infrastructure.Persistence.Repositories;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Persistence;

public sealed class SpiSentMsgRepositoryTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SpiSentMsgRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    private SpiSentMsgRepository CreateRepo() => new(_fixture.CreateDbContext());

    [Fact]
    public async Task AddAsync_ThenFindByIdSystemA_ReturnsEntity()
    {
        var msg = SpiSentMsg.Create(Uid(), Uid(), CorrelationSource.Orchestrator);
        var repo = CreateRepo();

        await repo.AddAsync(msg);
        var found = await repo.FindByIdSystemAAsync(msg.IdSystemA);

        found.Should().NotBeNull();
        found!.IdSystemA.Should().Be(msg.IdSystemA);
        found.IdSystemB.Should().Be(msg.IdSystemB);
        found.CorrelationSource.Should().Be(CorrelationSource.Orchestrator);
    }

    [Fact]
    public async Task FindByIdSystemA_UnknownId_ReturnsNull()
    {
        var result = await CreateRepo().FindByIdSystemAAsync(Uid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindByIdSystemB_KnownId_ReturnsEntity()
    {
        var msg = SpiSentMsg.Create(Uid(), Uid(), CorrelationSource.Heuristic);
        await CreateRepo().AddAsync(msg);

        var found = await CreateRepo().FindByIdSystemBAsync(msg.IdSystemB);
        found.Should().NotBeNull();
        found!.IdSystemA.Should().Be(msg.IdSystemA);
    }

    [Fact]
    public async Task ExistsAsync_ExistingPair_ReturnsTrue()
    {
        var msg = SpiSentMsg.Create(Uid(), Uid(), CorrelationSource.Orchestrator);
        await CreateRepo().AddAsync(msg);

        var exists = await CreateRepo().ExistsAsync(msg.IdSystemA, msg.IdSystemB);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_UnknownPair_ReturnsFalse()
    {
        var exists = await CreateRepo().ExistsAsync(Uid(), Uid());
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesStaleRows_LeavesRecentRows()
    {
        // Insert a record and then directly update its CreatedAt to be old
        var staleId = Uid();
        var recentId = Uid();
        var stale = SpiSentMsg.Create(staleId, Uid(), CorrelationSource.Orchestrator);
        var recent = SpiSentMsg.Create(recentId, Uid(), CorrelationSource.Orchestrator);

        await using var ctx = _fixture.CreateDbContext();
        ctx.SpiSentMsgs.Add(stale);
        ctx.SpiSentMsgs.Add(recent);
        await ctx.SaveChangesAsync();

        // Force stale record's CreatedAt to 40 days ago
        await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE SpiSentMsgs SET CreatedAt = {0} WHERE IdSystemA = {1}",
            DateTime.UtcNow.AddDays(-40), staleId);

        var repo = new SpiSentMsgRepository(ctx);
        var deleted = await repo.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));

        deleted.Should().BeGreaterThanOrEqualTo(1);
        (await repo.FindByIdSystemAAsync(staleId)).Should().BeNull();
        (await repo.FindByIdSystemAAsync(recentId)).Should().NotBeNull();
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..16];
}
