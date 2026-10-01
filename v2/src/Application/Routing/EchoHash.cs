using System.Security.Cryptography;
using System.Text;

namespace ConvivenciaPix.V2.Application.Routing;

public static class EchoHash
{
    /// <summary>SHA-256 (hex) of the echo content, used to pair a pibr.002 with its pibr.001.</summary>
    public static string Compute(string echoData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(echoData.Trim())));
}
