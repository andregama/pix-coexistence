using System.Xml.Linq;

namespace ConvivenciaPix.V2.Infrastructure.Parsing;

/// <summary>
/// Locates the transactions inside an SPI message. Shared by the parser (which reads each
/// transaction) and the splitter (which removes some of them), so that "transaction N" means the
/// same element to both.
/// </summary>
internal static class SpiTransactionSegments
{
    public const int MaxTransactions = 500;

    // The element that repeats in each schema of the SPI catalog (maxOccurs="unbounded"), most
    // specific first. Removing one of these leaves a message that is still valid for its schema.
    //   CdtTrfTxInf        pacs.008              TxInf               pacs.004, camt.055
    //   PmtInf             pain.013              Mndt                pain.009
    //   OrgnlPmtInfAndSts  pain.014, camt.029    UndrlygAccptncDtls  pain.012
    //   TxInfAndSts        pacs.002              UndrlygCxlDtls      pain.011
    //   TrckrStsAndTx      trck.002              RctDtls             camt.025
    private static readonly string[] Containers =
    [
        "CdtTrfTxInf", "PmtInf", "OrgnlPmtInfAndSts", "TxInfAndSts", "TxInf", "Mndt",
        "UndrlygAccptncDtls", "UndrlygCxlDtls", "TrckrStsAndTx", "RctDtls"
    ];

    /// <summary>
    /// The transaction elements in document order, or null when the message has none of the known
    /// containers (it is then a single, indivisible unit).
    /// </summary>
    public static List<XElement>? Find(XElement root)
    {
        var document = Document(root);

        foreach (var container in Containers)
        {
            // Outermost only, should a schema ever nest a container inside one of the same name.
            var segments = document.Descendants()
                .Where(e => e.Name.LocalName == container && e.Parent?.Name.LocalName != container)
                .Take(MaxTransactions)
                .ToList();
            if (segments.Count > 0)
                return segments;
        }

        return null;
    }

    public static XElement Document(XElement root) =>
        root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "Document") ?? root;
}
