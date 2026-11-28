using System.Data;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin.Migration.Import;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Migration.Mapping;
using Xrmd.Pipeline;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// The archive tables of an import (migration table mapping spec §8.7), created at the start of the schema phase, before the package's
/// solutions: for each map entry <c>archive: true</c> a custom table of the source's ownership and kind (an activity source becomes an
/// activity table), named as the source shows it, holding the source's columns as <c>xrmva_&lt;column&gt;</c> (<see cref="ArchiveColumns"/>),
/// with one generic main form listing them and one active view. No business logic, no other form or view. Everything comes from the
/// package's source catalog, that is from the customer's own environment; nothing of a Microsoft table is stored in xrmverse.
/// <list type="bullet">
/// <item>The publisher <see cref="PublisherFriendlyName"/> (prefix <see cref="PublisherPrefix"/>, option values from
/// <see cref="OptionValuePrefix"/>) and the unmanaged solution <see cref="SolutionName"/> are created when absent, in a transaction of
/// their own (the publisher prefix must be committed before a component takes it).</item>
/// <item>The tables are then created in one transaction through the metadata messages (CreateEntity, CreateAttribute, CreateOneToMany,
/// CreateCustomerRelationships), with their form and view, and each table's ledger chunk <see cref="ChunkKey"/> is marked in it: a
/// resume finds either nothing (and creates everything) or the marks (and creates nothing).</item>
/// <item>A lookup to a table the package's solutions or schema delta bring is created after them (<see cref="CreateLookupsAsync"/>, ledger
/// chunk <see cref="LookupsChunkKey"/>); the census, the dry run and the record translator count those tables as present too.</item>
/// <item>The source's state and status reason become the choice columns <c>xrmva_statecode</c> and <c>xrmva_statuscode</c> with the
/// source's values and labels: the states of an xrmverse table are fixed (two for a table, three for an activity) and xrmverse has no
/// InsertStatusValue, so the archive table keeps its own status model beside the source's (a table's rows active; an activity's rows
/// open, completed or canceled as in the source, with that state's default status reason).</item>
/// </list>
/// This class holds no Microsoft SDK type (S.2): the requests themselves are spelled by <see cref="ArchiveSchemaRequests"/>.
/// </summary>
public static class ArchiveSchema
{
    public const string PublisherUniqueName = "xrmversearchive";
    public const string PublisherFriendlyName = "xrmverse archive";
    public const string PublisherPrefix = "xrmva";
    public const int OptionValuePrefix = 72_000;
    public const string SolutionName = "XrmverseMigrationArchive";

    /// <summary>The language of the labels the archive writes: the organization's base language (xrmverse labels in 1033).</summary>
    private const int Language = MetadataMapTarget.BaseLanguage;

    private const int Phase = SchemaPhase.PhaseOrder;

    /// <summary>The ledger chunk of an archive table, marked in the transaction that creates it.</summary>
    public static string ChunkKey(string archiveTable) => $"schema/archive/{archiveTable}";

    /// <summary>The ledger chunk of the lookups of an archive table created after the solutions and the delta (<see cref="CreateLookupsAsync"/>).</summary>
    public static string LookupsChunkKey(string archiveTable) => $"schema/archive/{archiveTable}/lookups";

