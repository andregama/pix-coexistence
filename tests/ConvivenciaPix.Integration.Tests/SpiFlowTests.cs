using Confluent.Kafka;
using ConvivenciaPix.Application.Mappers;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using ConvivenciaPix.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ConvivenciaPix.Integration.Tests;

/// <summary>
/// End-to-end coverage of the coexistence flows not exercised by <see cref="SpiPipelineTests"/>:
/// the pibr Echo path, System B / System A outbound correlation and completion, the comparison
/// engine, Bacen error propagation, DLQ routing, and stream acking. Drives the real API + all four
/// worker consumers over Testcontainers (SQL/Kafka/Redis).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class SpiFlowTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public SpiFlowTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = _fixture.CreateClient();
    }

    // ---------------------------------------------------------------- pibr Echo

    [Fact]
    public async Task Pibr001_IsAnswered_WithSignedPibr002Echo()
    {
        var data = "echo-" + NewId();
        var xml = BuildPibr001Xml(frIspb: "12345678", toIspb: "87654321", data: data);

        await PostXmlAsync("/api/v1/in/12345678/msgs", xml, expect: HttpStatusCode.Created);

        var body = await PullUntilAsync("12345678", b => b.Contains(data));

        body.Should().NotBeNull("System B must receive a pibr.002 echo for its pibr.001");
        body!.Should().Contain("EchoRpt");
        body.Should().Contain("pibr.002.spi.1.3");
        body.Should().Contain($"<OrgnlData>{data}</OrgnlData>");
        // Signature must be inside AppHdr/Sgntr (the HSM/software signer placement).
        body.Should().Contain("Signature");
        SignatureIsInsideSgntr(body!).Should().BeTrue();
    }

    // ---------------------------------------------------------------- ingest edge cases

    [Fact]
    public async Task PostMessages_UnsupportedMediaType_Returns415()
    {
        var content = new StringContent("plain", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var response = await _client.PostAsync("/api/v1/in/12345678/msgs", content);
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task PostMessages_Duplicate_AreBothAcked_WithDistinctResourceIds()
    {
        // Bacen §2.2.1: duplicate sends are recorded again; idempotency happens during processing.
        var e2e = NewId();
        var xml = BuildPacs008Xml("MSG-" + e2e, e2e);

        var first = await PostXmlAsync("/api/v1/in/12345678/msgs", xml, HttpStatusCode.Created);
        var second = await PostXmlAsync("/api/v1/in/12345678/msgs", xml, HttpStatusCode.Created);

        var idA = first.Headers.GetValues("PI-ResourceId").Single();
        var idB = second.Headers.GetValues("PI-ResourceId").Single();
        idA.Should().NotBe(idB);
    }

    // ---------------------------------------------------------------- outbound correlation

    [Fact]
    public async Task SystemBOutbound_Pacs008_PersistsSentMsgWithSystemBSide()
    {
        var e2e = NewId();
        await PostXmlAsync("/api/v1/in/12345678/msgs", BuildPacs008Xml("MSG-B-" + e2e, e2e), HttpStatusCode.Created);

        var sent = await WaitForSentMsgAsync(e2e, m => m.XmlMsgSystemB is not null);

        sent.Should().NotBeNull();
        sent!.MsgType.Should().Be("pacs.008");
        sent.XmlMsgSystemB.Should().NotBeNull();
        sent.IsComplete.Should().BeFalse("no System A side has arrived yet");
    }

    [Fact]
    public async Task SystemAAndSystemBOutbound_Complete_TheSharedSentMsgRow()
    {
        var e2e = NewId();

        // System B side via the proxy-api POST path. Wait for its row before sending the System A
        // side so the CDC event updates the shared row instead of racing a second insert on it.
        await PostXmlAsync("/api/v1/in/12345678/msgs", BuildPacs008Xml("MSG-B-" + e2e, e2e), HttpStatusCode.Created);
        await WaitForSentMsgAsync(e2e, m => m.XmlMsgSystemB is not null);

        // System A side via a Debezium CDC event on the outbound table.
        await PublishCdcAsync(CdcSource.OutboundTable, e2e, BuildPacs008Xml("MSG-A-" + e2e, e2e), messageId: "MSG-A-" + e2e);

        var sent = await WaitForSentMsgAsync(e2e, m => m.IsComplete);

        sent.Should().NotBeNull("both outbound sides should merge into one complete row");
        sent!.IsComplete.Should().BeTrue();
        sent.XmlMsgSystemA.Should().NotBeNull();
        sent.XmlMsgSystemB.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- comparison engine

    [Fact]
    public async Task DivergentBusinessFields_BetweenSystems_RaiseADiscrepancy()
    {
        var e2e = NewId();

        // Same transaction (EndToEndId) but a different payer on each side. Sequence the two sides
        // (wait for System B's row) so the CDC event completes the shared row deterministically.
        await PostXmlAsync("/api/v1/in/12345678/msgs",
            BuildPacs008Xml("MSG-B-" + e2e, e2e, payer: "PAYER-B", payee: "PAYEE"), HttpStatusCode.Created);
        await WaitForSentMsgAsync(e2e, m => m.XmlMsgSystemB is not null);

        await PublishCdcAsync(CdcSource.OutboundTable, e2e,
            BuildPacs008Xml("MSG-A-" + e2e, e2e, payer: "PAYER-A", payee: "PAYEE"), messageId: "MSG-A-" + e2e);

        // The comparison engine persists the discrepancy (RF-08) and publishes it to Kafka.
        var persisted = await WaitForDiscrepancyAsync(e2e, "PayerId");
        if (!persisted)
        {
            // Diagnostic: surface a dead-lettered comparison event if the engine failed to process.
            var dlq = await ConsumeUntilAsync("spi.comparison.events.dlq", v => v.Contains(e2e), timeoutSeconds: 5);
            dlq.Should().BeNull("the comparison event must not be dead-lettered");
        }
        persisted.Should().BeTrue("the comparison engine must flag and persist the payer mismatch");

        // The discrepancy detail is base64-encoded inside the envelope's PayloadBase64; the
        // IdempotentId is carried in plaintext on the envelope, so match on that.
        var published = await ConsumeUntilAsync(Topics.Discrepancies,
            v => v.Contains(e2e), timeoutSeconds: 15);
        published.Should().NotBeNull("the discrepancy must also be published to spi.discrepancies");
    }

    // ---------------------------------------------------------------- error propagation (RF-04)

    [Fact]
    public async Task InboundBacenError_IsSimulated_AsSignedRejectionForSystemB()
    {
        var systemAE2e = NewId();
        var systemBE2e = NewId();

        await SeedCompletePacs008PairAsync(systemAE2e, systemBE2e);

        // Bacen rejects the transaction: a pacs.002 with status RJCT plus an error code.
        var rejection = BuildPacs002Xml("BACEN-" + systemAE2e, systemAE2e, txSts: "RJCT");
        await PublishCdcAsync(CdcSource.InboundTable, systemAE2e, rejection, problem: "AB09");

        var body = await PullUntilAsync("11111111", b => b.Contains(systemBE2e));

        body.Should().NotBeNull("the errored response must be delivered to System B");
        body!.Should().Contain("RJCT", "the Bacen rejection status must be propagated");
        body.Should().Contain("Signature");
        // Transformed to System B's identifier.
        body.Should().Contain($"<OrgnlEndToEndId>{systemBE2e}</OrgnlEndToEndId>");
    }

    // ---------------------------------------------------------------- resiliency / DLQ (RF-07)

    [Fact]
    public async Task UncorrelatedInbound_RoutesToDeadLetterQueue()
    {
        // A Bacen inbound response with no seeded A/B pacs.008 pair cannot be transformed, so the
        // inbound correlation use case throws and KafkaConsumerBase routes the event to the DLQ.
        var orphanE2e = NewId();
        var xml = BuildPacs002Xml("BACEN-" + orphanE2e, orphanE2e);
        await PublishCdcAsync(CdcSource.InboundTable, orphanE2e, xml);

        var dead = await ConsumeUntilAsync(Topics.SystemACdcDlq, v => v.Contains(orphanE2e));

        dead.Should().NotBeNull("the uncorrelated event must land in spi.systema.cdc.dlq");
    }

    // ---------------------------------------------------------------- stream acking (§2.2.2.3)

    [Fact]
    public async Task DeleteUnknownStream_Returns410Gone()
    {
        var response = await _client.DeleteAsync("/api/v1/out/12345678/stream/deadbeefcafe");
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    // ================================================================ helpers

    private async Task<HttpResponseMessage> PostXmlAsync(string url, string xml, HttpStatusCode expect)
    {
        var content = new StringContent(xml, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        var response = await _client.PostAsync(url, content);
        response.StatusCode.Should().Be(expect);
        return response;
    }

    /// <summary>
    /// Long-polls the outbound stream, acking each batch as it advances, until a message body
    /// satisfies <paramref name="matches"/> or the timeout elapses. Draining unrelated messages
    /// keeps the test robust against the shared outbound queue.
    /// </summary>
    private async Task<string?> PullUntilAsync(string ispb, Func<string, bool> matches, int timeoutSeconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var path = $"/api/v1/out/{ispb}/stream/start";

        while (DateTime.UtcNow < deadline)
        {
            var resp = await _client.GetAsync(path);
            var next = resp.Headers.TryGetValues("PI-Pull-Next", out var v)
                ? v.First()
                : $"/api/v1/out/{ispb}/stream/start";

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var body = await resp.Content.ReadAsStringAsync();
                if (matches(body))
                {
                    await _client.GetAsync(next); // ack the matched batch
                    return body;
                }
                path = next; // not ours: advance (acks it) and keep draining
            }
            else
            {
                path = next;
                await Task.Delay(500);
            }
        }

        return null;
    }

    private async Task<string?> ConsumeUntilAsync(string topic, Func<string, bool> matches, int timeoutSeconds = 40)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _fixture.KafkaBootstrapServers,
            GroupId = "itest-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = true
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                    if (cr?.Message?.Value is { } value && matches(value))
                        return value;
                }
                catch (ConsumeException)
                {
                    // The topic may not exist until the first message is produced to it; keep polling.
                    await Task.Delay(200);
                }
            }
        }
        finally
        {
            consumer.Close();
        }

        return null;
    }

    private async Task<SpiSentMsg?> WaitForSentMsgAsync(
        string idempotentId, Func<SpiSentMsg, bool> predicate, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _fixture.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
            var msg = await repo.FindByIdempotentIdAsync(idempotentId);
            if (msg is not null && predicate(msg))
                return msg;
            await Task.Delay(500);
        }
        return null;
    }

    private async Task<bool> WaitForDiscrepancyAsync(string idempotentId, string field, int timeoutSeconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ConvivenciaPix.Infrastructure.Persistence.CoexistenceDbContext>();
                var exists = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.SpiDiscrepancies, d => d.IdempotentId == idempotentId && d.Field == field);
                if (exists)
                    return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task SeedCompletePacs008PairAsync(string systemAE2e, string systemBE2e)
    {
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
        var sent = SpiSentMsg.Create(systemAE2e, "pacs.008");
        sent.UpdateFromSystemA("MSG-A-" + systemAE2e, BuildPacs008Xml("MSG-A", systemAE2e), null);
        sent.UpdateFromSystemB("MSG-B-" + systemBE2e, BuildPacs008Xml("MSG-B", systemBE2e), null);
        await repo.AddAsync(sent);
    }

    private async Task PublishCdcAsync(
        string table, string key, string xmlMsg, string? messageId = null, string? problem = null)
    {
        object after = messageId is null
            ? new { XmlMsg = xmlMsg, Problem = problem }
            : new { MessageId = messageId, XmlMsg = xmlMsg, Problem = problem };

        var cdc = new { op = "c", source = new { table }, after };
        await PublishToKafkaAsync(Topics.SystemACdc, key, JsonSerializer.Serialize(cdc));
    }

    private async Task PublishToKafkaAsync(string topic, string key, string value)
    {
        var config = new ProducerConfig { BootstrapServers = _fixture.KafkaBootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value });
    }

    private static bool SignatureIsInsideSgntr(string signedXml)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(signedXml);
        return doc.SelectSingleNode(
            "//*[local-name()='AppHdr']/*[local-name()='Sgntr']/*[local-name()='Signature']") is not null;
    }

    // ---------------------------------------------------------------- message builders

    private static string BuildPacs008Xml(
        string messageId, string endToEndId, string? payer = null, string? payee = null) => $"""
        <Document>
            <FIToFICstmrCdtTrf>
                <GrpHdr><MsgId>{messageId}</MsgId></GrpHdr>
                <CdtTrfTxInf>
                    <PmtId><EndToEndId>{endToEndId}</EndToEndId></PmtId>
                    <Dbtr><Nm>{payer ?? "PAYER-" + endToEndId}</Nm></Dbtr>
                    <Cdtr><Nm>{payee ?? "PAYEE-" + endToEndId}</Nm></Cdtr>
                </CdtTrfTxInf>
            </FIToFICstmrCdtTrf>
        </Document>
        """;

    private static string BuildPacs002Xml(string messageId, string orgnlEndToEndId, string? txSts = null) => $"""
        <Document>
            <FIToFIPmtStsRpt>
                <GrpHdr><MsgId>{messageId}</MsgId></GrpHdr>
                <TxInfAndSts>
                    <OrgnlEndToEndId>{orgnlEndToEndId}</OrgnlEndToEndId>
                    {(txSts is null ? "" : $"<TxSts>{txSts}</TxSts>")}
                    <Dbtr><Nm>PAYER-{orgnlEndToEndId}</Nm></Dbtr>
                    <Cdtr><Nm>PAYEE-{orgnlEndToEndId}</Nm></Cdtr>
                </TxInfAndSts>
            </FIToFIPmtStsRpt>
        </Document>
        """;

    private static string BuildPibr001Xml(string frIspb, string toIspb, string data)
    {
        var msgId = "M" + frIspb + Guid.NewGuid().ToString("N")[..23];
        return $"""
            <Envelope xmlns="https://www.bcb.gov.br/pi/pibr.001/1.3">
              <AppHdr>
                <Fr><FIId><FinInstnId><Othr><Id>{frIspb}</Id></Othr></FinInstnId></FIId></Fr>
                <To><FIId><FinInstnId><Othr><Id>{toIspb}</Id></Othr></FinInstnId></FIId></To>
                <BizMsgIdr>{msgId}</BizMsgIdr>
                <MsgDefIdr>pibr.001.spi.1.3</MsgDefIdr>
                <CreDt>2026-01-01T00:00:00.000Z</CreDt>
                <Sgntr/>
              </AppHdr>
              <Document>
                <EchoReq>
                  <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T00:00:00.000Z</CreDtTm></GrpHdr>
                  <EchoTxInf><Data>{data}</Data></EchoTxInf>
                </EchoReq>
              </Document>
            </Envelope>
            """;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
