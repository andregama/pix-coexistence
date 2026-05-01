using ConvivenciaPix.Application.UseCases.ReceiveSpiRequest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Xml.Linq;

namespace ConvivenciaPix.SpiProxyApi.Controllers;

[ApiController]
[Route("api/spi")]
[Authorize]
public sealed class SpiController : ControllerBase
{
    private readonly IReceiveSpiRequestUseCase _useCase;

    public SpiController(IReceiveSpiRequestUseCase useCase)
    {
        _useCase = useCase;
    }

    /// <summary>
    /// Simulates the Bacen SPI endpoint for System B. Accepts a raw SPI XML message,
    /// delegates processing to the use case, and returns the signed response or an error.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SPI response XML message.</returns>
    /// <response code="200">Returns the signed SPI response XML.</response>
    /// <response code="400">If the input XML is invalid.</response>
    /// <response code="429">Too many requests.</response>
    /// <response code="504">If the request times out waiting for System A.</response>
    [HttpPost("messages")]
    [Consumes("application/xml", "text/xml")]
    [Produces("application/xml")]
    [ProducesResponseType(typeof(string), 200)]
    [ProducesResponseType(typeof(string), 400)]
    [ProducesResponseType(typeof(void), 429)]
    [ProducesResponseType(typeof(string), 504)]
    public async Task<IActionResult> PostMessage(CancellationToken cancellationToken)
    {
        string rawXml;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            rawXml = await reader.ReadToEndAsync(cancellationToken);

        var result = await _useCase.ExecuteAsync(rawXml, cancellationToken);

        if (result.IsError)
        {
            var statusCode = result.ErrorCode == "SPI9999" ? 504 : 200; // Bacen errors are often 200 OK with error XML, but timeout is 504 per original code
            return StatusCode(statusCode, Content(BuildErrorXml("RJCT", result.ErrorCode!, result.ErrorReason!), "application/xml"));
        }

        return Content(result.ResponseXml!, "application/xml");
    }

    private static string BuildErrorXml(string transactionStatus, string reasonCode, string additionalInfo) =>
        new XDocument(
            new XElement("Document",
                new XElement("FIToFIPmtStsRpt",
                    new XElement("GrpHdr",
                        new XElement("MsgId", Guid.NewGuid().ToString()),
                        new XElement("CreDtTm", DateTimeOffset.UtcNow.ToString("O"))),
                    new XElement("TxInfAndSts",
                        new XElement("TxSts", transactionStatus),
                        new XElement("StsRsnInf",
                            new XElement("Rsn",
                                new XElement("Cd", reasonCode)),
                            new XElement("AddtlInf", additionalInfo)))))).ToString();
}
