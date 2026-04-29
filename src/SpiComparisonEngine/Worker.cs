using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConvivenciaPix.SpiComparisonEngine;

public class Worker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }
}
