using FluentAssertions;
using Orchestrator.PayerAccount;
using Xunit;

namespace Orchestrator.PayerAccount.Tests;

public sealed class PayerAccountExtractorTests
{
    private readonly PayerAccountExtractor _extractor = new();

    // Real Bacen SPI pacs.008 shape: <Envelope xmlns="..."> with AppHdr + Document; DbtrAcct carries
    // the account (Othr/Id) and branch (Othr/Issr).
    private static string Pacs008(string account = "500000", string branch = "3000") => $"""
        <Envelope xmlns="https://www.bcb.gov.br/pi/pacs.008/1.16">
          <Document><FIToFICstmrCdtTrf><CdtTrfTxInf>
            <PmtId><EndToEndId>E2E-1</EndToEndId></PmtId>
            <Dbtr><Nm>Fulano da Silva</Nm></Dbtr>
            <DbtrAcct><Id><Othr><Id>{account}</Id><Issr>{branch}</Issr></Othr></Id><Tp><Cd>CACC</Cd></Tp></DbtrAcct>
          </CdtTrfTxInf></FIToFICstmrCdtTrf></Document>
        </Envelope>
        """;

    [Fact]
    public void ExtractFromPacs008_ReturnsBranchAndAccount()
    {
        var result = _extractor.ExtractFromPacs008(Pacs008(account: "500000", branch: "3000"));

        result.Should().NotBeNull();
        result!.Account.Should().Be("500000");
        result.Branch.Should().Be("3000");
    }

    [Fact]
    public void ExtractFromPacs008_NoDebtorAccount_ReturnsNull()
    {
        var xml = """
            <Envelope xmlns="https://www.bcb.gov.br/pi/pacs.008/1.16">
              <Document><FIToFICstmrCdtTrf><CdtTrfTxInf>
                <PmtId><EndToEndId>E2E-1</EndToEndId></PmtId>
              </CdtTrfTxInf></FIToFICstmrCdtTrf></Document>
            </Envelope>
            """;

        _extractor.ExtractFromPacs008(xml).Should().BeNull();
    }

    [Fact]
    public void ExtractFromPacs008_MissingBranch_ReturnsAccountWithEmptyBranch()
    {
        var xml = """
            <Envelope><Document><FIToFICstmrCdtTrf><CdtTrfTxInf>
              <DbtrAcct><Id><Othr><Id>987654</Id></Othr></Id></DbtrAcct>
            </CdtTrfTxInf></FIToFICstmrCdtTrf></Document></Envelope>
            """;

        var result = _extractor.ExtractFromPacs008(xml);

        result.Should().NotBeNull();
        result!.Account.Should().Be("987654");
        result.Branch.Should().BeEmpty();
    }

    [Fact]
    public void ExtractOriginalEndToEndId_FromPacs004_ReturnsOriginalEndToEndId()
    {
        var xml = """
            <Envelope xmlns="https://www.bcb.gov.br/pi/pacs.004/1.5">
              <Document><PmtRtr><TxInf>
                <RtrId>D-1</RtrId>
                <OrgnlEndToEndId>E2E-ORIGINAL-008</OrgnlEndToEndId>
              </TxInf></PmtRtr></Document>
            </Envelope>
            """;

        _extractor.ExtractOriginalEndToEndId(xml).Should().Be("E2E-ORIGINAL-008");
    }
}
