using ConvivenciaPix.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/healthz", () => Results.Ok()).WithName("Health");

app.Run();
