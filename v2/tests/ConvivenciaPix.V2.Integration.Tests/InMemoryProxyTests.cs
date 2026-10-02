using ConvivenciaPix.V2.Infrastructure.Simulation;
using ConvivenciaPix.V2.Domain;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.SpiProxyApi.Listeners;
using ConvivenciaPix.V2.Tests.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace ConvivenciaPix.V2.Integration.Tests;

/// <summary>
/// The proxy with <c>Storage:Provider=InMemory</c>: the API process alone. It runs the pull loop
/// itself and simulates the SPI — no SQL Server, Redis, Kafka, Docker or SPI endpoint.
/// </summary>
public sealed class InMemoryProxyFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The simulated SPI inside the proxy: inject inbound messages, inspect what was forwarded.</summary>
    public SimulatorState Bacen => Services.GetRequiredService<SimulatorState>();

    public SpiClient SystemA => new(ClientFor(ListenerKind.SystemA));
    public SpiClient SystemB => new(ClientFor(ListenerKind.SystemB));
    public HttpClient Admin => ClientFor(ListenerKind.Admin);

    public Task InitializeAsync()
    {
        using var _ = CreateClient();
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync() => await base.DisposeAsync();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Program chooses the storage provider while it is still building the host, before
        // ConfigureAppConfiguration is applied. Host configuration reaches it as command-line
        // arguments, so it is already there.
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "InMemory",
            // Nothing listens on these: the mode must not need them.
            ["ConnectionStrings:SqlServer"] = "Server=127.0.0.1,1;Connect Timeout=1",
            ["ConnectionStrings:Redis"] = "127.0.0.1:1",
            ["Audit:BootstrapServers"] = "127.0.0.1:1",
            ["Spi:Ispb"] = ProxyFixture.Ispb,
            ["SpiGateway:BaseUrl"] = "http://127.0.0.1:1",
            ["Simulator:LongPollSeconds"] = "1",
            ["DeliveryStream:LongPollSeconds"] = "1",
            ["DeliveryStream:PollIntervalMs"] = "100",
            ["Registry:RefreshSeconds"] = "1",
            ["InboundPull:Enabled"] = "true",
            ["InboundPull:Ispb"] = ProxyFixture.Ispb,
            ["InboundPull:PullTimeoutSeconds"] = "15",
            ["InboundPull:ErrorBackoffMs"] = "200",
            ["InboundSigning:VerifyBacenSignature"] = "false"
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICallerSystemResolver>();
            services.AddSingleton<ICallerSystemResolver, HeaderCallerSystemResolver>();

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private HttpClient ClientFor(ListenerKind listener)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ProxyFixture.ListenerHeader, listener.ToString());
        return client;
    }
}

public sealed class InMemoryProxyTests(InMemoryProxyFixture fixture) : IClassFixture<InMemoryProxyFixture>
{
    private static string NewCpf() => Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();

    [Fact]
    public async Task EachSystem_ReceivesOnlyTheResponseToItsOwnMessage()
    {
        var (msgA, e2eA) = (SpiXml.NewMsgId(), SpiXml.NewEndToEndId());
        var (msgB, e2eB) = (SpiXml.NewMsgId(), SpiXml.NewEndToEndId());
        var systemA = fixture.SystemA;
        var systemB = fixture.SystemB;

        (await systemA.SendAsync(SpiXml.Pacs008(msgA, e2eA))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await systemB.SendAsync(SpiXml.Pacs008(msgB, e2eB))).StatusCode.Should().Be(HttpStatusCode.Created);

        (await systemA.PullUntilAsync(xml => xml.Contains(e2eA))).Should().Contain("pacs.002");
        (await systemB.PullUntilAsync(xml => xml.Contains(e2eB))).Should().Contain("pacs.002");
        (await systemA.DrainAsync(2)).Should().NotContain(xml => xml.Contains(e2eB));

        var routes = fixture.Services.GetRequiredService<ISentRouteRepository>();
        (await routes.FindSystemByMsgIdAsync(msgA)).Should().Be(SystemName.SystemA);
        (await routes.FindSystemByMsgIdAsync(msgB)).Should().Be(SystemName.SystemB);
        fixture.Bacen.Received.Should().Contain(m => m.MsgId == msgB);
    }

    [Fact]
    public async Task ReceivedTransfer_GoesToSystemB_OnceTheCustomerIsRegistered()
    {
        var cpf = NewCpf();
        var created = await fixture.Admin.PostAsJsonAsync(
            "/admin/v1/system-b-documents", new { conta = "12345", agencia = "0001", documento = cpf });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var e2e = SpiXml.NewEndToEndId("99999999");
        fixture.Bacen.Enqueue(Encoding.UTF8.GetBytes(SpiXml.Pacs008(
            SpiXml.NewMsgId("99999999"), e2e, creditorDocument: cpf, from: SpiXml.BacenIspb, to: SpiXml.BankIspb)));

        (await fixture.SystemB.PullUntilAsync(xml => xml.Contains(e2e))).Should().Contain("pacs.008");
        (await fixture.SystemA.DrainAsync(2)).Should().NotContain(xml => xml.Contains(e2e));
    }

    [Fact]
    public async Task MessageInjectedThroughTheAdminListener_IsRoutedLikeOneFromBacen()
    {
        var e2e = SpiXml.NewEndToEndId("99999999");
        var xml = SpiXml.Pacs008(
            SpiXml.NewMsgId("99999999"), e2e, creditorDocument: NewCpf(), from: SpiXml.BacenIspb, to: SpiXml.BankIspb);

        var injected = await fixture.Admin.PostAsync("/sim/inbound", new StringContent(xml, Encoding.UTF8, "application/xml"));
        injected.StatusCode.Should().Be(HttpStatusCode.OK);

        // Not registered for System B: the default system receives it.
        (await fixture.SystemA.PullUntilAsync(m => m.Contains(e2e))).Should().Contain("pacs.008");
        (await fixture.SystemA.Http.PostAsync("/sim/inbound", new StringContent(xml))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the developer endpoints exist only on the admin listener");
    }

    [Fact]
    public async Task HttpErrorFromTheSpi_ReachesTheSenderUnchanged()
    {
        var xml = SpiXml.Pacs008(SpiXml.NewMsgId(), SpiXml.NewEndToEndId(), extra: $"<!-- {SpiSimulator.HttpErrorMarker} -->");

        var response = await fixture.SystemB.SendAsync(xml);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("urn:ietf:rfc:7807");
    }

    [Fact]
    public async Task Readiness_DoesNotDependOnSqlOrRedis()
    {
        (await fixture.Admin.GetAsync("/healthz/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
