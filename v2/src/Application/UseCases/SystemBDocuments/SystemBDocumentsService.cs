using ConvivenciaPix.V2.Application.Interfaces;
using ConvivenciaPix.V2.Domain.Repositories;
using ConvivenciaPix.V2.Domain.Routing;

namespace ConvivenciaPix.V2.Application.UseCases.SystemBDocuments;

public sealed record NewSystemBDocument(string? Conta, string? Agencia, string? Documento);

public interface ISystemBDocumentsService
{
    Task<IReadOnlyList<SystemBDocumentEntry>> ListAsync(
        string? documento, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Adds the entries; throws <see cref="ArgumentException"/> when one is invalid.</summary>
    Task<IReadOnlyList<SystemBDocumentEntry>> AddAsync(
        IReadOnlyList<NewSystemBDocument> entries, CancellationToken cancellationToken = default);

    Task<int> RemoveAsync(string documento, CancellationToken cancellationToken = default);
}

/// <summary>Administration of the customers assigned to System B.</summary>
public sealed class SystemBDocumentsService(
    ISystemBDocumentRepository repository, ISystemBDocumentRegistry registry) : ISystemBDocumentsService
{
    public const int MaxPageSize = 500;

    public Task<IReadOnlyList<SystemBDocumentEntry>> ListAsync(
        string? documento, int skip, int take, CancellationToken cancellationToken = default) =>
        repository.ListAsync(
            string.IsNullOrWhiteSpace(documento) ? null : CustomerDocument.Normalize(documento),
            Math.Max(0, skip),
            Math.Clamp(take, 1, MaxPageSize),
            cancellationToken);

    public async Task<IReadOnlyList<SystemBDocumentEntry>> AddAsync(
        IReadOnlyList<NewSystemBDocument> entries, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.Conta) || string.IsNullOrWhiteSpace(entry.Agencia))
                throw new ArgumentException($"Entry {i}: Conta and Agencia are required.");
            if (CustomerDocument.Normalize(entry.Documento).Length == 0)
                throw new ArgumentException($"Entry {i}: Documento is required.");
            if (entry.Conta.Length > 50 || entry.Agencia.Length > 50 || entry.Documento!.Length > 50)
                throw new ArgumentException($"Entry {i}: Conta, Agencia and Documento are limited to 50 characters.");
        }

        var added = new List<SystemBDocumentEntry>(entries.Count);
        foreach (var entry in entries)
        {
            added.Add(await repository.AddAsync(
                entry.Conta!.Trim(), entry.Agencia!.Trim(), entry.Documento!.Trim(), cancellationToken));
        }

        await registry.RefreshAsync(cancellationToken);
        return added;
    }

    public async Task<int> RemoveAsync(string documento, CancellationToken cancellationToken = default)
    {
        var normalized = CustomerDocument.Normalize(documento);
        if (normalized.Length == 0)
            throw new ArgumentException("Documento is required.");

        var removed = await repository.RemoveAsync(normalized, cancellationToken);
        await registry.RefreshAsync(cancellationToken);
        return removed;
    }
}
