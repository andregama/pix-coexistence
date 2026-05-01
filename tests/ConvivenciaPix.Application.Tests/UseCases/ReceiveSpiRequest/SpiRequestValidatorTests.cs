using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Application.Tests.UseCases.ReceiveSpiRequest;

public sealed class SpiRequestValidatorTests
{
    private readonly SpiRequestValidator _sut = new();

    [Fact]
    public void Validate_EmptyXml_ReturnsError()
    {
        var result = _sut.Validate(string.Empty);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cannot be empty"));
    }

    [Fact]
    public void Validate_MissingDocumentRoot_ReturnsError()
    {
        var result = _sut.Validate("<InvalidRoot />");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("missing <Document> root element"));
    }

    [Fact]
    public void Validate_ValidXml_ReturnsSuccess()
    {
        var result = _sut.Validate("<Document><MsgId>123</MsgId></Document>");
        result.IsValid.Should().BeTrue();
    }
}
