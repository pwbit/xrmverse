namespace Xrmd.Abstractions.Storage;

/// <summary>
/// The database engine an organization is stored on (plan 2.7a, ruling P3), selected by the <c>Storage:Provider</c>
/// setting. Engine-specific SQL text and behaviour belong to the engine's dialect, never to a check of this value outside
/// <c>Storage/</c>.
/// </summary>
public enum StorageProvider
{
    SqlServer,
    PostgreSql,
}
