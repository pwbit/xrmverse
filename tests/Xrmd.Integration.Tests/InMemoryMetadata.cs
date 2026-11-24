using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>
/// A definition change the API refuses (securing a seeded column, a third state, a table flag without its side effects), made in
/// memory for one test. The store's definitions are shared and read-only (S.2b2 Task 4): a changed copy replaces the published
/// one; the test undoes its change the same way. The change does not bump the store's <c>VersionStamp</c>: a cache keyed on the
/// stamp keeps its old derived data, so do not use this helper for a setting such a cache reads.
/// </summary>
internal static class InMemoryMetadata
{
    /// <summary>Publishes a copy of <paramref name="table"/> changed by <paramref name="change"/>.</summary>
    public static void Change(IMetadataStore store, string table, Action<TableDefinition> change) =>
        ((MetadataStore)store).ReplaceInMemory(table, change);

    /// <summary>Publishes a copy of <paramref name="table"/> with its column <paramref name="column"/> changed.</summary>
    public static void ChangeColumn(IMetadataStore store, string table, string column, Action<ColumnDefinition> change) =>
        Change(store, table, t => change(t.Attributes!.Single(a => a.LogicalName == column)));
}
