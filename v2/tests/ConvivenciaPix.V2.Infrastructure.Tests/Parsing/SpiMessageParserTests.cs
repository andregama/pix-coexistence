using ConvivenciaPix.V2.Application.Parsing;
using ConvivenciaPix.V2.Domain.Routing;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using ConvivenciaPix.V2.Tests.Shared;
using FluentAssertions;
using System.Text;
using Xunit;

namespace ConvivenciaPix.V2.Infrastructure.Tests.Parsing;

public sealed class SpiMessageParserTests
{
    private readonly SpiMessageParser _parser = new();

    private ParsedSpiMessage Parse(string xml) => _parser.Parse(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void Pacs008_ExtractsIdTransactionKeyAndBothDocuments()
    {
        var parsed = Parse(SpiXml.Pacs008("M12345678aaaaaaaaaaaaaaaaaaaaaaa", "E1234567820260101120000000000001",
            creditorDocument: "111.222.333-44", debtorDocument: "12.345.678/0001-95"));

        parsed.MsgType.Should().Be("pacs.008");
        parsed.MsgId.Should().Be("M12345678aaaaaaaaaaaaaaaaaaaaaaa");
        parsed.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E1234567820260101120000000000001"));
        parsed.Transactions[0].CreditorDocument.Should().Be("11122233344");
        parsed.Transactions[0].DebtorDocument.Should().Be("12345678000195");
        parsed.OriginalMsgRefs.Should().BeEmpty();
    }

    [Fact]
    public void Pacs008_DocumentUnderOrgId_IsAlsoRead()
    {
        var xml = SpiXml.Envelope("pacs.008", "1.13", "FIToFICstmrCdtTrf",
            "<CdtTrfTxInf><Cdtr><Id><OrgId><Othr><Id>12345678000195</Id></Othr></OrgId></Id></Cdtr></CdtTrfTxInf>",
            "M-1");

        Parse(xml).Transactions[0].CreditorDocument.Should().Be("12345678000195");
    }

    [Fact]
    public void Pacs002_IsLinkedByTheInstructionItReportsOn()
    {
        var parsed = Parse(SpiXml.Pacs002("M00038166bbbbbbbbbbbbbbbbbbbbbbb", "E1234567820260101120000000000001"));

        parsed.MsgType.Should().Be("pacs.002");
        parsed.OriginalMsgRefs.Should().BeEmpty();
        parsed.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E1234567820260101120000000000001"));
        parsed.Transactions[0].CreditorDocument.Should().BeNull();
    }

    [Fact]
    public void Pacs002_ForAReturn_PutsTheReturnIdBeforeTheOriginalPayment()
    {
        var parsed = Parse(SpiXml.Pacs002("M-STS", "D1234567820260101120000000000009", "ACCC",
            originalEndToEndId: "E1234567820260101120000000000001"));

        parsed.TxKeys.Should().Equal(
            new TxKey(TxKeyKind.RTR, "D1234567820260101120000000000009"),
            new TxKey(TxKeyKind.E2E, "E1234567820260101120000000000001"));
    }

    [Fact]
    public void Pacs004_ExtractsOriginalPaymentBeforeItsOwnReturnId()
    {
        var parsed = Parse(SpiXml.Pacs004("M-RET", "D1234567820260101120000000000009", "E1234567820260101120000000000001"));

        parsed.MsgType.Should().Be("pacs.004");
        parsed.TxKeys.Should().Equal(
            new TxKey(TxKeyKind.E2E, "E1234567820260101120000000000001"),
            new TxKey(TxKeyKind.RTR, "D1234567820260101120000000000009"));
    }

    [Fact]
    public void Pibr_ExtractsEchoContentFromRequestAndReply()
    {
        Parse(SpiXml.Pibr001("M-ECHO", "Campo livre")).EchoData.Should().Be("Campo livre");

        var reply = Parse(SpiXml.Pibr002("M-REPLY", "Campo livre"));
        reply.MsgType.Should().Be("pibr.002");
        reply.EchoData.Should().Be("Campo livre");
    }

    [Fact]
    public void Admi002_ExtractsTheRelatedReference()
    {
        var parsed = Parse(SpiXml.Admi002("M-REJ", "M12345678aaaaaaaaaaaaaaaaaaaaaaa"));

        parsed.MsgType.Should().Be("admi.002");
        parsed.OriginalMsgRefs.Should().Equal("M12345678aaaaaaaaaaaaaaaaaaaaaaa");
    }

    [Fact]
    public void OriginalMessageReference_WrappedForm_IsRead()
    {
        // camt.025 names the original message as <OrgnlMsgId><MsgId>…</MsgId></OrgnlMsgId>.
        var xml = SpiXml.Envelope("camt.025", "1.0", "Rct",
            "<RctDtls><OrgnlMsgId><MsgId>M-ORIGINAL</MsgId></OrgnlMsgId><OrgnlPmtId><PrtryId>E2E-INTERNAL</PrtryId></OrgnlPmtId></RctDtls>",
            "M-RCT");

        var parsed = Parse(xml);

        parsed.OriginalMsgRefs.Should().Equal("M-ORIGINAL");
        parsed.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E2E-INTERNAL"));
    }

    [Fact]
    public void OriginalMessageReference_ZerosPlaceholder_IsIgnored()
    {
        // pain.014 in the SPI catalog fills OrgnlMsgId with zeros; the real link is OrgnlEndToEndId.
        var xml = SpiXml.Envelope("pain.014", "1.0", "CdtrPmtActvtnReqStsRpt",
            "<OrgnlGrpInfAndSts><OrgnlMsgId>00000000000000000000000000000000</OrgnlMsgId></OrgnlGrpInfAndSts>"
            + "<OrgnlPmtInfAndSts><TxInfAndSts><OrgnlEndToEndId>E2E-9</OrgnlEndToEndId></TxInfAndSts></OrgnlPmtInfAndSts>",
            "M-STS");

        var parsed = Parse(xml);

        parsed.OriginalMsgRefs.Should().BeEmpty();
        parsed.TxKeys.Should().Equal(new TxKey(TxKeyKind.E2E, "E2E-9"));
    }

    [Theory]
    [InlineData("camt.060", "1.0", "AcctRptgReq")]
    [InlineData("reda.031", "1.0", "Anything")]
    [InlineData("admi.004", "1.3", "SysEvtNtfctn")]
    public void MessageType_IsReadFromTheHeaderForAnyFamily(string family, string version, string element) =>
        Parse(SpiXml.Envelope(family, version, element, "", "M-1")).MsgType.Should().Be(family);

    [Fact]
    public void MessageType_WithoutHeader_FallsBackToNamespaceThenElementName()
    {
        Parse("""<Document xmlns="urn:iso:std:iso:20022:tech:xsd:pacs.002.001.10"><FIToFIPmtStsRpt/></Document>""")
            .MsgType.Should().Be("pacs.002");
        Parse("<Document><PmtRtr/></Document>").MsgType.Should().Be("pacs.004");
    }

    [Theory]
    [InlineData("this is not xml")]
    [InlineData("")]
    [InlineData("<Envelope><unclosed></Envelope>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>")]
    public void MalformedOrHostileXml_NeverThrows(string xml) =>
        Parse(xml).Should().BeSameAs(ParsedSpiMessage.Unparseable);

    [Fact]
    public void WellFormedButUnrecognised_IsUnknownType()
    {
        var parsed = Parse("<Something><Else/></Something>");

        parsed.IsUnknown.Should().BeTrue();
        parsed.MsgId.Should().BeNull();
    }
}
