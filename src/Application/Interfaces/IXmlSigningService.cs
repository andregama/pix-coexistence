using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace ConvivenciaPix.Application.Interfaces;

public interface IXmlSigningService
{
    Task<string> SignAsync(XDocument document, X509Certificate2 certificate);
    Task<bool> VerifyAsync(string signedXml, X509Certificate2 certificate);
}
