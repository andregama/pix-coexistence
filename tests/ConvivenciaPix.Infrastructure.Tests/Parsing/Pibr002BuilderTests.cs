using ConvivenciaPix.Infrastructure.Parsing;
using FluentAssertions;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Parsing;

public sealed class Pibr002BuilderTests
{
    private const string Pibr002Ns = "https://www.bcb.gov.br/pi/pibr.002/1.3";

    private readonly Pibr002Builder _builder = new();
    private readonly SpiXmlParser _parser = new();

    private static string BuildPibr001(
        string frIspb = "99999010",
        string toIspb = "00038166",
        string msgId = "M99999010bfefffd6533b49708ba8101",
        string data = "Campo livre") => $"""
        <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.001/1.3">
          <AppHdr>
            <Fr><FIId><FinInstnId><Othr><Id>{frIspb}</Id></Othr></FinInstnId></FIId></Fr>
            <To><FIId><FinInstnId><Othr><Id>{toIspb}</Id></Othr></FinInstnId></FIId></To>
            <BizMsgIdr>{msgId}</BizMsgIdr>
            <MsgDefIdr>pibr.001.spi.1.3</MsgDefIdr>
            <CreDt>2020-04-07T13:47:22.580Z</CreDt>
            <Sgntr/>
          </AppHdr>
          <Document>
            <EchoReq>
              <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2020-04-07T13:47:22.580Z</CreDtTm></GrpHdr>
              <EchoTxInf><Data>{data}</Data></EchoTxInf>
            </EchoReq>
          </Document>
        </Envelope>
        """;

    private XElement Build(string reqXml)
    {
        XNamespace ns = Pibr002Ns;
        var doc = XDocument.Parse(_builder.Build(reqXml));
        doc.Root!.Name.Should().Be(ns + "Envelope");
        return doc.Root!;
    }

    [Fact]
    public void Build_ProducesPibr002Envelope_RecognisedByParser()
    {
        var built = _builder.Build(BuildPibr001());
        _parser.ExtractMessageType(built).Should().Be("pibr.002");
    }

    [Fact]
    public void Build_SwapsFrAndTo()
    {
        XNamespace ns = Pibr002Ns;
        var root = Build(BuildPibr001(frIspb: "99999010", toIspb: "00038166"));

        string PartyId(string role) => root
            .Element(ns + "AppHdr")!.Element(ns + role)!
            .Descendants(ns + "Id").Single().Value;

        // The original recipient (To) answers back to the original sender (Fr).
        PartyId("Fr").Should().Be("00038166");
        PartyId("To").Should().Be("99999010");
    }

    [Fact]
    public void Build_EchoesDataAsOrgnlData()
    {
        XNamespace ns = Pibr002Ns;
        var root = Build(BuildPibr001(data: "ping-42"));
        root.Descendants(ns + "OrgnlData").Single().Value.Should().Be("ping-42");
    }

    [Fact]
    public void Build_SetsMsgDefIdrAndMatchingIds()
    {
        XNamespace ns = Pibr002Ns;
        var root = Build(BuildPibr001(toIspb: "00038166"));

        root.Descendants(ns + "MsgDefIdr").Single().Value.Should().Be("pibr.002.spi.1.3");

        var bizMsgIdr = root.Descendants(ns + "BizMsgIdr").Single().Value;
        var grpMsgId = root.Descendants(ns + "MsgId").Single().Value;
        bizMsgIdr.Should().Be(grpMsgId);

        // MsgIdType: [M][0-9A-Z]{8}[a-zA-Z0-9]{23}, sender ISPB = the responder (original To).
        Regex.IsMatch(bizMsgIdr, "^M[0-9A-Z]{8}[a-zA-Z0-9]{23}$").Should().BeTrue();
        bizMsgIdr.Should().StartWith("M00038166");
    }

    [Fact]
    public void Build_MintsAFreshIdPerCall()
    {
        XNamespace ns = Pibr002Ns;
        var a = Build(BuildPibr001()).Descendants(ns + "BizMsgIdr").Single().Value;
        var b = Build(BuildPibr001()).Descendants(ns + "BizMsgIdr").Single().Value;
        a.Should().NotBe(b);
    }

    [Fact]
    public void Build_HandlesTheVerbatimCatalogSample_IncludingXmlDeclaration()
    {
        // The exact pibr.001 example from Catálogo SFN v5.12.1 (exemplos/pibr001/pibr.001_msg.xml),
        // including the XML declaration with standalone="no".
        const string catalogSample = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.001/1.3">
                <AppHdr>
                    <Fr><FIId><FinInstnId><Othr><Id>99999010</Id></Othr></FinInstnId></FIId></Fr>
                    <To><FIId><FinInstnId><Othr><Id>00038166</Id></Othr></FinInstnId></FIId></To>
                    <BizMsgIdr>M99999010bfefffd6533b49708ba8101</BizMsgIdr>
                    <MsgDefIdr>pibr.001.spi.1.3</MsgDefIdr>
                    <CreDt>2020-04-07T13:47:22.580Z</CreDt>
                    <Sgntr/>
                </AppHdr>
                <Document>
                    <EchoReq>
                        <GrpHdr>
                            <MsgId>M99999010bfefffd6533b49708ba8101</MsgId>
                            <CreDtTm>2020-04-07T13:47:22.580Z</CreDtTm>
                        </GrpHdr>
                        <EchoTxInf><Data>Campo livre</Data></EchoTxInf>
                    </EchoReq>
                </Document>
            </Envelope>
            """;

        XNamespace ns = Pibr002Ns;
        var root = Build(catalogSample);

        _parser.ExtractMessageType(root.Document!.ToString()).Should().Be("pibr.002");
        root.Descendants(ns + "OrgnlData").Single().Value.Should().Be("Campo livre");
        root.Element(ns + "AppHdr")!.Element(ns + "Fr")!.Descendants(ns + "Id").Single().Value
            .Should().Be("00038166");
    }
}
