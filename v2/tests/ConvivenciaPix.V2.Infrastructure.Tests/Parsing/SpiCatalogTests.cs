using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Application.Routing;
using ConvivenciaPix.V2.Domain.Routing;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Parsing;

/// <summary>
/// The parser and the routing table against the example messages of the Bacen SPI catalog 5.13.1
/// (tests/Catalog). These pin down, per message type, the field that links a message to the one it
/// answers or to its transaction — the facts the routing table is built on.
/// </summary>
public sealed class SpiCatalogTests
{
    private static readonly string CatalogRoot = Path.Combine(AppContext.BaseDirectory, "Catalog", "spi.5.13.1");

    private static readonly SpiMessageParser Parser = new();
    private static readonly RoutingTable Table = new(Options.Create(new RoutingOptions()));

    private static ParsedSpiMessage Example(string relativePath) =>
        Parser.Parse(File.ReadAllBytes(Path.Combine(CatalogRoot, relativePath)));

    public static IEnumerable<object[]> AllExamples() =>
        Directory.GetFiles(CatalogRoot, "*.xml", SearchOption.AllDirectories)
            .Select(file => new object[] { Path.GetRelativePath(CatalogRoot, file) });

    [Fact]
    public void CatalogIsPresent() => AllExamples().Should().HaveCountGreaterThan(70);

