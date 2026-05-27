using Confluent.Kafka;
using FluentAssertions;
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
        var idSystemB = Guid.NewGuid().ToString("N")[..16];
        var xml = BuildPacsXml(messageId: "MSG-" + idSystemB, idSystemB: idSystemB);

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
        var idA = Guid.NewGuid().ToString("N")[..16];
        var idB = Guid.NewGuid().ToString("N")[..16];

        using var multipart = new MultipartContent("mixed", "boundary-test");
        foreach (var (msgId, idSysB) in new[] { ("MSG-" + idA, idA), ("MSG-" + idB, idB) })
        {
            var part = new StringContent(BuildPacsXml(msgId, idSysB), Encoding.UTF8);
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

    [Fact]
    public async Task FullRoundTrip_PostThenStreamGetsSignedResponse()
    {
        var idSystemB = Guid.NewGuid().ToString("N")[..16];
        var xml = BuildPacsXml("MSG-" + idSystemB, idSystemB);
        var content = new StringContent(xml, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };

        var postResponse = await _client.PostAsync("/api/v1/in/11111111/msgs", content);
        postResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Give the SystemBSentConsumer time to persist the pending row, otherwise the propagate
        // worker's correlation lookup (5×1s retries) can exhaust before the row exists.
        await Task.Delay(3000);

        // Simulate System A response via CDC.
        var idSystemA = "A-" + idSystemB;
        var cdc = new
        {
            after = new
            {
                IdSystemA = idSystemA,
                MessageId = "MSG-A-" + idSystemB,
                Payload = $"<Document><MsgId>BACEN-{idSystemB}</MsgId><Dbtr><Nm>PAYER-{idSystemB}</Nm></Dbtr><Cdtr><Nm>PAYEE-{idSystemB}</Nm></Cdtr></Document>"
            }
        };
        await PublishToKafka("spi.systema.responses", idSystemA, JsonSerializer.Serialize(cdc));

        // Pull from the outbound stream — must wait for the full correlate→sign→enqueue chain.
        // Worst case: SystemBSent consumer + Correlate consumer + ProxyResponse consumer + up to 5×1s
        // correlation retries inside PropagateResponseUseCase. Allow ~30s.
        HttpResponseMessage? streamResponse = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            streamResponse = await _client.GetAsync("/api/v1/out/11111111/stream/start");
            if (streamResponse.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(1000);
        }

        streamResponse!.StatusCode.Should().Be(HttpStatusCode.OK);
        streamResponse.Headers.GetValues("PI-Pull-Next").Should().NotBeEmpty();
        streamResponse.Headers.GetValues("PI-ResourceId").Should().NotBeEmpty();
        var body = await streamResponse.Content.ReadAsStringAsync();
        body.Should().Contain("BACEN-" + idSystemB);
        body.Should().Contain("<Signature");
    }

    private async Task PublishToKafka(string topic, string key, string value)
    {
        var config = new ProducerConfig { BootstrapServers = _fixture.KafkaBootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value });
    }

    private static string BuildPacsXml(string messageId, string idSystemB) => $"""
        <Document>
            <FIToFIPmtStsRpt>
                <GrpHdr><MsgId>{messageId}</MsgId></GrpHdr>
                <TxInfAndSts>
                    <EndToEndId>{idSystemB}</EndToEndId>
                    <Dbtr><Nm>PAYER-{idSystemB}</Nm></Dbtr>
                    <Cdtr><Nm>PAYEE-{idSystemB}</Nm></Cdtr>
                </TxInfAndSts>
            </FIToFIPmtStsRpt>
        </Document>
        """;
}
