namespace ConvivenciaPix.Infrastructure.Outbound;

/// <summary>DI key for the DICT proxy's outbound Polly <c>ResiliencePipeline</c> (retry + timeout).</summary>
internal static class DictResilience
{
    public const string Key = "dict-outbound";
}
