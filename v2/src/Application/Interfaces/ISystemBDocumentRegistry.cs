namespace ConvivenciaPix.V2.Application.Interfaces;

/// <summary>In-memory view of the documents assigned to System B.</summary>
public interface ISystemBDocumentRegistry
{
    /// <summary>False until the first snapshot has been loaded.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// True when the document is assigned to System B. Throws <see cref="RegistryUnavailableException"/>
    /// if no snapshot was ever loaded — routing by guess would send a customer to the wrong system.
    /// </summary>
    bool Contains(string document);

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class RegistryUnavailableException() : Exception("The System B document registry has not been loaded yet.");