    /// <param name="planned">The tables the package's solutions and delta create (<see cref="DryRunPreview.PlannedTablesAsync"/>): a
    /// lookup to one of them is kept, and created by <see cref="CreateLookupsAsync"/> once they exist.</param>
    public static async Task CreateAsync(MigrationPhaseContext context, MapSet maps, SourceCatalog catalog, IReadOnlySet<string> planned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(planned);
        var entries = maps.Tables.Where(t => t.Archive).ToList();
        var pending = new List<MapTable>();
        foreach (var entry in entries)
            if (!await context.Store.IsChunkDoneAsync(context.RunId, ChunkKey(entry.Target), ct)) pending.Add(entry);
        if (pending.Count == 0) return;

        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var metadata = services.GetRequiredService<IMetadataStore>();
        var core = services.GetRequiredService<CoreOperations>();
        await EnsureSolutionAsync(factory, core, context.Caller, ct);

        var sources = catalog.Tables.GroupBy(t => t.LogicalName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var archived = entries.ToDictionary(e => e.Source, StringComparer.Ordinal);
        bool Exists(string table) => archived.ContainsKey(table) || metadata.GetTable(maps.TargetOf(table) ?? table) is not null;
        bool Present(string table) => Exists(table) || planned.Contains(maps.TargetOf(table) ?? table);
        var builds = new List<Build>();
        foreach (var entry in pending)
        {
            if (!sources.TryGetValue(entry.Source, out var source))
                throw new MigrationPhaseException($"The archive table '{entry.Target}' cannot be created: the package does not describe the table '{entry.Source}' (map/source.json).");
            builds.Add(new Build(entry, source, ArchiveColumns.Plan(entry, source, catalog, Present)));
        }

        var operations = services.GetRequiredService<MetadataOperations>();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var s = new DataSession(conn, tx);
        var current = builds[0];
        try
        {
            // Tables and their plain columns first, then the lookups (which may point to another archive table), then forms and views.
            foreach (var build in builds)
            {
                current = build;
                context.Progress.Report($"schema: archive table {build.Entry.Target}");
                await operations.ExecuteAsync(
                    ArchiveSchemaRequests.CreateEntity(build.Entry.Target, build.DisplayName, build.Source, build.Primary, build.PrimaryName, catalog),
                    context.Caller, s, ct);
                foreach (var column in build.Columns.Where(c => c.Kind is ArchiveColumnKind.Copied or ArchiveColumnKind.LookupAsText && c != build.Primary))
                    await operations.ExecuteAsync(ArchiveSchemaRequests.CreateAttribute(build.Entry.Target, build.Source, column, catalog), context.Caller, s, ct);
            }
            foreach (var build in builds)
            {
                current = build;
                // A lookup to a table the solutions or the delta bring waits for them (CreateLookupsAsync).
                foreach (var column in build.Columns.Where(c => c.Kind == ArchiveColumnKind.Lookup && ArchiveColumns.KeepsLookup(c.Source, Exists)))
                    await CreateLookupAsync(operations, context.Caller, s, build, column, maps, archived, sources, metadata, ct);
            }
            foreach (var build in builds)
            {
                current = build;
                var later = build.Columns.Where(c => c.Kind == ArchiveColumnKind.Lookup && !ArchiveColumns.KeepsLookup(c.Source, Exists))
                    .Select(c => c.Target!).ToHashSet(StringComparer.Ordinal);
                await CreateLayoutsAsync(core, metadata, context.Caller, s, build, later, ct);
                await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Info, "archive-created", build.Entry.Target, null, null,
                    $"{build.Entry.Source} → {build.Entry.Target}: archive table created in {SolutionName}."), s, ct);
                await context.Store.MarkChunkAsync(context.RunId, Phase, ChunkKey(build.Entry.Target), 1, 0, s, ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is XrmdFaultException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            s.RolledBack();
            throw new MigrationPhaseException($"The archive table '{current.Entry.Target}' (archive of '{current.Entry.Source}') could not be created: {ex.Message}");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            s.RolledBack();
            throw;
        }
        s.Committed();
    }

    /// <summary>
    /// After the solutions and the schema delta: the lookups of the archive tables to tables those brought, which <see cref="CreateAsync"/>
    /// left for now. Each archive table's <see cref="LookupsChunkKey"/> is marked in the one transaction that creates them, so a resume
    /// creates them once. A lookup whose table still does not exist (its solution or delta item did not create it) becomes the text column
    /// the archive takes otherwise, with a warning <c>archive-lookup-as-text</c>: the translator writes it as text (the archive's columns
    /// decide once it exists), and the main form shows the text column in place of the lookup.
    /// </summary>
    public static async Task CreateLookupsAsync(MigrationPhaseContext context, MapSet maps, SourceCatalog catalog, IReadOnlySet<string> planned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(planned);
        var entries = maps.Tables.Where(t => t.Archive).ToList();
        var pending = new List<MapTable>();
        foreach (var entry in entries)
            if (!await context.Store.IsChunkDoneAsync(context.RunId, LookupsChunkKey(entry.Target), ct)) pending.Add(entry);
        if (pending.Count == 0) return;

        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var metadata = services.GetRequiredService<IMetadataStore>();
        var operations = services.GetRequiredService<MetadataOperations>();
        var core = services.GetRequiredService<CoreOperations>();
        var sources = catalog.Tables.GroupBy(t => t.LogicalName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var archived = entries.ToDictionary(e => e.Source, StringComparer.Ordinal);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var s = new DataSession(conn, tx);
        bool Exists(string table) => archived.ContainsKey(table) || metadata.GetTable(s, maps.TargetOf(table) ?? table) is not null;
        bool Present(string table) => Exists(table) || planned.Contains(maps.TargetOf(table) ?? table);
        MapTable? current = null;
        try
        {
            foreach (var entry in pending)
            {
                current = entry;
                if (sources.GetValueOrDefault(entry.Source) is not { } source) continue;
                var build = new Build(entry, source, ArchiveColumns.Plan(entry, source, catalog, Present));
                var columns = metadata.GetTable(s, entry.Target)?.Attributes?.Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal) ?? [];
                var asText = new HashSet<string>(StringComparer.Ordinal);
                foreach (var column in build.Columns.Where(c => c.Kind == ArchiveColumnKind.Lookup && !columns.Contains(c.Target!) && !columns.Contains(c.TextTarget!)))
                {
                    if (ArchiveColumns.KeepsLookup(column.Source, Exists))
                    {
                        context.Progress.Report($"schema: archive lookup {entry.Target}.{column.Target}");
                        await CreateLookupAsync(operations, context.Caller, s, build, column, maps, archived, sources, metadata, ct);
                        continue;
                    }
                    var text = column with { Kind = ArchiveColumnKind.LookupAsText };
                    asText.Add(column.Target!);
                    await operations.ExecuteAsync(ArchiveSchemaRequests.CreateAttribute(entry.Target, build.Source, text, catalog), context.Caller, s, ct);
                    await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Warning, "archive-lookup-as-text", entry.Target, null, text.TextTarget,
                        $"{entry.Source}.{column.Source.LogicalName}: the package's solutions and schema delta did not create {string.Join(", ", column.Source.Targets.Select(t => maps.TargetOf(t) ?? t))}; " +
                        "the lookup is kept as the referenced rows' names."), s, ct);
                }
                // The main form listed the lookup that is not created: it shows the text column instead.
                if (asText.Count > 0) await ReplaceFormAsync(core, context.Caller, s, build with
                {
                    Columns = [.. build.Columns.Select(c => c.Kind == ArchiveColumnKind.Lookup && asText.Contains(c.Target!) ? c with { Kind = ArchiveColumnKind.LookupAsText } : c)],
                }, ct);
                await context.Store.MarkChunkAsync(context.RunId, Phase, LookupsChunkKey(entry.Target), 1, 0, s, ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is XrmdFaultException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            s.RolledBack();
            throw new MigrationPhaseException($"The lookups of the archive table '{current?.Target}' (archive of '{current?.Source}') could not be created: {ex.Message}");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            s.RolledBack();
            throw;
        }
        s.Committed();
    }

    /// <summary>One archive table to create: its map entry, its source and its columns; <see cref="Primary"/> is created with the table.</summary>
    private sealed record Build(MapTable Entry, SourceCatalogTable Source, IReadOnlyList<ArchiveColumn> Columns)
    {
        /// <summary>
        /// The primary name of a table: a text column of the source (<c>name</c>, then <c>fullname</c>, <c>title</c>, <c>subject</c>, then the
        /// first); null for an activity (its primary name is <c>subject</c>) or a source without text (a column of its own is made).
        /// </summary>
        public ArchiveColumn? Primary { get; } = Source.IsActivity ? null
            : Columns.Where(c => c.Kind == ArchiveColumnKind.Copied && c.Source.AttributeType == "String")
                .OrderBy(c => Array.IndexOf(PrimaryNames, c.Source.LogicalName) is var i && i >= 0 ? i : PrimaryNames.Length)
                .FirstOrDefault();

        private static readonly string[] PrimaryNames = ["name", "fullname", "title", "subject"];

        public string DisplayName => string.IsNullOrWhiteSpace(Source.DisplayName) ? Source.LogicalName : Source.DisplayName;

        /// <summary>The primary name column's logical name.</summary>
        public string PrimaryName => Source.IsActivity ? "subject" : Primary?.Target ?? ArchiveNames.Column("primaryname");

        /// <summary>The primary key the server gives the table.</summary>
        public string PrimaryKey => Source.IsActivity ? "activityid" : Entry.Target + "id";
    }

    /// <summary>A lookup the archive keeps: to the table of its source's target under its target name (mapped, archived or present).</summary>
    private static Task CreateLookupAsync(MetadataOperations operations, Caller caller, DataSession s, Build build, ArchiveColumn column, MapSet maps,
        Dictionary<string, MapTable> archived, Dictionary<string, SourceCatalogTable> sources, IMetadataStore metadata, CancellationToken ct)
    {
        var c = column.Source;
        string Referenced(string source) => maps.TargetOf(source) ?? source;
        string KeyOf(string source) => archived.TryGetValue(source, out var entry)
            ? (sources.GetValueOrDefault(source)?.IsActivity == true ? "activityid" : entry.Target + "id")
            : metadata.GetTable(Referenced(source))?.PrimaryIdAttribute
              ?? throw new InvalidOperationException($"The table '{Referenced(source)}' of the lookup '{build.Source.LogicalName}.{c.LogicalName}' does not exist.");
        return operations.ExecuteAsync(ArchiveSchemaRequests.Lookup(build.Entry.Target, build.Source, column, Referenced, KeyOf), caller, s, ct);
    }

    // ---- publisher and solution -------------------------------------------------------------------------------------------------

    /// <summary>The publisher and the unmanaged solution of the archive tables, each created when absent, committed at once.</summary>
    private static async Task EnsureSolutionAsync(IDbConnectionFactory factory, CoreOperations core, Caller caller, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var s = new DataSession(conn, tx);
        try
        {
            var publisher = await IdAsync(s, "publisher", PublisherUniqueName, ct);
            if (publisher is null)
            {
                publisher = Guid.NewGuid();
                await CoreSdkBoundary.ExecuteCoreAsync(core,
                    ArchiveSchemaRequests.CreatePublisher(publisher.Value, PublisherUniqueName, PublisherFriendlyName, PublisherPrefix, OptionValuePrefix),
                    caller, s, ct: ct);
            }
            if (await IdAsync(s, "solution", SolutionName, ct) is null)
                await CoreSdkBoundary.ExecuteCoreAsync(core, ArchiveSchemaRequests.CreateSolution(SolutionName, "xrmverse migration archive", "1.0.0.0", publisher.Value,
                    "The archive tables an import created for source tables this organization does not have."), caller, s, ct: ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            s.RolledBack();
            throw;
        }
        s.Committed();
    }

    private static async Task<Guid?> IdAsync(DataSession s, string table, string uniqueName, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(table + "id")} FROM {s.DataTable(table)} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}");
        cmd.AddParameter("@name", DbType.String, uniqueName);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    // ---- form and view ----------------------------------------------------------------------------------------------------------

    private const string TextControl = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";
    private const string MemoControl = "{E0DECE4B-6FC8-4a8f-A065-082708572369}";
    private const string LookupControl = "{270BD3DB-D9AF-4782-9025-509E298DEC0A}";
    private const string ChoiceControl = "{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}";
    private const string DateControl = "{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}";
    private const string TwoOptionsControl = "{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}";
    private const string WholeNumberControl = "{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}";
    private const string DecimalControl = "{C3EFE0C3-0EC6-42be-8349-CBD9079DFD8E}";
    private const string FloatControl = "{0D2C745A-E5A8-4c8f-BA63-C6D3BB604660}";
    private const string MoneyControl = "{533B9E00-756B-4312-95A0-DC888637AC78}";
    private const string MultiSelectControl = "{4AA28AB7-9C13-4F57-A73D-AD894D048B5F}";
    private const string PartyListControl = "{CBFB742C-14E7-4a17-96BB-1A13F7F64AA2}";

    /// <summary>One main form with every column of the archive (one section) and one active view, in the archive solution.</summary>
    /// <param name="later">Lookups created after the solutions (<see cref="CreateLookupsAsync"/>): on the form, not in the view.</param>
    private static async Task CreateLayoutsAsync(CoreOperations core, IMetadataStore metadata, Caller caller, DataSession s, Build build,
        IReadOnlySet<string> later, CancellationToken ct)
    {
        var table = build.Entry.Target;
        var fields = Fields(build);
        var formId = Guid.NewGuid();
        await CoreSdkBoundary.ExecuteCoreAsync(core, ArchiveSchemaRequests.CreateMainForm(formId, build.DisplayName, table, FormXml(formId, build.DisplayName, fields)),
            caller, s, ct: ct);

        var columns = fields.Where(f => f.ClassId != PartyListControl && !later.Contains(f.Column)).Take(6).Select(f => f.Column).ToList();
        var code = metadata.GetTable(s, table)?.ObjectTypeCode ?? 0;
        var fetchXml = $"<fetch version=\"1.0\" mapping=\"logical\"><entity name=\"{table}\">" +
                string.Concat(columns.Select(c => $"<attribute name=\"{c}\" />")) + $"<attribute name=\"{build.PrimaryKey}\" />" +
                $"<order attribute=\"{build.PrimaryName}\" descending=\"false\" />" +
                "<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter></entity></fetch>";
        var layoutXml = $"<grid name=\"resultset\" object=\"{code}\" jump=\"{build.PrimaryName}\" select=\"1\" icon=\"1\" preview=\"1\">" +
                $"<row name=\"result\" id=\"{build.PrimaryKey}\">" + string.Concat(columns.Select(c => $"<cell name=\"{c}\" width=\"{(c == build.PrimaryName ? 200 : 150)}\" />")) +
                "</row></grid>";
        await CoreSdkBoundary.ExecuteCoreAsync(core, ArchiveSchemaRequests.CreateView("Active " + build.DisplayName, table, fetchXml, layoutXml), caller, s, ct: ct);
    }

    /// <summary>Writes the main form of the archive table again from <paramref name="build"/>'s columns (same form id, same layout).</summary>
    private static async Task ReplaceFormAsync(CoreOperations core, Caller caller, DataSession s, Build build, CancellationToken ct)
    {
        Guid formId;
        await using (var cmd = s.Command(
            $"SELECT {s.Quote("systemformid")} FROM {s.DataTable("systemform")} WHERE {s.Quote("objecttypecode")} = @table AND {s.Quote("type")} = 2"))
        {
            cmd.AddParameter("@table", DbType.String, build.Entry.Target);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id) return;
            formId = id;
        }
        await CoreSdkBoundary.ExecuteCoreAsync(core, ArchiveSchemaRequests.UpdateFormXml(formId, FormXml(formId, build.DisplayName, Fields(build))), caller, s, ct: ct);
    }

