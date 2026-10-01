namespace ConvivenciaPix.V2.Domain.Routing;

public static class CustomerDocument
{
    /// <summary>
    /// Canonical form used to compare documents: punctuation removed, upper-cased. Letters are kept
    /// because the alphanumeric CNPJ is not digits-only.
    /// </summary>
    public static string Normalize(string? document)
    {
        if (string.IsNullOrEmpty(document)) return string.Empty;

        Span<char> buffer = document.Length <= 64 ? stackalloc char[document.Length] : new char[document.Length];
        var length = 0;
        foreach (var c in document)
        {
            if (char.IsAsciiLetterOrDigit(c))
                buffer[length++] = char.ToUpperInvariant(c);
        }
        return new string(buffer[..length]);
    }
}
