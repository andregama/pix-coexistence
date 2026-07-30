using ConvivenciaPix.Infrastructure.Hsm;
using ConvivenciaPix.Infrastructure.Signing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Signing;

public sealed class DinamoHsmServiceTests
{
    private readonly Mock<IDinamoSdkClient> _sdk = new();
    private readonly DinamoOptions _options = new()
    {
        Host = "hsm.local", Port = 4433, UserId = "u", Password = "p",
        CertId = "cert-1", KeyId = "key-1", ChainId = "chain-1", Crl = "crl-1"
    };

    private DinamoHsmService BuildSut() => new(
        _sdk.Object, Options.Create(_options), NullLogger<DinamoHsmService>.Instance);

    [Fact]
    public async Task SignXmlAsync_Connects_SignsPIXWithConfiguredIds_ThenDisconnects()
    {
        var sequence = new List<string>();
        _sdk.Setup(s => s.Connect("hsm.local", 4433, "u", "p")).Callback(() => sequence.Add("connect"));
        _sdk.Setup(s => s.SignPIX("key-1", "cert-1", It.IsAny<byte[]>()))
            .Callback(() => sequence.Add("sign"))
            .Returns(Encoding.UTF8.GetBytes("<signed/>"));
        _sdk.Setup(s => s.Disconnect()).Callback(() => sequence.Add("disconnect"));

        var result = await BuildSut().SignXmlAsync("<unsigned/>", CancellationToken.None);

        result.Should().Be("<signed/>");
        sequence.Should().Equal("connect", "sign", "disconnect");
    }

    [Fact]
    public async Task SignXmlAsync_Disconnects_EvenWhenSignThrows()
    {
        _sdk.Setup(s => s.SignPIX(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .Throws(new InvalidOperationException("hsm down"));

        var act = () => BuildSut().SignXmlAsync("<unsigned/>", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _sdk.Verify(s => s.Disconnect(), Times.Once);
    }

    [Fact]
    public async Task VerifyXmlAsync_UsesChainAndCrl()
    {
        _sdk.Setup(s => s.VerifyPIX("chain-1", "crl-1", "<signed/>")).Returns(true);

        var ok = await BuildSut().VerifyXmlAsync("<signed/>", CancellationToken.None);

        ok.Should().BeTrue();
        _sdk.Verify(s => s.Disconnect(), Times.Once);
    }

    [Fact]
    public async Task SignDictXmlAsync_UsesSignPIXDict_WithConfiguredIds()
    {
        var sequence = new List<string>();
        _sdk.Setup(s => s.Connect("hsm.local", 4433, "u", "p")).Callback(() => sequence.Add("connect"));
        _sdk.Setup(s => s.SignPIXDict("key-1", "cert-1", It.IsAny<byte[]>()))
            .Callback(() => sequence.Add("sign"))
            .Returns(Encoding.UTF8.GetBytes("<signed-dict/>"));
        _sdk.Setup(s => s.Disconnect()).Callback(() => sequence.Add("disconnect"));

        var result = await BuildSut().SignDictXmlAsync("<unsigned/>", CancellationToken.None);

        result.Should().Be("<signed-dict/>");
        sequence.Should().Equal("connect", "sign", "disconnect");
        // SPI signing must not be used for DICT messages.
        _sdk.Verify(s => s.SignPIX(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task SignDictXmlAsync_StripsExistingSignature_BeforeSigning()
    {
        byte[]? captured = null;
        _sdk.Setup(s => s.SignPIXDict("key-1", "cert-1", It.IsAny<byte[]>()))
            .Callback<string, string, byte[]>((_, _, bytes) => captured = bytes)
            .Returns(Encoding.UTF8.GetBytes("<signed-dict/>"));

        const string signed =
            "<Msg xmlns:d=\"http://www.w3.org/2000/09/xmldsig#\"><d:Signature><d:X/></d:Signature></Msg>";
        await BuildSut().SignDictXmlAsync(signed, CancellationToken.None);

        Encoding.UTF8.GetString(captured!).Should().NotContain("Signature");
    }

    [Fact]
    public async Task VerifyDictXmlAsync_UsesVerifyPIXDict_WithChainAndCrl()
    {
        _sdk.Setup(s => s.VerifyPIXDict("chain-1", "crl-1", It.IsAny<byte[]>())).Returns(true);

        var ok = await BuildSut().VerifyDictXmlAsync("<signed-dict/>", CancellationToken.None);

        ok.Should().BeTrue();
        _sdk.Verify(s => s.Disconnect(), Times.Once);
    }
}
