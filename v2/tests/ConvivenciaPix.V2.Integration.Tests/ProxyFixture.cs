using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.BacenSpiSimulator;
using ConvivenciaPix.V2.Infrastructure;
using ConvivenciaPix.V2.Infrastructure.Parsing;
using ConvivenciaPix.V2.Infrastructure.Persistence;
using ConvivenciaPix.V2.SpiProxyApi.Listeners;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Testcontainers.MsSql;
using Testcontainers.Redis;
using Xunit;

namespace ConvivenciaPix.V2.Integration.Tests;

/// <summary>
/// The whole v2 solution in one process: the proxy API (both system listeners and the admin
/// listener), the inbound worker's pull loop, a real SQL Server and Redis, and the Bacen SPI
/// simulator listening on a real port as the "real SPI".
/// </summary>
public sealed class ProxyFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Ispb = "12345678";
    public const string ListenerHeader = "X-Test-Listener";

    private const string RegistryTableSql = """
        IF OBJECT_ID('dbo.TB_DADOS_CONVIVENCIA', 'U') IS NULL
        CREATE TABLE dbo.TB_DADOS_CONVIVENCIA
        (
            Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            Conta       VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            Agencia     VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            Documento   VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            DataCriacao DATETIME2 NOT NULL
        );
        """;

    private readonly MsSqlContainer _sql = new MsSqlBuilder().Build();
    private readonly RedisContainer _redis = new RedisBuilder().Build();
    private WebApplication _simulator = default!;

    public string SqlConnectionString => _sql.GetConnectionString();
    public string SimulatorUrl { get; private set; } = default!;

    /// <summary>The simulated SPI's queues: inject inbound messages, inspect what the proxy forwarded.</summary>
    public SimulatorState Bacen => _simulator.Services.GetRequiredService<SimulatorState>();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _redis.StartAsync());

        await SqlSchemaBootstrapper.ApplyAsync(SqlConnectionString);
        await ExecuteSqlAsync(RegistryTableSql);

        _simulator = SimulatorHost.Build(["--urls", "http://127.0.0.1:0", "--Simulator:LongPollSeconds", "1"]);
        await _simulator.StartAsync();
        SimulatorUrl = _simulator.Urls.First();

        // Starts the host, and with it the pull loop against the simulator.
        using var _ = CreateClient();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _simulator.DisposeAsync();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = SqlConnectionString,
                // In real environments the registry table lives in the first solution's database.
                ["ConnectionStrings:CoexistenceRegistry"] = SqlConnectionString,
                ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
                ["Spi:Ispb"] = Ispb,
                ["SpiGateway:BaseUrl"] = SimulatorUrl,
                ["Forward:TimeoutSeconds"] = "10",
                ["DeliveryStream:LongPollSeconds"] = "1",
                ["DeliveryStream:PollIntervalMs"] = "100",
                ["DeliveryStream:LeaseSeconds"] = "3",
                ["Registry:RefreshSeconds"] = "1",
                ["InboundPull:Enabled"] = "true",
                ["InboundPull:Ispb"] = Ispb,
                ["InboundPull:PullTimeoutSeconds"] = "15",
                ["InboundPull:ErrorBackoffMs"] = "200",
                // The simulator does not sign what it sends; tests that exercise verification turn
                // it on for their own duration.
                ["InboundSigning:VerifyBacenSignature"] = "false",
                ["Audit:Enabled"] = "false",
                ["Cleanup:Enabled"] = "false"
            });
        });

        builder.ConfigureServices((context, services) =>
        {
            // The test server has no ports and no TLS: the listener comes from a header, and the
            // client certificate check is replaced.
            services.RemoveAll<ICallerSystemResolver>();
            services.AddSingleton<ICallerSystemResolver, HeaderCallerSystemResolver>();

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Run the inbound worker inside the same host so the full path is exercised.
            services.AddInboundWorker(context.Configuration);
        });
    }

    /// <summary>The bank's signing identity (the development mock): signs what systems receive.</summary>
    public ISpiMessageSigner Signer => Services.GetRequiredService<ISpiMessageSigner>();

    public InboundSigningOptions InboundSigning => Services.GetRequiredService<IOptions<InboundSigningOptions>>().Value;

    public SpiClient SystemA => new(ClientFor(ListenerKind.SystemA));
    public SpiClient SystemB => new(ClientFor(ListenerKind.SystemB));
    public HttpClient Admin => ClientFor(ListenerKind.Admin);

    public HttpClient ClientFor(ListenerKind? listener)
    {
        var client = CreateClient();
        if (listener is not null)
            client.DefaultRequestHeaders.Add(ListenerHeader, listener.ToString());
        return client;
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Waits until the worker has pulled, routed and stored the inbound message.</summary>
    public async Task<(string Targets, string RouteSource)> WaitForRoutingAsync(string piResourceId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var source = await ScalarAsync<string>($"SELECT RouteSource FROM dbo.SpiInboundMsg WHERE PiResourceId = '{piResourceId}'");
            if (source is not null)
            {
                var targets = await ScalarAsync<string>($"SELECT Targets FROM dbo.SpiInboundMsg WHERE PiResourceId = '{piResourceId}'");
                return (targets ?? string.Empty, source);
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Inbound message {piResourceId} was not routed within 20s.");
    }
}

[CollectionDefinition(Name)]
public sealed class ProxyCollection : ICollectionFixture<ProxyFixture>
{
    public const string Name = "Proxy";
}

internal sealed class HeaderCallerSystemResolver : ICallerSystemResolver
{
    public ListenerKind Resolve(HttpContext context) =>
        Enum.TryParse<ListenerKind>(context.Request.Headers[ProxyFixture.ListenerHeader].ToString(), out var kind)
            ? kind
            : ListenerKind.Unknown;
}

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>A system (A or B) talking to the proxy exactly as it would talk to the SPI.</summary>
public sealed class SpiClient(HttpClient http)
{
    private string _nextPath = $"/api/v1/out/{ProxyFixture.Ispb}/stream/start";

    public HttpClient Http => http;

    public Task<HttpResponseMessage> SendAsync(string xml) =>
        http.PostAsync($"/api/v1/in/{ProxyFixture.Ispb}/msgs", new StringContent(xml, Encoding.UTF8, "application/xml"));

    public Task<HttpResponseMessage> SendMultipartAsync(params string[] xmls)
    {
        var content = new MultipartContent("mixed", "test-boundary");
        foreach (var xml in xmls)
            content.Add(new StringContent(xml, Encoding.UTF8, "application/xml"));
        return http.PostAsync($"/api/v1/in/{ProxyFixture.Ispb}/msgs", content);
    }

    /// <summary>One pull on the current stream; following it acks whatever the previous pull returned.</summary>
    public async Task<IReadOnlyList<(string PiResourceId, string Xml)>> PullOnceAsync(bool advance = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _nextPath);
        request.Headers.TryAddWithoutValidation("Accept", "multipart/mixed");
        using var response = await http.SendAsync(request);

        if (advance && response.Headers.TryGetValues("PI-Pull-Next", out var next))
            _nextPath = next.First();

        if (response.StatusCode == HttpStatusCode.NoContent)
            return [];
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync();
        var parts = await SpiMultipart.ReadPartsAsync(body, response.Content.Headers.ContentType!.ToString(), int.MaxValue)
            ?? throw new InvalidOperationException("Pull response is not valid multipart.");
        return parts.Select(p => (p.PiResourceId!, Encoding.UTF8.GetString(p.Body))).ToList();
    }

    /// <summary>Keeps pulling (and acking) until a message satisfies the predicate.</summary>
    public async Task<string> PullUntilAsync(Func<string, bool> match, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (_, xml) in await PullOnceAsync())
            {
                if (match(xml))
                    return xml;
            }
        }
        throw new TimeoutException("The expected message was not delivered to this system.");
    }

    /// <summary>Pulls for a while and returns everything delivered, to assert a message is NOT there.</summary>
    public async Task<List<string>> DrainAsync(int seconds = 3)
    {
        var all = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
            all.AddRange((await PullOnceAsync()).Select(m => m.Xml));
        return all;
    }
}
