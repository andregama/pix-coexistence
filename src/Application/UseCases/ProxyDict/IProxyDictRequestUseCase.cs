using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.UseCases.ProxyDict;

/// <summary>
/// Orchestrates a single DICT proxy exchange: re-sign the inbound request, forward it to the real
/// DICT API, then re-sign the response before it is returned to System B.
/// </summary>
public interface IProxyDictRequestUseCase
{
    Task<DictProxyResponse> ExecuteAsync(DictProxyRequest request, CancellationToken cancellationToken = default);
}
