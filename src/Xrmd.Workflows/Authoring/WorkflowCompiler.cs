using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Schema;
using Xrmd.Workflows.Xaml;
using D = Xrmd.Workflows.Authoring.DesignerXaml;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// Compiles a workflow document into the <c>workflow</c> row and the designer XAML of the classic designer (plan 2.21, A6–A11). It
/// first resolves and types every table, column, record, child workflow and literal against the organization
/// (<see cref="IAuthoringEnvironment"/>), collecting every error with its YAML line and column; only a document without errors is
/// written (<see cref="DesignerXaml"/>). The XAML is then parsed by the 2.14 parser and validated against the row
/// (<see cref="WorkflowValidator"/>): an unsupported step or a validator message is a compile error too (A10).
/// </summary>
/// <remarks>
/// <c>kind: workflow</c> (category 0), <c>kind: businessRule</c> (category 2, entity scope) and <c>kind: action</c> (category 3,
/// plan 2.21 Task 3) share the resolution and the writer (<see cref="Binder"/> and <see cref="DesignerXaml"/>).
/// </remarks>
public static partial class WorkflowCompiler
{
    /// <summary>The <c>introducedversion</c> without a solution (Global Constraints).</summary>
    public const string DefaultIntroducedVersion = "1.0.0.0";

    /// <summary>Reads <paramref name="yaml"/> and compiles it; a YAML error is a <see cref="WorkflowCompileException"/> with its position.</summary>
    public static async Task<CompileResult> CompileAsync(string yaml, IAuthoringEnvironment env, string? solution, CancellationToken ct)
    {
        WorkflowDocument document;
        try
        {
            document = WorkflowYaml.Read(yaml);
        }
        catch (SchemaFormatException ex)
        {
            throw new WorkflowCompileException([new CompileError(new SourceSpan((int)ex.Line, (int)ex.Column), PositionPrefix().Replace(ex.Message, ""))]);
        }
        return await CompileAsync(document, env, solution, ct);
    }

    /// <summary>Compiles <paramref name="d"/>.</summary>
    /// <exception cref="WorkflowCompileException">Every error of the document (nothing is written).</exception>
    public static async Task<CompileResult> CompileAsync(WorkflowDocument d, IAuthoringEnvironment env, string? solution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(env);

        var id = d.Id ?? WorkflowIdentity.IdOf(d.Kind, d.Entity, d.Name);
        var binder = new Binder(d, env, ct);
        var process = await binder.BindAsync(id);
        var errors = binder.Errors;
        // The introduced version is the solution's when the workflow is compiled into one (review I3).
        var version = DefaultIntroducedVersion;
        if (solution is not null)
        {
            if (await env.SolutionVersionAsync(solution, ct) is { } solutionVersion)
                version = solutionVersion;
            else
                errors.Add(new CompileError(null, $"The solution '{solution}' does not exist, so its version cannot be the workflow's introduced version."));
        }
        // An action's message is the publisher's prefix and the uniquename (A15).
        string? uniqueName = null;
        if (d.Kind == WorkflowKind.Action)
        {
            var prefix = await env.PublisherPrefixAsync(id, solution, ct);
            try
            {
                uniqueName = SplitMessage(d.Message ?? "", prefix);
            }
            catch (XrmdFaultException ex)
            {
                errors.Add(new CompileError(d.MessageSpan ?? d.Span, ex.Message));
            }
        }
        if (errors.Count > 0 || process is null)
            throw new WorkflowCompileException(errors);

        var (xaml, written) = D.WriteWithShapes(process);
        var row = Row(d, id, xaml) with { IntroducedVersion = version, UniqueName = uniqueName };
        return Check(row, xaml, env.WorkflowSdkAvailable, written);
    }

