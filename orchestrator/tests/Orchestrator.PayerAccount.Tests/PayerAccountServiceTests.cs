using FluentAssertions;
using Orchestrator.PayerAccount;
using Xunit;

namespace Orchestrator.PayerAccount.Tests;

public sealed class PayerAccountServiceTests
{
    private readonly FakeReader _reader = new();
    private readonly PayerAccountService _sut;

    public PayerAccountServiceTests() => _sut = new PayerAccountService(_reader, new PayerAccountExtractor());

    private static string Pacs008(string account, string branch) => $"""
        <Envelope><Document><FIToFICstmrCdtTrf><CdtTrfTxInf>
          <DbtrAcct><Id><Othr><Id>{account}</Id><Issr>{branch}</Issr></Othr></Id></DbtrAcct>
        </CdtTrfTxInf></FIToFICstmrCdtTrf></Document></Envelope>
        """;

    private static string Pacs004(string orgnlEndToEndId) => $"""
        <Envelope><Document><PmtRtr><TxInf>
          <RtrId>D-1</RtrId><OrgnlEndToEndId>{orgnlEndToEndId}</OrgnlEndToEndId>
        </TxInf></PmtRtr></Document></Envelope>
        """;

    [Fact]
    public async Task Pacs008Row_ExtractsPayerAccountDirectly()
    {
        _reader.Add(new SpiSentMsgRow("E2E-1", "pacs.008", Pacs008("500000", "3000"), null, null));

        var result = await _sut.GetPayerAccountAsync("E2E-1");

        result.Should().Be(new PayerAccountInfo("3000", "500000"));
    }

    [Fact]
    public async Task Pacs008Row_FallsBackToSystemB_WhenSystemANull()
    {
        _reader.Add(new SpiSentMsgRow("E2E-1", "pacs.008", XmlMsgSystemA: null, Pacs008("111", "222"), null));

        var result = await _sut.GetPayerAccountAsync("E2E-1");

        result.Should().Be(new PayerAccountInfo("222", "111"));
    }

    [Fact]
    public async Task Pacs004Row_FollowsStoredOriginalId_ToOriginalPacs008()
    {
        _reader.Add(new SpiSentMsgRow("RTR-1", "pacs.004", Pacs004("E2E-ORIG"), null, OriginalMsgIdempotentId: "E2E-ORIG"));
        _reader.Add(new SpiSentMsgRow("E2E-ORIG", "pacs.008", Pacs008("900001", "4000"), null, null));

        var result = await _sut.GetPayerAccountAsync("RTR-1");

        result.Should().Be(new PayerAccountInfo("4000", "900001"));
    }

    [Fact]
    public async Task Pacs004Row_NoStoredOriginalId_ParsesOrgnlEndToEndIdFromXml()
    {
        _reader.Add(new SpiSentMsgRow("RTR-1", "pacs.004", Pacs004("E2E-ORIG"), null, OriginalMsgIdempotentId: null));
        _reader.Add(new SpiSentMsgRow("E2E-ORIG", "pacs.008", Pacs008("900002", "4100"), null, null));

        var result = await _sut.GetPayerAccountAsync("RTR-1");

        result.Should().Be(new PayerAccountInfo("4100", "900002"));
    }

    [Fact]
    public async Task Pacs004Row_OriginalNotInSpiSentMsg_ReturnsNull()
    {
        _reader.Add(new SpiSentMsgRow("RTR-1", "pacs.004", Pacs004("E2E-MISSING"), null, "E2E-MISSING"));

        (await _sut.GetPayerAccountAsync("RTR-1")).Should().BeNull();
    }

    [Fact]
    public async Task UnknownIdempotentId_ReturnsNull()
    {
        (await _sut.GetPayerAccountAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task UnsupportedMsgType_Throws()
    {
        _reader.Add(new SpiSentMsgRow("X-1", "pacs.002", "<x/>", null, null));

        await FluentActions.Invoking(() => _sut.GetPayerAccountAsync("X-1"))
            .Should().ThrowAsync<NotSupportedException>();
    }

    private sealed class FakeReader : ISpiSentMsgReader
    {
        private readonly Dictionary<string, SpiSentMsgRow> _rows = new();
        public void Add(SpiSentMsgRow row) => _rows[row.IdempotentId] = row;

        public Task<SpiSentMsgRow?> FindByIdempotentIdAsync(string idempotentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.GetValueOrDefault(idempotentId));
    }
}
