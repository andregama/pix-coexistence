using ConvivenciaPix.V2.Application.UseCases.SystemBDocuments;
using ConvivenciaPix.V2.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConvivenciaPix.V2.SpiProxyApi.Controllers;

/// <summary>
/// Administration of the customers assigned to System B (<c>TB_DADOS_CONVIVENCIA</c>). A document on
/// this list has its received transfers routed to System B; everyone else stays on System A.
/// Served only on the admin listener.
/// </summary>
[ApiController]
[Authorize]
[Route("admin/v1/system-b-documents")]
public sealed class SystemBDocumentsController(ISystemBDocumentsService service) : ControllerBase
{
    /// <summary>Lists registered documents, optionally filtered by document (punctuation is ignored).</summary>
    [HttpGet]
    public Task<IReadOnlyList<SystemBDocumentEntry>> List(
        [FromQuery] string? documento, [FromQuery] int skip = 0, [FromQuery] int take = 100,
        CancellationToken cancellationToken = default) =>
        service.ListAsync(documento, skip, take, cancellationToken);

    /// <summary>Assigns one customer to System B.</summary>
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] NewSystemBDocument entry, CancellationToken cancellationToken)
    {
        try
        {
            var added = await service.AddAsync([entry], cancellationToken);
            return StatusCode(StatusCodes.Status201Created, added[0]);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Assigns several customers to System B. Nothing is added if any entry is invalid.</summary>
    [HttpPost("bulk")]
    public async Task<IActionResult> AddBulk([FromBody] List<NewSystemBDocument> entries, CancellationToken cancellationToken)
    {
        try
        {
            var added = await service.AddAsync(entries, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, added);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Returns a customer to System A by removing every row with the document.</summary>
    [HttpDelete("{documento}")]
    public async Task<IActionResult> Remove(string documento, CancellationToken cancellationToken)
    {
        try
        {
            var removed = await service.RemoveAsync(documento, cancellationToken);
            return removed == 0 ? NotFound() : Ok(new { removed });
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
