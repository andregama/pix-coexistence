using Microsoft.Extensions.Hosting;
using ConvivenciaPix.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

var host = builder.Build();
host.Run();
