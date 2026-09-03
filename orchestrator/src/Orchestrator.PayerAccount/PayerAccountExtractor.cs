using System.Xml;
using System.Xml.XPath;

namespace Orchestrator.PayerAccount;

/// <summary>
/// Namespace-agnostic (local-name based) XML extractor for Bacen SPI pacs.008 / pacs.004 messages.
/// Works regardless of the message's XML namespace or envelope shape.
/// </summary>
public sealed class PayerAccountExtractor : IPayerAccountExtractor
{
    // pacs.008 debtor account: <DbtrAcct><Id><Othr><Id>account</Id><Issr>branch</Issr></Othr></Id></DbtrAcct>
    private const string AccountXPath =
        "//*[local-name()='DbtrAcct']/*[local-name()='Id']/*[local-name()='Othr']/*[local-name()='Id']";
    private const string BranchXPath =
        "//*[local-name()='DbtrAcct']/*[local-name()='Id']/*[local-name()='Othr']/*[local-name()='Issr']";

    public PayerAccountInfo? ExtractFromPacs008(string pacs008Xml)
    {
        var doc = Load(pacs008Xml);
        var account = SelectText(doc, AccountXPath);
        if (string.IsNullOrEmpty(account))
            return null;

        var branch = SelectText(doc, BranchXPath) ?? string.Empty;
        return new PayerAccountInfo(branch, account);
    }

    public string? ExtractOriginalEndToEndId(string pacs004Xml)
    {
        var doc = Load(pacs004Xml);
        return SelectText(doc, "//*[local-name()='TxInf']/*[local-name()='OrgnlEndToEndId']")
            ?? SelectText(doc, "//*[local-name()='OrgnlEndToEndId']");
    }

    private static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    private static string? SelectText(XmlDocument doc, string xpath)
    {
        try
        {
            var text = doc.SelectSingleNode(xpath)?.InnerText.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (XPathException)
        {
            return null;
        }
    }
}
