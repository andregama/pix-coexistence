namespace ConvivenciaPix.V2.SpiProxyApi.Listeners;

/// <summary>
/// Keeps each listener to its purpose: the SPI endpoints answer only on the two system ports, and
/// everything else (administration, metrics, documentation) only on the admin port. A request on
/// the wrong listener gets 404, as if the endpoint did not exist there.
/// </summary>
public sealed class ListenerGateMiddleware(RequestDelegate next, ICallerSystemResolver resolver)
{
    private const string SpiPrefix = "/api/v1";
    private const string HealthPrefix = "/healthz";

    public Task InvokeAsync(HttpContext context)
    {
        var kind = resolver.Resolve(context);
        context.SetListener(kind);

        var path = context.Request.Path;
        var allowed =
            path.StartsWithSegments(HealthPrefix) ? kind != ListenerKind.Unknown
            : path.StartsWithSegments(SpiPrefix) ? kind is ListenerKind.SystemA or ListenerKind.SystemB
            : kind == ListenerKind.Admin;

        if (allowed)
            return next(context);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
