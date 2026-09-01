using ConvivenciaPix.Infrastructure.Parsing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Parsing;

public sealed class InboundResponseTransformerTests
{
    private readonly InboundResponseTransformer _transformer = new(
        Options.Create(new ResponseTransformOptions()),
        NullLogger<InboundResponseTransformer>.Instance);

    private static string Pacs008(string endToEndId, string initiationForm) => $"""
        <Document>
          <FIToFICstmrCdtTrf>
            <CdtTrfTxInf>
              <PmtId><EndToEndId>{endToEndId}</EndToEndId></PmtId>
              <PmtTpInf><LclInstrm><Prtry>{initiationForm}</Prtry></LclInstrm></PmtTpInf>
            </CdtTrfTxInf>
          </FIToFICstmrCdtTrf>
        </Document>
        """;

    private static string Pacs002(string orgnlEndToEndId, string? initiationForm = null)
    {
        var orgnlTxRef = initiationForm is null
            ? string.Empty
            : $"<OrgnlTxRef><PmtTpInf><LclInstrm><Prtry>{initiationForm}</Prtry></LclInstrm></PmtTpInf></OrgnlTxRef>";
        return $"""
            <Document>
              <FIToFIPmtStsRpt>
                <TxInfAndSts>
                  <OrgnlEndToEndId>{orgnlEndToEndId}</OrgnlEndToEndId>
                  {orgnlTxRef}
                </TxInfAndSts>
              </FIToFIPmtStsRpt>
            </Document>
            """;
    }

    [Fact]
    public void Transform_RewritesOrgnlEndToEndId_FromAToB()
    {
        var sentA = Pacs008("E2E-A", "DICT");
        var sentB = Pacs008("E2E-B", "DICT");
        var response = Pacs002("E2E-A");

        var result = _transformer.Transform(response, sentA, sentB);

        result.Should().Contain("<OrgnlEndToEndId>E2E-B</OrgnlEndToEndId>");
        result.Should().NotContain("E2E-A");
    }

    [Fact]
    public void Transform_RewritesInitiationForm_WhenPresentInResponse()
    {
        var sentA = Pacs008("E2E-A", "DICT");
        var sentB = Pacs008("E2E-B", "MANU");
        var response = Pacs002("E2E-A", initiationForm: "DICT");

        var result = _transformer.Transform(response, sentA, sentB);

        result.Should().Contain("<OrgnlEndToEndId>E2E-B</OrgnlEndToEndId>");
        result.Should().Contain("<Prtry>MANU</Prtry>");
        result.Should().NotContain("DICT");
    }

    [Fact]
    public void Transform_SkipsRule_WhenResponseTargetNodeAbsent()
    {
        // A and B differ on initiation form, but the pacs.002 does not echo LclInstrm/Prtry.
        var sentA = Pacs008("E2E-A", "DICT");
        var sentB = Pacs008("E2E-B", "MANU");
        var response = Pacs002("E2E-A");

        var result = _transformer.Transform(response, sentA, sentB);

        result.Should().Contain("<OrgnlEndToEndId>E2E-B</OrgnlEndToEndId>");
        result.Should().NotContain("Prtry");
    }

    [Fact]
    public void Transform_RewritesOrgnlMsgId_FromAToB_ForStatusReport()
    {
        // System A and System B minted independent MsgIds for the message being answered; the
        // response's OrgnlMsgId back-link must be rewritten from A's MsgId to B's.
        var sentA = "<Document><AppHdr><MsgId>MSG-A</MsgId></AppHdr><MndtCxlReq/></Document>";
        var sentB = "<Document><AppHdr><MsgId>MSG-B</MsgId></AppHdr><MndtCxlReq/></Document>";
        var response = "<Document><MndtAccptncRpt><OrgnlMsgInf><OrgnlMsgId>MSG-A</OrgnlMsgId></OrgnlMsgInf></MndtAccptncRpt></Document>";

        var result = _transformer.Transform(response, sentA, sentB);

        result.Should().Contain("<OrgnlMsgId>MSG-B</OrgnlMsgId>");
        result.Should().NotContain("MSG-A");
    }

