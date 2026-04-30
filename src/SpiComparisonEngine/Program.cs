using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.SpiComparisonEngine.Consumers;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<SpiComparisonConsumer>();

var host = builder.Build();
host.Run();