    /// <summary>The columns of the form: the primary name, the archive's own columns in the source's order, then the owner.</summary>
    private static List<(string Column, string Label, string ClassId)> Fields(Build build)
    {
        var fields = new List<(string Column, string Label, string ClassId)>();
        var primaryLabel = build.Source.IsActivity
            ? build.Source.Columns.FirstOrDefault(c => c.LogicalName == "subject")?.DisplayName ?? "subject"
            : build.Primary?.Source.DisplayName ?? "Name";
        fields.Add((build.PrimaryName, primaryLabel, TextControl));
        foreach (var column in build.Columns)
        {
            var c = column.Source;
            var label = c.DisplayName ?? c.LogicalName;
            switch (column.Kind)
            {
                case ArchiveColumnKind.Copied when column != build.Primary:
                    fields.Add((column.Target!, label, column.IsMultiSelect ? MultiSelectControl : ControlOf(c.AttributeType)));
                    break;
                case ArchiveColumnKind.Lookup:
                    fields.Add((column.Target!, label, LookupControl));
                    break;
                case ArchiveColumnKind.LookupAsText:
                    fields.Add((column.TextTarget!, label, TextControl));
                    break;
                case ArchiveColumnKind.ByName when build.Source.IsActivity && column.Target != "subject"
                                                 && (c.AttributeType == "PartyList" || RecordTranslator.ActivityColumns.Contains(column.Target!)):
                    fields.Add((column.Target!, label, c.AttributeType == "PartyList" ? PartyListControl : ControlOf(c.AttributeType)));
                    break;
            }
        }
        if (build.Source.Ownership != "organization") fields.Add(("ownerid", "Owner", LookupControl));
        return fields;
    }

