using FluentValidation;

namespace ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;

public sealed class SpiRequestValidator : AbstractValidator<string>
{
    public SpiRequestValidator()
    {
        RuleFor(xml => xml)
            .NotEmpty()
            .WithMessage("SPI XML cannot be empty.");

        RuleFor(xml => xml)
            .Must(xml => xml.Contains("<Document", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Invalid SPI XML: missing <Document> root element.");
        
        // Additional ISO 20022 schema validation could be added here
    }
}
