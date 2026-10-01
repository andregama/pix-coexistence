using ConvivenciaPix.V2.Domain.Routing;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.V2.Domain.Tests;

public sealed class CustomerDocumentTests
{
    [Theory]
    [InlineData("111.222.333-44", "11122233344")]
    [InlineData("12.345.678/0001-95", "12345678000195")]
    [InlineData(" 11122233344 ", "11122233344")]
    [InlineData("12.abc.345/01de-35", "12ABC34501DE35")] // alphanumeric CNPJ keeps its letters
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsPunctuationAndUpperCases(string? input, string expected) =>
        CustomerDocument.Normalize(input).Should().Be(expected);
}
