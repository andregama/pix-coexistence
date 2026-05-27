using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.Interfaces;
using ConvivenciaPix.Application.UseCases.PullStream;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.PullStream;

public sealed class PullStreamUseCaseTests
{
    private readonly Mock<IOutboundStream> _streamMock = new();
    private readonly PullStreamUseCase _sut;

    public PullStreamUseCaseTests()
    {
        var options = Options.Create(new PullStreamOptions { LongPollSeconds = 0, MaxMessagesPerMultipart = 10 });
        _sut = new PullStreamUseCase(_streamMock.Object, options, NullLogger<PullStreamUseCase>.Instance);
    }

    [Fact]
    public async Task FreshStart_ReturnsMessagesAndPersistsStreamState()
    {
        var msg = new OutboundMessage("rid-1", "<signed/>");
        _streamMock.Setup(s => s.PeekAsync(1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { msg });

        var result = await _sut.ExecuteAsync(streamId: null, multipart: false, ispb: "11111111", CancellationToken.None);

        result.Messages.Should().ContainSingle().Which.PiResourceId.Should().Be("rid-1");
        result.NextStreamId.Should().NotBeNullOrWhiteSpace();
        _streamMock.Verify(
            s => s.SaveStreamAsync(result.NextStreamId, It.Is<IReadOnlyList<string>>(l => l.Single() == "rid-1"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FollowingStreamId_CommitsPreviousBatchThenPeeks()
    {
        _streamMock.Setup(s => s.GetAndDeleteStreamAsync("prev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "rid-prev" });
        _streamMock.Setup(s => s.PeekAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OutboundMessage>());

        var result = await _sut.ExecuteAsync(streamId: "prev", multipart: true, ispb: "11111111", CancellationToken.None);

        _streamMock.Verify(s => s.CommitAsync(It.Is<IReadOnlyList<string>>(l => l.Single() == "rid-prev"), It.IsAny<CancellationToken>()), Times.Once);
        result.Messages.Should().BeEmpty();
        result.NextStreamId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Multipart_RequestsUpToTenMessages()
    {
        _streamMock.Setup(s => s.PeekAsync(10, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OutboundMessage>());

        await _sut.ExecuteAsync(streamId: null, multipart: true, ispb: "x", CancellationToken.None);

        _streamMock.Verify(s => s.PeekAsync(10, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SingleMode_RequestsOneMessage()
    {
        _streamMock.Setup(s => s.PeekAsync(1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OutboundMessage>());

        await _sut.ExecuteAsync(streamId: null, multipart: false, ispb: "x", CancellationToken.None);

        _streamMock.Verify(s => s.PeekAsync(1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