    /// <summary>
    /// The <c>uniquename</c> of an action whose message is <paramref name="message"/> (A15): the message without the publisher prefix
    /// <paramref name="prefix"/> and its <c>_</c>, as the export stores it (<c>xrmv_Recalculate</c> → <c>Recalculate</c>), so that 2.14
    /// names the message <paramref name="message"/> again (<c>ActionMessages.MessageName</c>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when the message does not start with <paramref name="prefix"/> and <c>_</c> (naming both
    /// prefixes), when the rest is not a uniquename (letters, digits and <c>_</c>), or when it starts with the prefix again.</exception>
    public static string SplitMessage(string message, string prefix)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var underscore = message.IndexOf('_', StringComparison.Ordinal);
        if (underscore <= 0)
            throw XrmdFaultException.InvalidArgument(
                $"The message '{message}' has no publisher prefix: an action's message starts with '{prefix}_', the prefix of the publisher of its solution.");
        var actual = message[..underscore];
        var unique = message[(underscore + 1)..];
        if (!string.Equals(actual, prefix, StringComparison.Ordinal))
            throw XrmdFaultException.InvalidArgument(
                $"The message '{message}' has the prefix '{actual}', but the publisher of the action's solution has the prefix '{prefix}': name it '{prefix}_{unique}'.");
        if (unique.Length == 0 || !CustomApiRules.HasValidNameCharacters(unique))
            throw XrmdFaultException.InvalidArgument($"The message '{message}' must be '{prefix}_' followed by letters, digits and '_' only.");
        // One case rule (publisher prefixes are lowercase): the prefix and its repetition are compared case-sensitively.
        if (unique.StartsWith(prefix + "_", StringComparison.Ordinal))
            throw XrmdFaultException.InvalidArgument($"The message '{message}' repeats the prefix '{prefix}_': the action's message would be '{unique}'.");
        return unique;
    }

    // ---- the row (Global Constraints: the defaults of the real export) ---------------------------------------------------------

    private static WorkflowRow Row(WorkflowDocument d, Guid id, string xaml)
    {
        var row = new WorkflowRow
        {
            Id = id,
            Name = d.Name,
            Description = d.Description,
            Type = 1,
            Scope = 4,
            RunAs = 1,
            PrimaryEntity = d.Entity,
            Xaml = xaml,
            IsTransacted = true,
            IsCustomizable = true,
            IntroducedVersion = DefaultIntroducedVersion,
            StateCode = 0,
        };
        return d.Kind switch
        {
            // Export Newbusinessrule: real-time, organization, entity scope, no stage, failures not logged.
            WorkflowKind.BusinessRule => row with { Category = 2, Mode = 1, SyncWorkflowLogOnFailure = false, ProcessTriggerScope = 2 },
            // Export Recalculate: background mode stored, organization, create stage 40, failures logged; the uniquename is set by the caller.
            WorkflowKind.Action => row with { Category = 3, Mode = 0, CreateStage = 40, SyncWorkflowLogOnFailure = true },
            _ => WorkflowRowOf(d, id, xaml),
        };
    }

    private static WorkflowRow WorkflowRowOf(WorkflowDocument d, Guid id, string xaml)
    {
        var o = d.Options;
        var realtime = o.Mode == "realtime";
        var trigger = d.Trigger;
        return new WorkflowRow
        {
            Id = id,
            Name = d.Name,
            Description = d.Description,
            Category = 0,
            Type = 1,
            Mode = realtime ? 1 : 0,
            Scope = o.Scope switch
            {
                "businessUnit" => 2,
                "parentChildBusinessUnits" => 3,
                "organization" => 4,
                _ => 1,
            },
            PrimaryEntity = d.Entity,
            Xaml = xaml,
            TriggerOnCreate = trigger?.Create is not null,
            TriggerOnDelete = trigger?.Delete is not null,
            TriggerOnUpdateAttributes = trigger?.Update is { } update ? string.Join(",", update.Columns) : null,
            // A real-time workflow has a create stage even without a create trigger (export Child); the update and delete stages
            // follow A4 (post 40 by default, pre 20; delete only pre).
            CreateStage = realtime ? (trigger?.Create?.Stage == "pre" ? 20 : 40) : null,
            UpdateStage = realtime && trigger?.Update is { } u ? (u.Stage == "pre" ? 20 : 40) : null,
            DeleteStage = realtime && trigger?.Delete is not null ? 20 : null,
            RunAs = realtime && o.RunAs == "owner" ? 0 : 1,
            OnDemand = o.OnDemand,
            Subprocess = o.ChildProcess,
            AsyncAutoDelete = !realtime && o.AutoDelete,
            SyncWorkflowLogOnFailure = realtime && o.LogFailures,
            IsTransacted = true,
            IsCustomizable = true,
            IntroducedVersion = DefaultIntroducedVersion,
            StateCode = 0,
            ProcessTriggerScope = null,
        };
    }

    // ---- the final check (A10) ---------------------------------------------------------------------------------------------------

    private static CompileResult Check(WorkflowRow row, string xaml, bool workflowSdkAvailable, IReadOnlySet<string> written)
    {
        WorkflowDefinition definition;
        IReadOnlySet<string> used;
        try
        {
            definition = XamlWorkflowParser.Parse(xaml, row.Category, workflowSdkAvailable);
            used = XamlWorkflowParser.CatalogEntriesUsed(xaml, row.Category, workflowSdkAvailable);
        }
        catch (XamlWorkflowException ex)
        {
            throw new WorkflowCompileException([new CompileError(null, ex.Message)]);
        }

        var errors = definition.Unsupported
            .Select(u => new CompileError(null, $"The step {u.Label} compiles to XAML xrmverse cannot run ({u.Element}): {u.Reason}"))
            .Concat(WorkflowValidator.Validate(definition, row).Select(m => new CompileError(null, m)))
            .ToList();
        if (errors.Count > 0)
            throw new WorkflowCompileException(errors);

        // The shapes no real export has confirmed (A10): catalog entries of source assumed, the Learn types the export never used,
        // and the shapes the writer reports (review M1, M7).
        var assumed = ActivityCatalog.Entries
            .Where(e => e.Source != "export" && used.Contains(e.Element))
            .Select(e => ShapeName(e.Element))
            .Concat(written)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new CompileResult(row, xaml, definition, assumed);
    }

    /// <summary>A catalog entry as the XAML spells it: <c>mxswa:AssignEntity</c> for an element, the entry itself for a shape.</summary>
    private static string ShapeName(string element)
    {
        if (!element.StartsWith('{'))
            return element;
        var name = XName.Get(element);
        var prefix = name.NamespaceName switch
        {
            ActivityCatalog.SdkActivitiesNs => "mxswa:",
            ActivityCatalog.ClientActivitiesNs => "mcwc:",
            ActivityCatalog.SdkWorkflowNs => "mxsw:",
            _ => "",
        };
        return prefix + name.LocalName;
    }

    /// <summary>A name the designer generates for a step (A8) or its variables: a step kind's prefix and its number.</summary>
    [GeneratedRegex(@"^(Create|Update|Assign|SetState|SendEmail|Condition|ConditionBranch|Wait|WaitBranch|TimeoutBranch|StopWorkflow|ChildWorkflow|Action|CustomActivity|AssignOutputArgument)Step[0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedStepName();

    /// <summary>The "(line l, column c): " a <see cref="SchemaFormatException"/> message starts with.</summary>
    [GeneratedRegex(@"^\(line \d+, column \d+\): ")]
    private static partial Regex PositionPrefix();

    /// <summary>Text the XAML can hold: XML 1.0 characters only (a control character cannot be written).</summary>
    internal static bool IsXmlText(string text)
    {
        try
        {
            XmlConvert.VerifyXmlChars(text);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
