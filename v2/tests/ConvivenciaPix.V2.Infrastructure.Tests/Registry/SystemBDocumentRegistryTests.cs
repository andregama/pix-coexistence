using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Infrastructure.Registry;
using ConvivenciaPix.V2.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Registry;

[Collection(SqlServerCollection.Name)]
public sealed class SystemBDocumentRegistryTests(SqlServerFixture fixture)
{
    private readonly SystemBDocumentRepository _repository = new(fixture.Db);

    private static string NewCpf() => Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();

    [Fact]
    public void Contains_BeforeTheFirstLoad_Throws()
    {
        var registry = new SystemBDocumentRegistry(_repository);

        registry.IsLoaded.Should().BeFalse();
        var act = () => registry.Contains("11122233344");
        act.Should().Throw<RegistryUnavailableException>();
    }

    [Fact]
    public async Task Contains_MatchesRegardlessOfPunctuation()
    {
        var cpf = NewCpf();
        var formatted = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";
        await _repository.AddAsync("123", "0001", formatted);
        var registry = new SystemBDocumentRegistry(_repository);

        await registry.RefreshAsync();

        registry.Contains(cpf).Should().BeTrue();
        registry.Contains(formatted).Should().BeTrue();
        registry.Contains(NewCpf()).Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_OnlyChangesOnRefresh()
    {
        var registry = new SystemBDocumentRegistry(_repository);
        await registry.RefreshAsync();
        var cpf = NewCpf();

        await _repository.AddAsync("123", "0001", cpf);
        registry.Contains(cpf).Should().BeFalse();

        await registry.RefreshAsync();
        registry.Contains(cpf).Should().BeTrue();
    }

    [Fact]
    public async Task Repository_AddListRemove_RoundTrips()
    {
        var cpf = NewCpf();
        var formatted = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";

        var added = await _repository.AddAsync("98765", "0001", formatted);
        await _repository.AddAsync("11111", "0002", cpf);

        added.Id.Should().BeGreaterThan(0);
        added.Documento.Should().Be(formatted);
        (await _repository.ListAsync(cpf, 0, 100)).Should().HaveCount(2);
        (await _repository.ListAsync(null, 0, 1)).Should().HaveCount(1);

        (await _repository.RemoveAsync(cpf)).Should().Be(2);
        (await _repository.ListAsync(cpf, 0, 100)).Should().BeEmpty();
    }
}
