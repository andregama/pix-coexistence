using ConvivenciaPix.Infrastructure.Parsing;
using FluentAssertions;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Parsing;

public sealed class SpiXmlParserTests
{
    private readonly SpiXmlParser _parser = new();

    private static string BuildPacs008(
        string messageId = "MSG-001",
        string endToEndId = "E2E-001",
        string amount = "1500.00",
        string payerId = "PAYER-BIC",
        string payeeId = "PAYEE-BIC",
        string? timestamp = null)
    {
        timestamp ??= DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"""
            <Document xmlns:head="urn:iso:std:iso:20022:tech:xsd:head.001.001.02">
              <head:AppHdr>
                <head:MsgId>{messageId}</head:MsgId>
              </head:AppHdr>
              <FIToFICstmrCdtTrf>
                <CdtTrfTxInf>
                  <PmtId>
                    <EndToEndId>{endToEndId}</EndToEndId>
                  </PmtId>
                  <IntrBkSttlmAmt>{amount}</IntrBkSttlmAmt>
                  <IntrBkSttlmDt>{timestamp}</IntrBkSttlmDt>
                  <Dbtr>
                    <Nm>{payerId}</Nm>
                  </Dbtr>
                  <Cdtr>
                    <Nm>{payeeId}</Nm>
                  </Cdtr>
                </CdtTrfTxInf>
              </FIToFICstmrCdtTrf>
            </Document>
            """;
    }

    private static string BuildPibr001(
        string frIspb = "99999010",
        string toIspb = "00038166",
        string msgId = "M99999010bfefffd6533b49708ba8101",
        string data = "Campo livre",
        bool includeMsgDefIdr = true)
    {
        var msgDefIdr = includeMsgDefIdr ? "<MsgDefIdr>pibr.001.spi.1.3</MsgDefIdr>" : string.Empty;
        return $"""
            <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.001/1.3">
              <AppHdr>
                <Fr><FIId><FinInstnId><Othr><Id>{frIspb}</Id></Othr></FinInstnId></FIId></Fr>
                <To><FIId><FinInstnId><Othr><Id>{toIspb}</Id></Othr></FinInstnId></FIId></To>
                <BizMsgIdr>{msgId}</BizMsgIdr>
                {msgDefIdr}
                <CreDt>2020-04-07T13:47:22.580Z</CreDt>
                <Sgntr/>
              </AppHdr>
              <Document>
                <EchoReq>
                  <GrpHdr>
                    <MsgId>{msgId}</MsgId>
                    <CreDtTm>2020-04-07T13:47:22.580Z</CreDtTm>
                  </GrpHdr>
                  <EchoTxInf><Data>{data}</Data></EchoTxInf>
                </EchoReq>
              </Document>
            </Envelope>
            """;
    }

    [Fact]
    public void ExtractMessageType_Pibr001_FromMsgDefIdr_ReturnsPibr001()
    {
        _parser.ExtractMessageType(BuildPibr001()).Should().Be("pibr.001");
    }

    [Fact]
    public void ExtractMessageType_Pibr001_FromEchoReqElement_ReturnsPibr001()
    {
        // No MsgDefIdr present -> falls back to the business element under Envelope/Document.
        _parser.ExtractMessageType(BuildPibr001(includeMsgDefIdr: false)).Should().Be("pibr.001");
    }

    [Fact]
    public void ExtractIdempotentId_Pibr001_ReturnsEchoReqMsgId()
    {
        var xml = BuildPibr001(msgId: "M99999010abc00000000000000000001");
        _parser.ExtractIdempotentId(xml, "pibr.001").Should().Be("M99999010abc00000000000000000001");
    }

    [Fact]
    public void ExtractIdempotentId_Pibr001_MissingMsgId_ThrowsInvalidOperationException()
    {
        var xml = "<Envelope xmlns=\"https://www.bcb.gov.br/pi/pibr.001/1.3\"><Document><EchoReq><GrpHdr/></EchoReq></Document></Envelope>";
        var act = () => _parser.ExtractIdempotentId(xml, "pibr.001");
        act.Should().Throw<InvalidOperationException>().WithMessage("*MsgId*");
    }

