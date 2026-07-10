using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.GeneratePibr002;

/// <summary>
/// Handles a System-B-originated SPI Echo request (pibr.001): synthesises a pibr.002 reply and
/// hands it to the existing sign-and-deliver pipeline. There is no System A counterpart, so this
/// bypasses correlation entirely.
/// </summary>
public interface IGeneratePibr002UseCase
{
    Task ExecuteAsync(KafkaEnvelope envelope, CancellationToken cancellationToken);
}
