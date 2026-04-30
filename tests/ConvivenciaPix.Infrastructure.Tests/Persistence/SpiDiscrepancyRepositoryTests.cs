using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Infrastructure.Persistence.Repositories;
using ConvivenciaPix.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ConvivenciaPix.Infrastructure.Tests.Persistence;

public sealed class SpiDiscrepancyRepositoryTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SpiDiscrepancyRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAsync_PersistsSingleRow()
    {
        var d = SpiDiscrepancy.Create(Uid(), Uid(), "Orchestrator", "Amount", "100.00", "99.00");
        await using var ctx = _fixture.CreateDbContext();
        var repo = new SpiDiscrepancyRepository(ctx);

        await repo.AddAsync(d, CancellationToken.None);

        var found = await ctx.SpiDiscrepancies.FirstOrDefaultAsync(x => x.Id == d.Id);
        found.Should().NotBeNull();
        found!.Field.Should().Be("Amount");
    }

    [Fact]
    public async Task AddRangeAsync_PersistsAllRows()
    {
        var idA = Uid();
        var idB = Uid();
        var discrepancies = new[]
        {
            SpiDiscrepancy.Create(idA, idB, "Heuristic", "Amount", "1", "2"),
            SpiDiscrepancy.Create(idA, idB, "Heuristic", "PayerId", "P1", "P2"),
            SpiDiscrepancy.Create(idA, idB, "Heuristic", "PayeeId", "Q1", "Q2"),
        };

        await using var ctx = _fixture.CreateDbContext();
        var repo = new SpiDiscrepancyRepository(ctx);
        await repo.AddRangeAsync(discrepancies, CancellationToken.None);

        var rows = await ctx.SpiDiscrepancies
            .Where(x => x.IdSystemA == idA && x.IdSystemB == idB)
            .ToListAsync();

        rows.Should().HaveCount(3);
        rows.Select(r => r.Field).Should().BeEquivalentTo(["Amount", "PayerId", "PayeeId"]);
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..16];
}
