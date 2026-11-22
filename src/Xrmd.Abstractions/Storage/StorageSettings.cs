namespace Xrmd.Abstractions.Storage;

/// <summary>The <c>Storage:Provider</c> setting (plan 2.7a, Global Constraints): the engine of every organization of the server.</summary>
public static class StorageSettings
{
    /// <summary>The configuration key of the provider (environment variable <c>Storage__Provider</c>).</summary>
    public const string ProviderKey = "Storage:Provider";

    /// <summary>The refusal of a value other than <c>SqlServer</c> or <c>PostgreSql</c> at startup.</summary>
    public const string InvalidProviderMessage = "Storage:Provider must be SqlServer or PostgreSql.";

    /// <summary>
    /// The provider <paramref name="value"/> names, ignoring case; <see cref="StorageProvider.SqlServer"/> when it is absent or blank.
    /// </summary>
    /// <exception cref="InvalidOperationException">Any other value (<see cref="InvalidProviderMessage"/>).</exception>
    public static StorageProvider ParseProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return StorageProvider.SqlServer;
        if (string.Equals(value.Trim(), nameof(StorageProvider.SqlServer), StringComparison.OrdinalIgnoreCase)) return StorageProvider.SqlServer;
        if (string.Equals(value.Trim(), nameof(StorageProvider.PostgreSql), StringComparison.OrdinalIgnoreCase)) return StorageProvider.PostgreSql;
        throw new InvalidOperationException(InvalidProviderMessage);
    }
}
