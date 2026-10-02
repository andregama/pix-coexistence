using Microsoft.Extensions.Configuration;

namespace ConvivenciaPix.V2.Infrastructure.Persistence;

public enum StorageProvider
{
    /// <summary>The coexistence database (<c>ConnectionStrings:SqlServer</c>).</summary>
    SqlServer,

    /// <summary>
    /// Everything in the memory of one process: no SQL Server, Redis or Kafka. For quick local runs
    /// and tests only — nothing survives a restart, so it is refused in Production.
    /// </summary>
    InMemory
}

public sealed class StorageOptions
{
    public StorageProvider Provider { get; set; } = StorageProvider.SqlServer;

    public static bool IsInMemory(IConfiguration configuration) =>
        (configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions()).Provider == StorageProvider.InMemory;
}
