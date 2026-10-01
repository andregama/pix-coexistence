using ConvivenciaPix.V2.Application.Interfaces;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ConvivenciaPix.V2.Infrastructure.Parsing;

/// <summary>
/// Builds, from a message that carries transactions of both systems, the message one system
/// should receive: the same envelope with only that system's transactions. Everything else —
/// header, message id, the transactions kept — is left exactly as Bacen sent it; the only derived
/// value is the transaction count in the group header. The result must be signed again, since
/// Bacen's signature no longer covers it.
/// </summary>
public sealed class SpiMessageSplitter : IInboundMessageSplitter
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    public byte[] Extract(byte[] xml, IReadOnlyCollection<int> transactionIndexes)
    {
        XDocument document;
        using (var input = new MemoryStream(xml, writable: false))
        using (var reader = XmlReader.Create(input, ReaderSettings))
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

        var root = document.Root ?? throw new InvalidOperationException("The message has no root element.");
        var segments = SpiTransactionSegments.Find(root)
            ?? throw new InvalidOperationException("The message has no transactions to split.");

        var keep = transactionIndexes.ToHashSet();
        if (keep.Count == 0 || keep.Any(index => index < 0 || index >= segments.Count))
            throw new ArgumentException(
                $"Transaction indexes must be within 0..{segments.Count - 1} and not empty.", nameof(transactionIndexes));

        for (var index = 0; index < segments.Count; index++)
        {
            if (keep.Contains(index)) continue;

            // Take the indentation in front of the element with it, so no blank lines are left behind.
            if (segments[index].PreviousNode is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value))
                whitespace.Remove();
            segments[index].Remove();
        }

        // The group header's transaction count is the only total the SPI schemas carry.
        var count = SpiTransactionSegments.Document(root).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NbOfTxs" && e.Parent?.Name.LocalName == "GrpHdr");
        if (count is not null)
            count.Value = keep.Count.ToString(CultureInfo.InvariantCulture);

        using var output = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = document.Declaration is null
        };
        using (var writer = XmlWriter.Create(output, settings))
            document.Save(writer);
        return output.ToArray();
    }
}
