using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Application.UseCases.SystemBDocuments;
using ConvivenciaPix.V2.Domain.Repositories;
using FluentAssertions;
using Moq;
using Xunit;

namespace ConvivenciaPix.V2.Application.Tests.UseCases;

public sealed class SystemBDocumentsServiceTests
{
    private readonly Mock<ISystemBDocumentRepository> _repository = new();
    private readonly Mock<ISystemBDocumentRegistry> _registry = new();

    private SystemBDocumentsService Service() => new(_repository.Object, _registry.Object);

    [Fact]
    public async Task Add_StoresTheEntryAndRefreshesTheRegistry()
    {
        _repository.Setup(r => r.AddAsync("123", "0001", "111.222.333-44", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemBDocumentEntry(1, "123", "0001", "111.222.333-44", DateTime.UtcNow));

        var added = await Service().AddAsync([new NewSystemBDocument(" 123 ", "0001", "111.222.333-44")]);

        added.Should().ContainSingle().Which.Id.Should().Be(1);
        _registry.Verify(r => r.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, "0001", "11122233344")]
    [InlineData("123", " ", "11122233344")]
    [InlineData("123", "0001", null)]
    [InlineData("123", "0001", ".-/")]
    public async Task Add_InvalidEntry_AddsNothing(string? conta, string? agencia, string? documento)
    {
        var entries = new[] { new NewSystemBDocument("1", "1", "11122233344"), new NewSystemBDocument(conta, agencia, documento) };

        var act = () => Service().AddAsync(entries);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Entry 1*");
        _repository.Verify(
            r => r.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Remove_ComparesInNormalizedFormAndRefreshesTheRegistry()
    {
        _repository.Setup(r => r.RemoveAsync("11122233344", It.IsAny<CancellationToken>())).ReturnsAsync(2);

        (await Service().RemoveAsync("111.222.333-44")).Should().Be(2);
        _registry.Verify(r => r.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_ClampsThePageSize()
    {
        await Service().ListAsync("111.222.333-44", skip: -5, take: 100_000);

        _repository.Verify(r => r.ListAsync("11122233344", 0, SystemBDocumentsService.MaxPageSize, It.IsAny<CancellationToken>()));
    }
}
