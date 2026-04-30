using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.SpiProxyWorker.Consumers;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<SystemAResponseProxyConsumer>();

var host = builder.Build();
host.Run();
