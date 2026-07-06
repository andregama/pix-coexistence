using Confluent.Kafka;
using ConvivenciaPix.Domain.Entities;
using ConvivenciaPix.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ConvivenciaPix.Integration.Tests;

public sealed class SpiPipelineTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public SpiPipelineTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = _fixture.CreateClient();
    }

    [Fact]
    public async Task PostMessages_Single_Returns201WithPiResourceId()
    {
        var idempotentId = NewId();
        var xml = BuildPacs008Xml("MSG-" + idempotentId, idempotentId);

        var content = new StringContent(xml, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        var response = await _client.PostAsync("/api/v1/in/12345678/msgs", content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("PI-ResourceId").Should().ContainSingle()
            .Which.Split(',').Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PostMessages_Multipart_Returns201WithMultipleResourceIds()
    {
        var idA = NewId();
        var idB = NewId();

        using var multipart = new MultipartContent("mixed", "boundary-test");
        foreach (var (msgId, e2eId) in new[] { ("MSG-" + idA, idA), ("MSG-" + idB, idB) })
        {
            var part = new StringContent(BuildPacs008Xml(msgId, e2eId), Encoding.UTF8);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
            multipart.Add(part);
        }

        var response = await _client.PostAsync("/api/v1/in/12345678/msgs", multipart);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var ids = response.Headers.GetValues("PI-ResourceId").Single().Split(',');
        ids.Should().HaveCount(2);
        ids[0].Should().NotBe(ids[1]);
    }

    [Fact]
    public async Task PostMessages_EmptyBody_Returns400()
    {
        var content = new StringContent("", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        var response = await _client.PostAsync("/api/v1/in/12345678/msgs", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartStream_EmptyQueue_Returns204WithPiPullNext()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        await redis.GetDatabase().KeyDeleteAsync("spi:out:queue");

        var response = await _client.GetAsync("/api/v1/out/99999999/stream/start");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("PI-Pull-Next").Should().ContainSingle()
            .Which.Should().StartWith("/api/v1/out/99999999/stream/");
    }

    /// <summary>
    /// Simulates a Bacen inbound message (pacs.002) arriving for System A via CDC.
    /// The correlate worker (SystemAInboundCorrelateConsumer) rewrites the response to System B's
    /// values from the stored pacs.008 A/B pair and publishes a ready event on spi.systemb.responses;
    /// the proxy worker (SystemBResponseProxyConsumer) signs it and enqueues it for System B to poll.
    /// A complete SpiSentMsg (System A + System B pacs.008) must be seeded first so the transform
    /// can compute B's EndToEndId — otherwise the response is routed to the DLQ.
    /// </summary>
    [Fact]
    public async Task FullRoundTrip_InboundCdcThenStreamGetsTransformedSignedResponse()
    {
        var systemAE2eId = NewId();
        var systemBE2eId = NewId();
        var bacenMsgId = "BACEN-" + systemAE2eId;

        // Seed the correlated pacs.008 A/B pair: System A used systemAE2eId, System B used systemBE2eId.
        using (var scope = _fixture.Services.CreateScope())
        {
            var sentRepo = scope.ServiceProvider.GetRequiredService<ISpiSentMsgRepository>();
            var sent = SpiSentMsg.Create(systemAE2eId, "pacs.008");
            sent.UpdateFromSystemA("MSG-A-" + systemAE2eId, BuildPacs008Xml("MSG-A", systemAE2eId), null);
            sent.UpdateFromSystemB("MSG-B-" + systemBE2eId, BuildPacs008Xml("MSG-B", systemBE2eId), null);
            await sentRepo.AddAsync(sent);
        }

        // Simulate CDC event from SpiRecepApiBacen (Bacen → System A). OrgnlEndToEndId = System A's E2E.
        var xmlMsg = BuildPacs002Xml(bacenMsgId, systemAE2eId);
        var cdc = new { after = new { XmlMsg = xmlMsg, Problem = (string?)null } };
        await PublishToKafka("spi.systema.inbound", systemAE2eId, JsonSerializer.Serialize(cdc));

        // Wait for correlate + proxy workers to transform, sign, and enqueue.
        HttpResponseMessage? streamResponse = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            streamResponse = await _client.GetAsync("/api/v1/out/11111111/stream/start");
            if (streamResponse.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(2000);
        }

        streamResponse!.StatusCode.Should().Be(HttpStatusCode.OK);
        streamResponse.Headers.GetValues("PI-Pull-Next").Should().NotBeEmpty();
        streamResponse.Headers.GetValues("PI-ResourceId").Should().NotBeEmpty();
        var body = await streamResponse.Content.ReadAsStringAsync();
        body.Should().Contain(bacenMsgId);
        body.Should().Contain("<Signature");
        // Proof of transformation: the OrgnlEndToEndId was rewritten from System A's to System B's E2E.
        body.Should().Contain(systemBE2eId);
        body.Should().Contain($"<OrgnlEndToEndId>{systemBE2eId}</OrgnlEndToEndId>");
    }

    private async Task PublishToKafka(string topic, string key, string value)
    {
        var config = new ProducerConfig { BootstrapServers = _fixture.KafkaBootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value });
    }

    // pacs.008: CdtTrfTxInf/PmtId/EndToEndId is the idempotency key
    private static string BuildPacs008Xml(string messageId, string endToEndId) => $"""
        <Document>
            <FIToFICstmrCdtTrf>
                <GrpHdr><MsgId>{messageId}</MsgId></GrpHdr>
                <CdtTrfTxInf>
                    <PmtId><EndToEndId>{endToEndId}</EndToEndId></PmtId>
                    <Dbtr><Nm>PAYER-{endToEndId}</Nm></Dbtr>
                    <Cdtr><Nm>PAYEE-{endToEndId}</Nm></Cdtr>
                </CdtTrfTxInf>
            </FIToFICstmrCdtTrf>
        </Document>
        """;

    // pacs.002: TxInfAndSts/OrgnlEndToEndId is the idempotency key
    private static string BuildPacs002Xml(string messageId, string orgnlEndToEndId) => $"""
        <Document>
            <FIToFIPmtStsRpt>
                <GrpHdr><MsgId>{messageId}</MsgId></GrpHdr>
                <TxInfAndSts>
                    <OrgnlEndToEndId>{orgnlEndToEndId}</OrgnlEndToEndId>
                    <Dbtr><Nm>PAYER-{orgnlEndToEndId}</Nm></Dbtr>
                    <Cdtr><Nm>PAYEE-{orgnlEndToEndId}</Nm></Cdtr>
                </TxInfAndSts>
            </FIToFIPmtStsRpt>
        </Document>
        """;

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
