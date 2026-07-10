using Xunit;

namespace ConvivenciaPix.Integration.Tests;

/// <summary>
/// Shares a single <see cref="IntegrationTestFixture"/> (SQL + Kafka + Redis containers and the
/// in-process worker pipeline) across every integration test class, so the containers spin up once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
    public const string Name = "Integration";
}
