namespace ConvivenciaPix.Domain.Repositories;

/// <summary>Result of an atomic per-side upsert.</summary>
/// <param name="Row">The current row (the inserted entity, or the merged row re-read after an update).</param>
/// <param name="Inserted">True when this call created the row (first arrival); false when it updated an existing row.</param>
public readonly record struct UpsertOutcome<T>(T Row, bool Inserted);
