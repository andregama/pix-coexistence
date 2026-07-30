using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Application.UseCases.ProxyDict;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConvivenciaPix.DictProxyApi.Controllers;

/// <summary>
/// Transparent DICT reverse proxy. A single catch-all endpoint forwards any method/path/query to the
/// real DICT API, re-signing the request and response XML bodies through the HSM. All DICT operations
/// (CreateEntry, GetEntry, Claims, Infractions, …) flow through here with no per-operation code.
/// </summary>
[ApiController]
[Authorize]
public sealed class DictProxyController : ControllerBase
{
    private readonly IProxyDictRequestUseCase _useCase;

    public DictProxyController(IProxyDictRequestUseCase useCase)
    {
        _useCase = useCase;
    }

    [Route("{**path}")]
    public async Task<IActionResult> Proxy(CancellationToken cancellationToken)
    {
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, cancellationToken);
            body = ms.ToArray();
        }

        var headers = Request.Headers.ToDictionary(
            h => h.Key,
            h => h.Value.Select(v => v ?? string.Empty).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        var proxyRequest = new DictProxyRequest(
            Method: Request.Method,
            PathAndQuery: Request.Path.Value + Request.QueryString.Value,
            Headers: headers,
            ContentType: Request.ContentType,
            Body: body);

        var proxyResponse = await _useCase.ExecuteAsync(proxyRequest, cancellationToken);

        Response.StatusCode = proxyResponse.StatusCode;
        foreach (var (name, values) in proxyResponse.Headers)
        {
            // Content-Type is applied via Response.ContentType; length is recomputed by the host.
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            Response.Headers[name] = values;
        }

        if (proxyResponse.ContentType is not null)
            Response.ContentType = proxyResponse.ContentType;

        if (proxyResponse.Body.Length > 0)
            await Response.Body.WriteAsync(proxyResponse.Body, cancellationToken);

        return new EmptyResult();
    }
}