    private static string ControlOf(string type) => type switch
    {
        "Memo" => MemoControl,
        "Integer" or "BigInt" => WholeNumberControl,
        "Decimal" => DecimalControl,
        "Double" => FloatControl,
        "Money" => MoneyControl,
        "Boolean" => TwoOptionsControl,
        "DateTime" => DateControl,
        "Picklist" or "State" or "Status" => ChoiceControl,
        "Lookup" or "Customer" or "Owner" => LookupControl,
        _ => TextControl,
    };

    private static string FormXml(Guid formId, string title, List<(string Column, string Label, string ClassId)> fields)
    {
        string Id(string key) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{formId:D}/{key}"))).ToString("B").ToUpperInvariant();
        static string Labels(string text) => $"<labels><label description=\"{SecurityElement.Escape(text)}\" languagecode=\"{Language}\"/></labels>";
        var rows = string.Concat(fields.Select(f =>
            $"<row><cell id=\"{Id("cell:" + f.Column)}\" showlabel=\"true\">{Labels(f.Label)}" +
            $"<control id=\"{f.Column}\" classid=\"{f.ClassId}\" datafieldname=\"{f.Column}\"/></cell></row>"));
        return $"<form><tabs><tab name=\"general\" id=\"{Id("tab:general")}\" expanded=\"true\" showlabel=\"false\" verticallayout=\"true\">" +
               Labels(title) + "<columns><column width=\"100%\"><sections>" +
               $"<section name=\"general\" id=\"{Id("section:general")}\" showlabel=\"false\" showbar=\"false\" columns=\"1\">" + Labels(title) +
               "<rows>" + rows + "</rows></section></sections></column></columns></tab></tabs></form>";
    }
}
