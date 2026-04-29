namespace ConvivenciaPix.Domain.ValueObjects;

public sealed class CorrelationSource : IEquatable<CorrelationSource>
{
    public static readonly CorrelationSource Orchestrator = new("Orchestrator");
    public static readonly CorrelationSource Heuristic = new("Heuristic");

    public string Value { get; }

    private CorrelationSource(string value) => Value = value;

    public static CorrelationSource From(string value) => value switch
    {
        "Orchestrator" => Orchestrator,
        "Heuristic" => Heuristic,
        _ => throw new ArgumentException($"Invalid CorrelationSource value: '{value}'", nameof(value))
    };

    public bool Equals(CorrelationSource? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is CorrelationSource other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static implicit operator string(CorrelationSource source) => source.Value;
}
