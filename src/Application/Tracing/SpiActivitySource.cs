using System.Diagnostics;

namespace ConvivenciaPix.Application.Tracing;

public static class SpiActivitySource
{
    public static readonly ActivitySource Instance = new("ConvivenciaPix", "1.0.0");

    public static Activity? StartProxyActivity(string name) =>
        Instance.StartActivity(name, ActivityKind.Internal);
}
