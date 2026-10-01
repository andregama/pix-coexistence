using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using FluentAssertions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Parsing;

/// <summary>
/// Splitting the multi-transaction examples of the Bacen catalog. Every result is validated against
/// the catalog schema of its message type: a system must be able to accept a split message exactly
/// as it accepts one from Bacen.
/// </summary>
public sealed class SpiMessageSplitterTests
{
    private static readonly string CatalogRoot = Path.Combine(AppContext.BaseDirectory, "Catalog");

    private readonly SpiMessageSplitter _splitter = new();
    private readonly SpiMessageParser _parser = new();

    private static byte[] Example(string relativePath) =>
        File.ReadAllBytes(Path.Combine(CatalogRoot, "spi.5.13.1", relativePath));

    private ParsedSpiMessage Parse(byte[] xml) => _parser.Parse(xml);

    /// <summary>Schema errors of a message, without positions, so two documents can be compared.</summary>
    private static List<string> SchemaErrors(byte[] xml, string msgType)
    {
        var schemas = new XmlSchemaSet { XmlResolver = null };
        var schemaDirectory = Path.Combine(CatalogRoot, "xsd-5.13.1");
        schemas.Add(null, Directory.GetFiles(schemaDirectory, msgType + ".spi.*.xsd").Single());
        schemas.Add(null, Path.Combine(schemaDirectory, "xmldsig.spi.1.0.xsd"));

        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read()) { }
        return errors;
    }

    public static TheoryData<string, string> MultiTransactionExamples => new()
    {
        { "pacs008/pacs.008_CONTA_10_msg.xml", "pacs.008" },
        { "pacs008/pacs.008_END_10_msg.xml", "pacs.008" },
        { "pacs002/pacs.002_SPI_10_msg.xml", "pacs.002" },
        { "pacs004/pacs.004_SPI_10_msg.xml", "pacs.004" }
    };

    [Theory]
    [MemberData(nameof(MultiTransactionExamples))]
    public void TwoParts_TogetherHoldEveryTransactionExactlyOnce(string example, string msgType)
    {
        var original = Example(example);
        var all = Parse(original).Transactions.Select(t => t.TxKeys).ToList();
        int[] forA = [0, 3, 9];
        int[] forB = [1, 2, 4, 5, 6, 7, 8];

        var partA = Parse(_splitter.Extract(original, forA));
        var partB = Parse(_splitter.Extract(original, forB));

        partA.MsgType.Should().Be(msgType);
        partA.Transactions.Select(t => t.TxKeys).Should().BeEquivalentTo(forA.Select(i => all[i]), o => o.WithStrictOrdering());
        partB.Transactions.Select(t => t.TxKeys).Should().BeEquivalentTo(forB.Select(i => all[i]), o => o.WithStrictOrdering());
    }

    [Theory]
    [MemberData(nameof(MultiTransactionExamples))]
    public void Part_IsAsValidForTheCatalogSchemaAsTheOriginal(string example, string msgType)
    {
        var original = Example(example);
        // The catalog examples are unsigned, so the original has one known error: the empty <Sgntr>.
        var baseline = SchemaErrors(original, msgType);

        var part = _splitter.Extract(original, [2, 7]);

        SchemaErrors(part, msgType).Should().BeEquivalentTo(baseline);
        baseline.Should().HaveCountLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("pacs008/pacs.008_CONTA_10_msg.xml", true)]
    [InlineData("pacs004/pacs.004_SPI_10_msg.xml", true)]
    [InlineData("pacs002/pacs.002_SPI_10_msg.xml", false)] // pacs.002 has no transaction count
    public void Part_UpdatesTheTransactionCount_WhenTheGroupHeaderHasOne(string example, bool hasCount)
    {
        var part = XDocument.Load(new MemoryStream(_splitter.Extract(Example(example), [1, 4, 8])));

        var counts = part.Descendants().Where(e => e.Name.LocalName == "NbOfTxs").Select(e => e.Value).ToList();

        counts.Should().Equal(hasCount ? ["3"] : []);
    }

    [Fact]
    public void Part_KeepsTheHeaderAndTheKeptTransactionsExactlyAsBacenSentThem()
    {
        var original = Example("pacs008/pacs.008_CONTA_10_msg.xml");
        var before = XDocument.Load(new MemoryStream(original));

        var after = XDocument.Load(new MemoryStream(_splitter.Extract(original, [4])));

        XElement Header(XDocument d) => d.Descendants().Single(e => e.Name.LocalName == "AppHdr");
        List<XElement> Transfers(XDocument d) => d.Descendants().Where(e => e.Name.LocalName == "CdtTrfTxInf").ToList();

        XNode.DeepEquals(Header(after), Header(before)).Should().BeTrue();
        Transfers(after).Should().ContainSingle();
        XNode.DeepEquals(Transfers(after)[0], Transfers(before)[4]).Should().BeTrue();
    }

    [Fact]
    public void AllTransactions_LeavesTheContentUnchanged()
    {
        var original = Example("pacs002/pacs.002_SPI_10_msg.xml");

        var everything = _splitter.Extract(original, Enumerable.Range(0, 10).ToArray());

        XNode.DeepEquals(XDocument.Load(new MemoryStream(everything)), XDocument.Load(new MemoryStream(original)))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 10 })]
    [InlineData(new[] { -1 })]
    public void IndexesOutsideTheMessage_AreRefused(int[] indexes)
    {
        var act = () => _splitter.Extract(Example("pacs008/pacs.008_CONTA_10_msg.xml"), indexes);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MessageWithoutTransactions_CannotBeSplit()
    {
        var act = () => _splitter.Extract(Example("admi004/admi.004_msg.xml"), [0]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParserAndSplitter_AgreeOnTheTransactionsOfEveryCatalogExample()
    {
        // Keeping only the first transaction must leave exactly the first transaction the parser saw.
        foreach (var file in Directory.GetFiles(Path.Combine(CatalogRoot, "spi.5.13.1"), "*.xml", SearchOption.AllDirectories))
        {
            var original = File.ReadAllBytes(file);
            var parsed = Parse(original);
            if (parsed.Transactions.Count < 2) continue;

            var first = Parse(_splitter.Extract(original, [0]));

            first.Transactions.Should().ContainSingle(file);
            first.Transactions[0].TxKeys.Should().Equal(parsed.Transactions[0].TxKeys, file);
        }
    }
}
