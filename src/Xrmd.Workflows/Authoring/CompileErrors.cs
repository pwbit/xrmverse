using Xrmd.Schema;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// One reason a workflow document does not compile (plan 2.21, A10). <paramref name="At"/> is the line and column of the YAML
/// node it comes from; null when it comes from the compiled XAML (the 2.14 parser or validator).
/// </summary>
public sealed record CompileError(SourceSpan? At, string Message)
{
    public override string ToString() => At is { } at ? $"({at}): {Message}" : Message;
}

/// <summary>A workflow document does not compile: every error of the file, together (A10). Nothing was written.</summary>
public sealed class WorkflowCompileException(IReadOnlyList<CompileError> errors)
    : Exception(errors.Count == 1 ? errors[0].ToString() : $"The workflow has {errors.Count} errors:{Environment.NewLine}" + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<CompileError> Errors { get; } = errors;
}

/// <summary>
/// A compiled workflow document: the <c>workflow</c> row to store (<see cref="WorkflowRow.Xaml"/> is <see cref="Xaml"/>), the XAML,
/// the definition the 2.14 parser reads from it, and the catalog shapes it uses that no real export has confirmed (A10).
/// </summary>
public sealed record CompileResult(WorkflowRow Row, string Xaml, WorkflowDefinition Definition, IReadOnlyList<string> AssumedShapes);
