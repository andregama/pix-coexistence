using ConvivenciaPix.Application.DTOs;

namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Outbound adapter that forwards a (re-signed) DICT request to the real Bacen DICT API over mTLS,
/// presenting the bank's ICP-Brasil client certificate, and returns the raw response. This is the
/// only component in the coexistence layer that calls a real Bacen API directly.
/// </summary>
public interface IDictForwarder
{
    Task<DictProxyResponse> SendAsync(DictProxyRequest request, CancellationToken cancellationToken = default);
}
