using ConvivenciaPix.Infrastructure;
using ConvivenciaPix.SpiCorrelateWorker.Consumers;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<SystemBSentConsumer>();
builder.Services.AddHostedService<SystemAResponseCorrelateConsumer>();

var host = builder.Build();
host.Run();
