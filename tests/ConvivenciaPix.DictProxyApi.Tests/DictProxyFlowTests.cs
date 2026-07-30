using FluentAssertions;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ConvivenciaPix.DictProxyApi.Tests;

/// <summary>
/// End-to-end tests for the transparent DICT proxy: System B → proxy → (WireMock stub of the real
/// DICT API) → proxy → System B, verifying the strip-and-re-sign in both directions.
/// </summary>
public sealed class DictProxyFlowTests : IDisposable
{
    private readonly WireMockServer _dict;
    private readonly DictProxyFactory _factory;
    private readonly HttpClient _client;

    // A request that already carries a System B signature — the proxy must strip it and re-sign,
    // leaving exactly one <Signature>.
    private const string SignedRequestXml =
        "<CreateEntryRequest><Entry>alice</Entry>" +
        "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><SignedInfo/></Signature>" +
        "</CreateEntryRequest>";

    public DictProxyFlowTests()
    {
        _dict = WireMockServer.Start();
        _factory = new DictProxyFactory(_dict.Url!);
        _client = _factory.CreateClient();
    }

    // Match only the <Signature> element itself, not <SignatureValue>/<SignatureMethod> children.
    private static int CountSignatures(string xml) => Regex.Matches(xml, "<Signature[ />]").Count;

    [Fact]
    public async Task Post_ReSignsRequestToUpstream_AndReSignsResponseToClient()
    {
        _dict
            .Given(Request.Create().WithPath("/api/v2/entries").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/xml")
                .WithBody("<CreateEntryResponse><Entry>alice</Entry></CreateEntryResponse>"));

        var content = new StringContent(SignedRequestXml, Encoding.UTF8, "application/xml");
        var response = await _client.PostAsync("/api/v2/entries", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The request forwarded to the real DICT API was re-signed: exactly one <Signature>.
        var upstreamBody = _dict.LogEntries.Single().RequestMessage.Body!;
        CountSignatures(upstreamBody).Should().Be(1);
        upstreamBody.Should().Contain("<Entry>alice</Entry>");

        // The response returned to System B was also re-signed.
        var clientBody = await response.Content.ReadAsStringAsync();
        CountSignatures(clientBody).Should().Be(1);
        clientBody.Should().Contain("<Entry>alice</Entry>");
    }

    [Fact]
    public async Task Get_ForwardsMethodPathAndQuery_AndPassesStatusThrough()
    {
        _dict
            .Given(Request.Create().WithPath("/api/v2/entries/alice").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var response = await _client.GetAsync("/api/v2/entries/alice?includeInactive=true");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var upstream = _dict.LogEntries.Single().RequestMessage;
        upstream.Method.Should().Be("GET");
        upstream.Path.Should().Be("/api/v2/entries/alice");
        upstream.RawQuery.Should().Contain("includeInactive=true");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _dict.Stop();
        _dict.Dispose();
    }
}
