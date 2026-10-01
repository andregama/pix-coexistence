namespace ConvivenciaPix.V2.Domain.Repositories;

/// <summary>Row of the existing <c>TB_DADOS_CONVIVENCIA</c> table.</summary>
public sealed record SystemBDocumentEntry(int Id, string Conta, string Agencia, string Documento, DateTime DataCriacao);

/// <summary>Access to the registry of customers assigned to System B (<c>TB_DADOS_CONVIVENCIA</c>).</summary>
public interface ISystemBDocumentRepository
{
    Task<IReadOnlyCollection<string>> LoadAllDocumentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SystemBDocumentEntry>> ListAsync(
        string? documento, int skip, int take, CancellationToken cancellationToken = default);

    Task<SystemBDocumentEntry> AddAsync(
        string conta, string agencia, string documento, CancellationToken cancellationToken = default);

    /// <summary>Removes every row whose document matches (compared in normalized form).</summary>
    Task<int> RemoveAsync(string documento, CancellationToken cancellationToken = default);
}