    [Theory]
    [MemberData(nameof(AllExamples))]
    public void EveryExample_IsIdentified(string relativePath)
    {
        var parsed = Example(relativePath);

        // Folder "pacs008" holds pacs.008 messages.
        var folder = Path.GetDirectoryName(relativePath)!;
        parsed.MsgType.Should().Be($"{folder[..4]}.{folder[4..]}");
        parsed.MsgId.Should().MatchRegex("^M[0-9A-Z]{8}[a-zA-Z0-9]{23}$");
        parsed.Transactions.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllExamples))]
    public void EveryExample_HasARoutingRuleThatCanDecideIt(string relativePath)
    {
        var parsed = Example(relativePath);
        var rule = Table.For(parsed.MsgType);

        // A rule is only useful if the message carries what one of its resolvers needs — otherwise
        // every message of the type silently falls to Default. Broadcast and Default always decide.
        var usable = rule.Chain.Where(resolver => resolver switch
        {
            RouteSources.SentByOriginalMsgId or RouteSources.SentByPiResourceId => parsed.OriginalMsgRefs.Count > 0,
            RouteSources.TxAffinity => parsed.TxKeys.Count > 0,
            RouteSources.EchoData => parsed.EchoData is not null,
            RouteSources.DocumentRegistry => parsed.Transactions.Any(t =>
                (rule.DocumentParty == DocumentParty.Creditor ? t.CreditorDocument : t.DebtorDocument) is not null),
            _ => true
        }).ToList();

        usable.First().Should().NotBe(RouteSources.Default,
            $"{parsed.MsgType} should be routable by something better than the default (chain: {string.Join(" → ", rule.Chain)})");
    }

    // ---- payments ----------------------------------------------------------------------------

    [Fact]
    public void Pacs008_CarriesTheCreditorDocumentPerTransaction()
    {
        var single = Example("pacs008/pacs.008_CONTA_1_msg.xml");
        single.Transactions.Should().ContainSingle().Which.CreditorDocument.Should().Be("80000000000");
        single.Transactions[0].DebtorDocument.Should().Be("70000000000");

        var batch = Example("pacs008/pacs.008_CONTA_10_msg.xml");
        batch.Transactions.Should().HaveCount(10);
        batch.Transactions.Select(t => t.CreditorDocument).Should().OnlyHaveUniqueItems();
        batch.Transactions.Should().OnlyContain(t => t.TxKeys.Count == 1 && t.TxKeys[0].Kind == TxKeyKind.E2E);
    }

    [Theory]
    [InlineData("pacs002/pacs.002_SPI_1_msg.xml", 1)]
    [InlineData("pacs002/pacs.002_ERRO_1_msg.xml", 1)]
    [InlineData("pacs002/pacs.002_SPI_10_msg.xml", 10)]
    public void Pacs002_NamesNoMessage_OnlyThePaymentItReportsOn(string example, int transactions)
    {
        var parsed = Example(example);

        parsed.OriginalMsgRefs.Should().BeEmpty();
        parsed.Transactions.Should().HaveCount(transactions);
        parsed.Transactions.Should().OnlyContain(t => t.TxKeys.Single().Kind == TxKeyKind.E2E);
    }

    [Fact]
    public void Pacs004_LinksToTheOriginalPaymentAndIntroducesAReturnId()
    {
        var parsed = Example("pacs004/pacs.004_SPI_1_msg.xml");

        parsed.Transactions.Single().TxKeys.Should().Equal(
            new TxKey(TxKeyKind.E2E, "E9999900912341234123412345678900"),
            new TxKey(TxKeyKind.RTR, "D9999900920200120123412345678900"));
        Example("pacs004/pacs.004_SPI_10_msg.xml").Transactions.Should().HaveCount(10);
    }

    // ---- requests and their answers ----------------------------------------------------------

    [Theory]
    [InlineData("camt052/camt.052_msg.xml")]
    [InlineData("camt053/camt.053_SALDO_MOMENTO_msg.xml")]
    [InlineData("camt054/camt.054_det_lancamento_msg.xml")]
    [InlineData("reda016/reda.016_sucesso_msg.xml")]
    [InlineData("admi002/admi.002_SPI_M_msg.xml")]
    public void Answer_NamesTheMessageItAnswers(string example) =>
        // The requests in the catalog (camt.060, reda.014, …) all use this id.
        Example(example).OriginalMsgRefs.Should().Equal("M9999901012345678901234567890123");

    [Fact]
    public void UnsolicitedStatement_NamesNoRequest() =>
        Example("camt053/camt.053_AVISO_REMUNERACAO_msg.xml").OriginalMsgRefs.Should().BeEmpty();

    [Fact]
    public void Admi002_ForAnUnreadableMessage_NamesItsResourceId() =>
        Example("admi002/admi.002_SPI_R_msg.xml").OriginalMsgRefs.Should().Equal("RUEkBcEPZ/0RXw6MnruFHGaLlc9V3j7IN");

    [Fact]
    public void Camt025_NamesTheTrackerMessageAndTheTransaction()
    {
        var receipt = Example("camt025/camt.025_SPI_msg.xml");
        receipt.OriginalMsgRefs.Should().Equal("M66666666dfg4frgh342434234324yyh");
        receipt.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E11111111201907261559y6j6mt9l0pi"));

        // The same field holds a return id when the tracked instruction is a return.
        Example("camt025/camt.025_ERRO_msg.xml").TxKeys.Single().Kind.Should().Be(TxKeyKind.RTR);
    }

    [Theory]
    [InlineData("camt055/camt.055_SOLIC_RECEBEDOR_msg.xml")]
    [InlineData("camt029/camt.029_ACEITA_CANCELAMENTO_msg.xml")]
    public void CancellationMessages_OnlyNameThePayment(string example)
    {
        var parsed = Example(example);

        parsed.OriginalMsgRefs.Should().BeEmpty();
        parsed.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E22222222202409011559y6j6mt9l0pi"));
        parsed.Transactions[0].CreditorDocument.Should().BeNull();
    }

    [Fact]
    public void Echo_RequestAndReplyCarryTheSameContent()
    {
        Example("pibr001/pibr.001_msg.xml").EchoData.Should().Be("Campo livre");
        Example("pibr002/pibr.002_msg.xml").EchoData.Should().Be("Campo livre");
    }

    // ---- Pix Automático ----------------------------------------------------------------------

    [Fact]
    public void Pain009_ReachesThePayersPsp_SoTheDebtorIsOurCustomer()
    {
        var transaction = Example("pain009/pain.009 - solicitação confirmação (1).xml").Transactions.Single();

        transaction.TxKeys.Should().Equal(new TxKey(TxKeyKind.REC, "RR2222222220240429njua7shf40k"));
        transaction.DebtorDocument.Should().Be("23933333316");
        transaction.CreditorDocument.Should().Be("12822222215001");
        // Addressed to 11111111, which is the debtor's agent.
        transaction.OwnSideDocument.Should().Be("23933333316");
    }

    [Theory]
    [InlineData("pain011/pain.011 - cancelamento autorização (1).xml")]
    [InlineData("pain012/pain.012 - aceitação da pain.009 (jornada1-a).xml")]
    [InlineData("pain012/pain.012 - confirmação do recebimento psp rec (jornada1-c).xml")]
    public void MandateMessages_ShareTheRecurrenceId(string example) =>
        Example(example).TxKeys.Should().Equal(new TxKey(TxKeyKind.REC, "RR2222222220240429njua7shf40k"));

    [Fact]
    public void Pain013_BindsThePaymentToItsMandate_AndPain014AnswersByPayment()
    {
        var request = Example("pain013/pain.013_Agendamento_instrução de pagamento periódicos.xml");
        request.Transactions.Single().TxKeys.Should().Equal(
            new TxKey(TxKeyKind.E2E, "E00038166202308051559y6j6mt9l0pi"),
            new TxKey(TxKeyKind.REC, "RR2222222220240429njua7shf40k"));
        request.Transactions[0].DebtorDocument.Should().Be("12822222215");

        var status = Example("pain014/pain.014_Sucesso_msg.xml");
        status.OriginalMsgRefs.Should().BeEmpty("the catalog fills OrgnlMsgId with zeros");
        status.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E00038166202308051559y6j6mt9l0pi"));
    }
}
