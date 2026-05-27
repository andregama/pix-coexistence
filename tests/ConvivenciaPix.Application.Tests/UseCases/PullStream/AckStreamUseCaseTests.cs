using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PullStream;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.PullStream;

public sealed class AckStreamUseCaseTests
{
    private readonly Mock<IOutboundStream> _streamMock = new();
    private readonly AckStreamUseCase _sut;

    public AckStreamUseCaseTests()
    {
        _sut = new AckStreamUseCase(_streamMock.Object, NullLogger<AckStreamUseCase>.Instance);
    }

    [Fact]
    public async Task KnownStream_CommitsAndReturnsTrue()
    {
        _streamMock.Setup(s => s.GetAndDeleteStreamAsync("sid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "rid-1", "rid-2" });

        var ok = await _sut.ExecuteAsync("sid", CancellationToken.None);

        ok.Should().BeTrue();
        _streamMock.Verify(s => s.CommitAsync(It.Is<IReadOnlyList<string>>(l => l.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownStream_ReturnsFalse()
    {
        _streamMock.Setup(s => s.GetAndDeleteStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>?)null);

        var ok = await _sut.ExecuteAsync("gone", CancellationToken.None);

        ok.Should().BeFalse();
        _streamMock.Verify(s => s.CommitAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyStream_ReturnsTrueWithoutCommit()
    {
        _streamMock.Setup(s => s.GetAndDeleteStreamAsync("empty", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var ok = await _sut.ExecuteAsync("empty", CancellationToken.None);

        ok.Should().BeTrue();
        _streamMock.Verify(s => s.CommitAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