    private static string Trck002(string bizMsgIdr, string ispb, string endToEndId = "E2E-INTERNAL") => $"""
        <Envelope>
          <AppHdr>
            <Fr><FIId><FinInstnId><Othr><Id>{ispb}</Id></Othr></FinInstnId></FIId></Fr>
            <To><FIId><FinInstnId><Othr><Id>00038166</Id></Othr></FinInstnId></FIId></To>
            <BizMsgIdr>{bizMsgIdr}</BizMsgIdr>
          </AppHdr>
          <Document><PmtStsTrckrRpt>
            <GrpHdr><MsgId>{bizMsgIdr}</MsgId></GrpHdr>
            <TrckrStsAndTx><Tx><PmtId><EndToEndId>{endToEndId}</EndToEndId></PmtId></Tx></TrckrStsAndTx>
          </PmtStsTrckrRpt></Document>
        </Envelope>
        """;

    private static string Camt025(string orgnlTrckMsgId, string endToEndId, string toIspb) => $"""
        <Envelope>
          <AppHdr>
            <Fr><FIId><FinInstnId><Othr><Id>00038166</Id></Othr></FinInstnId></FIId></Fr>
            <To><FIId><FinInstnId><Othr><Id>{toIspb}</Id></Othr></FinInstnId></FIId></To>
          </AppHdr>
          <Document><Rct><RctDtls>
            <OrgnlMsgId><MsgId>{orgnlTrckMsgId}</MsgId></OrgnlMsgId>
            <OrgnlPmtId><PrtryId>{endToEndId}</PrtryId></OrgnlPmtId>
            <ReqHdlg><Sts><Cd>ACPT</Cd></Sts></ReqHdlg>
          </RctDtls></Rct></Document>
        </Envelope>
        """;

    [Fact]
    public void Transform_Camt025_RewritesNestedOrgnlMsgId_And_AppHdrTo_ForSystemB()
    {
        var trckA = Trck002(bizMsgIdr: "TRCK-A", ispb: "11111111");
        var trckB = Trck002(bizMsgIdr: "TRCK-B", ispb: "22222222");
        var response = Camt025(orgnlTrckMsgId: "TRCK-A", endToEndId: "E2E-INTERNAL", toIspb: "11111111");

        var result = _transformer.Transform(response, trckA, trckB);

        // Nested OrgnlMsgId/MsgId back-link rewritten to B's trck.002 id.
        result.Should().Contain("<MsgId>TRCK-B</MsgId>");
        result.Should().NotContain("TRCK-A");
        // AppHdr To rewritten to B's ISPB.
        result.Should().Contain("<Id>22222222</Id>");
        // OrgnlPmtId/PrtryId (shared EndToEndId) left untouched, status unchanged.
        result.Should().Contain("<PrtryId>E2E-INTERNAL</PrtryId>");
        result.Should().Contain("<Cd>ACPT</Cd>");
    }

    [Fact]
    public void Transform_Camt025_Unchanged_WhenSystemsShareIspbAndTrckId()
    {
        var trck = Trck002(bizMsgIdr: "TRCK-SAME", ispb: "11111111");
        var response = Camt025(orgnlTrckMsgId: "TRCK-SAME", endToEndId: "E2E-INTERNAL", toIspb: "11111111");

        var result = _transformer.Transform(response, trck, trck);

        result.Should().Be(response);
    }

    [Fact]
    public void Transform_ReturnsUnchanged_WhenAAndBAreEqual()
    {
        var sent = Pacs008("E2E-SAME", "DICT");
        var response = Pacs002("E2E-SAME", initiationForm: "DICT");

        var result = _transformer.Transform(response, sent, sent);

        result.Should().Be(response);
    }
}
