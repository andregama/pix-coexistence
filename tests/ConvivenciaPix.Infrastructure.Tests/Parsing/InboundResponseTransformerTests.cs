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
    public void Transform_ReturnsUnchanged_WhenAAndBAreEqual()
    {
        var sent = Pacs008("E2E-SAME", "DICT");
        var response = Pacs002("E2E-SAME", initiationForm: "DICT");

        var result = _transformer.Transform(response, sent, sent);

        result.Should().Be(response);
    }
}
