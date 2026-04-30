using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ConvivenciaPix.Application.Interfaces;

public interface ICertificateValidator
{
    bool IsValid(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors);
}
