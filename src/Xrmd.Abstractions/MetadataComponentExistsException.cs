namespace Xrmd.Abstractions;

/// <summary>
/// A metadata create named a component that already exists (an entity, its entity set, an attribute, a relationship, a
/// global option set). The metadata front ends answer it, and only it, with 412 DuplicateRecord: any other
/// <see cref="InvalidOperationException"/> of the store is a fault of the server, not a duplicate.
/// </summary>
public sealed class MetadataComponentExistsException : InvalidOperationException
{
    public MetadataComponentExistsException(string message) : base(message) { }
}