    [Fact]
    public void ExtractMessageId_FromAppHdr_ReturnsValue()
    {
        var xml = BuildPacs008(messageId: "TEST-MSG-123");
        _parser.ExtractMessageId(xml).Should().Be("TEST-MSG-123");
    }

    [Fact]
    public void ExtractMessageType_FromMsgDefIdr_ReturnsPacs008()
    {
        var xml = """
            <Document xmlns:head="urn:iso:std:iso:20022:tech:xsd:head.001.001.02">
              <head:AppHdr>
                <head:MsgDefIdr>pacs.008.001.08</head:MsgDefIdr>
              </head:AppHdr>
            </Document>
            """;
        _parser.ExtractMessageType(xml).Should().Be("pacs.008");
    }

    [Fact]
    public void ExtractMessageType_FromBusinessElement_ReturnsPacs008()
    {
        var xml = "<Document><FIToFICstmrCdtTrf/></Document>";
        _parser.ExtractMessageType(xml).Should().Be("pacs.008");
    }

    [Fact]
    public void ExtractIdempotentId_Pacs008_ReturnsEndToEndId()
    {
        var xml = BuildPacs008(endToEndId: "E2E-XYZ");
        _parser.ExtractIdempotentId(xml, "pacs.008").Should().Be("E2E-XYZ");
    }

    [Fact]
    public void ExtractAmount_IntrBkSttlmAmt_ReturnsDecimal()
    {
        var xml = BuildPacs008(amount: "2500.75");
        _parser.ExtractAmount(xml).Should().Be(2500.75m);
    }

    [Fact]
    public void ExtractAmount_MissingElement_ReturnsZero()
    {
        var xml = "<Document><FIToFICstmrCdtTrf><CdtTrfTxInf></CdtTrfTxInf></FIToFICstmrCdtTrf></Document>";
        _parser.ExtractAmount(xml).Should().Be(0m);
    }

    [Fact]
    public void ExtractPayerId_FromDbtrNm_ReturnsValue()
    {
        var xml = BuildPacs008(payerId: "BRADESCOBRRJ");
        _parser.ExtractPayerId(xml).Should().Be("BRADESCOBRRJ");
    }

    [Fact]
    public void ExtractPayeeId_FromCdtrNm_ReturnsValue()
    {
        var xml = BuildPacs008(payeeId: "ITAUUNIBRRJ");
        _parser.ExtractPayeeId(xml).Should().Be("ITAUUNIBRRJ");
    }

    [Fact]
    public void ExtractTimestamp_ValidDate_ReturnsDateTimeOffset()
    {
        var ts = new DateTimeOffset(2024, 3, 15, 10, 30, 0, TimeSpan.Zero);
        var xml = BuildPacs008(timestamp: ts.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var result = _parser.ExtractTimestamp(xml);
        result.Should().BeCloseTo(ts, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ExtractTimestamp_MissingElement_ReturnsRecentTime()
    {
        var xml = "<Document><NoTimestampHere/></Document>";
        var result = _parser.ExtractTimestamp(xml);
        result.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ExtractMessageId_MissingMsgId_ThrowsInvalidOperationException()
    {
        var xml = "<Document><NoMsgIdHere/></Document>";
        var act = () => _parser.ExtractMessageId(xml);
        act.Should().Throw<InvalidOperationException>().WithMessage("*MsgId*");
    }

    [Fact]
    public void ExtractIdempotentId_Pacs008_MissingEndToEndId_ThrowsInvalidOperationException()
    {
        var xml = "<Document><FIToFICstmrCdtTrf><CdtTrfTxInf><PmtId/></CdtTrfTxInf></FIToFICstmrCdtTrf></Document>";
        var act = () => _parser.ExtractIdempotentId(xml, "pacs.008");
        act.Should().Throw<InvalidOperationException>().WithMessage("*EndToEndId*");
    }
}
