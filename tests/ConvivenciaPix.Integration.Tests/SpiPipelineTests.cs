using Confluent.Kafka;
using ConvivenciaPix.Application.DTOs;
using ConvivenciaPix.Infrastructure.Messaging;
using FluentAssertions;
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
    public async Task Pipeline_HappyPath_ReturnsSignedResponse()
    {
        // 1. Arrange
        var idSystemB = Guid.NewGuid().ToString("N")[..16];
        var messageId = "MSG-" + idSystemB;
        var requestXml = $"""
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

        // 2. Act: Send request to Proxy API (it will publish to Kafka and wait)
        var requestTask = _client.PostAsync("/api/spi/messages", 
            new StringContent(requestXml, Encoding.UTF8, "application/xml"));

        // Give it a moment to publish to Kafka
        await Task.Delay(2000);

        // 3. Simulate System A Response (CDC event)
        // In a real scenario, Debezium would publish this to 'spi.systema.responses'
        // The CDC event contains IdSystemA and the XML from Bacen
        var idSystemA = "A-" + idSystemB;
        var cdcEvent = new
        {
            IdSystemA = idSystemA,
            MessageId = "MSG-A-" + idSystemB,
            Payload = $"""<Document><MsgId>BACEN-{idSystemB}</MsgId><Dbtr><Nm>PAYER-{idSystemB}</Nm></Dbtr><Cdtr><Nm>PAYEE-{idSystemB}</Nm></Cdtr></Document>"""
        };
        
        await PublishToKafka("spi.systema.responses", idSystemA, JsonSerializer.Serialize(cdcEvent));

        // 4. Assert: API should return the signed XML eventually
        var response = await requestTask;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var responseXml = await response.Content.ReadAsStringAsync();
        responseXml.Should().Contain("BACEN-" + idSystemB);
        responseXml.Should().Contain("<Signature"); // Should be signed by proxy worker
    }

    [Fact]
    public async Task Pipeline_Timeout_Returns504()
    {
        // 1. Arrange
        var idSystemB = Guid.NewGuid().ToString("N")[..16];
        var requestXml = $"""<Document><GrpHdr><MsgId>MSG-{idSystemB}</MsgId></GrpHdr><TxInfAndSts><EndToEndId>{idSystemB}</EndToEndId><Dbtr><Nm>PAYER-{idSystemB}</Nm></Dbtr><Cdtr><Nm>PAYEE-{idSystemB}</Nm></Cdtr></TxInfAndSts></Document>""";

        // 2. Act: Send request and wait for timeout (configured to 10s in fixture)
        var response = await _client.PostAsync("/api/spi/messages", 
            new StringContent(requestXml, Encoding.UTF8, "application/xml"));

        // 3. Assert
        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        var errorXml = await response.Content.ReadAsStringAsync();
        errorXml.Should().Contain("SPI9999");
    }

    private async Task PublishToKafka(string topic, string key, string value)
    {
        var config = new ProducerConfig { BootstrapServers = _fixture.KafkaBootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value });
    }
}
