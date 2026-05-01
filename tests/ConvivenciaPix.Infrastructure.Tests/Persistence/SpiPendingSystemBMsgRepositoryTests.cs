using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Persistence.Repositories;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Persistence;

public sealed class SpiPendingSystemBMsgRepositoryTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SpiPendingSystemBMsgRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    private SpiPendingSystemBMsgRepository CreateRepo() => new(_fixture.CreateDbContext());

    private static readonly DateTimeOffset BaseTimestamp = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static SpiPendingSystemBMsg MakeMsg(
        string? idSystemB = null,
        DateTimeOffset? timestamp = null,
        decimal amount = 100m,
        string payerId = "PAYER1",
        string payeeId = "PAYEE1") =>
        SpiPendingSystemBMsg.Create(
            idSystemB ?? Guid.NewGuid().ToString("N")[..16],
            Guid.NewGuid().ToString("N"),
            timestamp ?? BaseTimestamp,
            amount, payerId, payeeId, "<xml/>");

    [Fact]
    public async Task AddAsync_ThenExistsByIdSystemB_ReturnsTrue()
    {
        var msg = MakeMsg();
        await CreateRepo().AddAsync(msg);
        var exists = await CreateRepo().ExistsByIdSystemBAsync(msg.IdSystemB);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task FindHeuristicMatch_AllFieldsMatch_WithinWindow_ReturnsEntity()
    {
        var msg = MakeMsg(amount: 250.00m, payerId: "DBT1", payeeId: "CDT1", timestamp: BaseTimestamp);
        await CreateRepo().AddAsync(msg);

        var match = await CreateRepo().FindHeuristicMatchAsync(
            BaseTimestamp, 250.00m, "DBT1", "CDT1", windowSeconds: 60);

        match.Should().NotBeNull();
        match!.IdSystemB.Should().Be(msg.IdSystemB);
    }

    [Fact]
    public async Task FindHeuristicMatch_TimestampOutsideWindow_ReturnsNull()
    {
        var msg = MakeMsg(amount: 300m, payerId: "D2", payeeId: "C2", timestamp: BaseTimestamp);
        await CreateRepo().AddAsync(msg);

        // Query with timestamp 2 minutes away, window = 60 seconds
        var match = await CreateRepo().FindHeuristicMatchAsync(
            BaseTimestamp.AddMinutes(2), 300m, "D2", "C2", windowSeconds: 60);

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindHeuristicMatch_WrongAmount_ReturnsNull()
    {
        var msg = MakeMsg(amount: 500m, payerId: "D3", payeeId: "C3", timestamp: BaseTimestamp);
        await CreateRepo().AddAsync(msg);

        var match = await CreateRepo().FindHeuristicMatchAsync(
            BaseTimestamp, 501m, "D3", "C3", windowSeconds: 60);

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindHeuristicMatch_WrongPayerId_ReturnsNull()
    {
        var msg = MakeMsg(amount: 100m, payerId: "D4", payeeId: "C4", timestamp: BaseTimestamp);
        await CreateRepo().AddAsync(msg);

        var match = await CreateRepo().FindHeuristicMatchAsync(
            BaseTimestamp, 100m, "WRONG", "C4", windowSeconds: 60);

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindHeuristicMatch_WrongPayeeId_ReturnsNull()
    {
        var msg = MakeMsg(amount: 100m, payerId: "D5", payeeId: "C5", timestamp: BaseTimestamp);
        await CreateRepo().AddAsync(msg);

        var match = await CreateRepo().FindHeuristicMatchAsync(
            BaseTimestamp, 100m, "D5", "WRONG", windowSeconds: 60);

        match.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        var msg = MakeMsg();
        await CreateRepo().AddAsync(msg);

        await CreateRepo().DeleteAsync(msg.Id);

        var exists = await CreateRepo().ExistsByIdSystemBAsync(msg.IdSystemB);
        exists.Should().BeFalse();
    }
}
