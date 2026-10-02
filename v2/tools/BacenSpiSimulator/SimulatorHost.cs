using ConvivenciaPix.V2.Infrastructure.Simulation;

namespace ConvivenciaPix.V2.BacenSpiSimulator;

/// <summary>
/// The SPI simulator (<see cref="SpiSimulator"/>) served over HTTP, for local development and
/// tests. The /sim endpoints let a developer inject or inspect messages.
/// </summary>
public static class SimulatorHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
        builder.Services.AddSingleton<SimulatorState>();
        builder.Services.AddSingleton<SpiSimulator>();
        configure?.Invoke(builder);

        var app = builder.Build();

        app.Map("/api/v1/{**rest}", async (HttpContext context, SpiSimulator simulator, CancellationToken ct) =>
        {
            var request = context.Request;
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);

            var answer = await simulator.HandleAsync(
                request.Method, request.Path, request.Headers.Accept.ToString(), request.ContentType, body.ToArray(), ct);

            var response = context.Response;
            response.StatusCode = answer.StatusCode;
            foreach (var (name, values) in answer.Headers)
                response.Headers[name] = values;
            if (answer.ContentType is not null)
                response.ContentType = answer.ContentType;
            if (answer.Body.Length > 0)
                await response.Body.WriteAsync(answer.Body, ct);
        });

        app.MapSimulatorDevEndpoints();

        return app;
    }
}
