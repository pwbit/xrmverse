using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// R11 and R11b of the migration table mapping (spec §8.3, GAP-27 task 10): the components of a mapped source table and of a
/// Microsoft table the organization lacks are taken out of a customer solution before it is imported, each one listed; lookups and
/// N:N of other tables to a mapped table are retargeted; a managed solution with such components fails the run. Invented names.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationSolutionMapTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-gap27-r11-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Customer = "xvcust";

    private const string LeadMap = """
        version: 1
        tables:
          - source: lead
            target: xv_lead
            columns: { subject: xv_name }
        """;

    private static readonly Guid FormId = Guid.Parse("5d0e6a1c-2b3c-4d5e-8f60-71a2b3c4d501"), ViewId = Guid.Parse("5d0e6a1c-2b3c-4d5e-8f60-71a2b3c4d502"),
        ChartId = Guid.Parse("5d0e6a1c-2b3c-4d5e-8f60-71a2b3c4d503"), QuoteFormId = Guid.Parse("5d0e6a1c-2b3c-4d5e-8f60-71a2b3c4d504");

    // ---- the customer solution -------------------------------------------------------------------------------------------

    /// <summary>
    /// The customer solution of the R11 tests: the <c>lead</c> node (custom column <c>new_score</c>, a form, a view, a chart and a
    /// ribbon), a lookup <c>lead.new_accountid</c> to <c>account</c>, the lookup <c>account.new_leadid</c> to <c>lead</c>, the custom
    /// table <c>new_progetto</c> and the N:N <c>new_lead_new_progetto</c>.
    /// </summary>
    private static byte[] LeadSolution(bool managed = false) => SolutionFile(managed,
        [
            new XElement("Entity",
                new XElement("Name", new XAttribute("LocalizedName", "Lead"), new XAttribute("OriginalName", "Lead"), "Lead"),
                new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", "Lead"),
                    new XElement("LocalizedNames", Label("LocalizedName", "Lead")),
                    new XElement("attributes",
                        Attribute("leadid", "primarykey", "Lead", custom: false),
                        Attribute("fullname", "nvarchar", "Name", custom: false),
                        Attribute("new_score", "int", "Score", custom: true),
                        Attribute("new_accountid", "lookup", "Account", custom: true)),
                    new XElement("OwnershipTypeMask", "UserOwned"))),
                new XElement("FormXml", new XElement("forms", new XAttribute("type", "main"), new XElement("systemform",
                    new XElement("formid", FormId.ToString("B")), new XElement("form"),
                    new XElement("LocalizedNames", Label("LocalizedName", "Information"))))),
                new XElement("SavedQueries", new XElement("savedqueries", new XElement("savedquery",
                    new XElement("savedqueryid", ViewId.ToString("B")), new XElement("returnedtypecode", "lead"),
                    new XElement("LocalizedNames", Label("LocalizedName", "My Leads"))))),
                new XElement("RibbonDiffXml", new XElement("CustomActions",
                    new XElement("CustomAction", new XAttribute("Id", "new.lead.Call"), new XAttribute("Location", "Mscrm.Form.lead.MainTab.Actions.Controls._children")))),
                new XElement("Visualizations", new XElement("visualization",
                    new XElement("savedqueryvisualizationid", ChartId.ToString("B")), new XElement("primaryentitytypecode", "lead"),
                    new XElement("LocalizedNames", Label("LocalizedName", "Leads by source"))))),
            AccountNode(Attribute("new_leadid", "lookup", "Lead", custom: true)),
            Progetto(),
        ],
        [
            OneToMany("new_lead_account_leadid", referenced: "lead", referencing: "account", "new_leadid"),
            OneToMany("new_account_lead_accountid", referenced: "account", referencing: "lead", "new_accountid"),
            ManyToMany("new_lead_new_progetto", "lead", "new_progetto"),
        ],
        [(1, "lead", 0), (1, "account", 2), (1, "new_progetto", 0), (60, FormId.ToString("B"), 0), (26, ViewId.ToString("B"), 0)]);

    /// <summary>The account node of a customer solution: its root component has behavior 2, the node only carries columns.</summary>
    internal static XElement AccountNode(params XElement[] attributes) => new("Entity",
        new XElement("Name", new XAttribute("LocalizedName", "Account"), new XAttribute("OriginalName", "Account"), "Account"),
        new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", "Account"),
            new XElement("LocalizedNames", Label("LocalizedName", "Account")),
            new XElement("attributes", attributes))));

    /// <summary>The customer's own table <c>new_progetto</c>.</summary>
    internal static XElement Progetto() => new("Entity",
        new XElement("Name", new XAttribute("LocalizedName", "Progetto"), new XAttribute("OriginalName", "Progetto"), "new_progetto"),
        new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", "new_progetto"),
            new XElement("LocalizedNames", Label("LocalizedName", "Progetto")),
            new XElement("LocalizedCollectionNames", Label("LocalizedCollectionName", "Progetti")),
            new XElement("attributes",
                Attribute("new_progettoid", "primarykey", "Progetto", custom: false),
                Attribute("new_name", "nvarchar", "Name", custom: true, primaryName: true)),
            new XElement("EntitySetName", "new_progettos"),
            new XElement("OwnershipTypeMask", "UserOwned"),
            new XElement("IsActivity", 0),
            new XElement("HasRelatedNotes", "True"),
            new XElement("HasRelatedActivities", "True"))));

    internal static byte[] SolutionFile(bool managed, XElement[] entities, XElement[] relationships, (int Type, string Key, int Behavior)[] roots,
        TestProcess[]? processes = null, XElement[]? steps = null)
    {
        var solution = new XDocument(new XElement("ImportExportXml",
            new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
            new XElement("SolutionManifest",
                new XElement("UniqueName", Customer),
                new XElement("LocalizedNames", Label("LocalizedName", "Customer solution")),
                new XElement("Descriptions"),
                new XElement("Version", "1.0.0.0"),
                new XElement("Managed", managed ? 1 : 0),
                new XElement("Publisher",
                    new XElement("UniqueName", "customerexample"),
                    new XElement("LocalizedNames", Label("LocalizedName", "Customer example")),
                    new XElement("Descriptions"),
                    new XElement("CustomizationPrefix", "new"),
                    new XElement("CustomizationOptionValuePrefix", 10000)),
                new XElement("RootComponents", roots.Select(r => new XElement("RootComponent", new XAttribute("type", r.Type),
                    Guid.TryParse(r.Key, out _) ? new XAttribute("id", r.Key) : new XAttribute("schemaName", r.Key), new XAttribute("behavior", r.Behavior)))),
                new XElement("MissingDependencies"))));
        var customizations = new XDocument(new XElement("ImportExportXml",
            new XElement("Entities", entities),
            new XElement("Roles"), new XElement("Workflows", (processes ?? []).Select(WorkflowSolutionBuilder.Workflow)), new XElement("FieldSecurityProfiles"), new XElement("Templates"), new XElement("EntityMaps"),
            new XElement("EntityRelationships", relationships),
            new XElement("OrganizationSettings"), new XElement("optionsets"), new XElement("CustomControls"), new XElement("SolutionPluginAssemblies"),
            new XElement("SdkMessageProcessingSteps", steps ?? []), new XElement("WebResources"),
            new XElement("Languages", new XElement("Language", 1033))));
        return SolutionZip.Write(solution, customizations,
            (processes ?? []).ToDictionary(p => p.FilePath, p => WorkflowSolutionBuilder.XamlBytes(p.Fixture!), StringComparer.Ordinal));
    }

    private const string Grid = "ValidForAdvancedFind|ValidForForm|ValidForGrid";

    internal static XElement Attribute(string logicalName, string type, string label, bool custom, bool primaryName = false) => new("attribute",
        new XAttribute("PhysicalName", logicalName),
        new XElement("Type", type),
        new XElement("Name", logicalName),
        new XElement("LogicalName", logicalName),
        new XElement("RequiredLevel", type == "primarykey" ? "systemrequired" : primaryName ? "required" : "none"),
        new XElement("DisplayMask", type == "primarykey" ? "ValidForAdvancedFind|RequiredForGrid" : primaryName ? "PrimaryName|" + Grid + "|RequiredForForm" : Grid),
        new XElement("ValidForUpdateApi", 1), new XElement("ValidForReadApi", 1), new XElement("ValidForCreateApi", 1),
        new XElement("IsCustomField", custom ? 1 : 0),
        new XElement("IsAuditEnabled", 1), new XElement("IsSecured", 0), new XElement("IntroducedVersion", "1.0.0.0"),
        new XElement("IsCustomizable", 1), new XElement("IsRenameable", 1), new XElement("CanModifySearchSettings", 1),
        new XElement("CanModifyRequirementLevelSettings", 1), new XElement("CanModifyAdditionalSettings", 1), new XElement("SourceType", 0),
        type switch
        {
            "nvarchar" => new object[] { new XElement("Format", "text"), new XElement("MaxLength", 100), new XElement("Length", 200) },
            "int" => [new XElement("Format", "none"), new XElement("MinValue", int.MinValue), new XElement("MaxValue", int.MaxValue)],
            _ => [],
        },
        new XElement("displaynames", Label("displayname", label)),
        new XElement("Descriptions", Label("Description", "")));

    internal static XElement OneToMany(string name, string referenced, string referencing, string attribute) => new("EntityRelationship",
        new XAttribute("Name", name),
        new XElement("EntityRelationshipType", "OneToMany"),
        new XElement("IsCustomRelationship", 1), new XElement("IsCustomizable", 1), new XElement("IntroducedVersion", "1.0.0.0"),
        new XElement("IsHierarchical", 0),
        new XElement("ReferencingEntityName", referencing),
        new XElement("ReferencedEntityName", referenced),
        new XElement("CascadeAssign", "NoCascade"), new XElement("CascadeDelete", "RemoveLink"), new XElement("CascadeArchive", "NoCascade"),
        new XElement("CascadeReparent", "NoCascade"), new XElement("CascadeShare", "NoCascade"), new XElement("CascadeUnshare", "NoCascade"),
        new XElement("CascadeRollupView", "NoCascade"),
        new XElement("IsValidForAdvancedFind", 1),
        new XElement("ReferencingAttributeName", attribute));

    private static XElement ManyToMany(string name, string first, string second) => new("EntityRelationship",
        new XAttribute("Name", name),
        new XElement("EntityRelationshipType", "ManyToMany"),
        new XElement("IsCustomRelationship", 1), new XElement("IsCustomizable", 1), new XElement("IntroducedVersion", "1.0.0.0"),
        new XElement("SecurityTypes", "Append"), new XElement("IsHierarchical", 0),
        new XElement("FirstEntityName", first), new XElement("SecondEntityName", second), new XElement("IntersectEntityName", name),
        new XElement("IsValidForAdvancedFind", 1));

    internal static XElement Label(string element, string text) => new(element, new XAttribute("description", text), new XAttribute("languagecode", 1033));

    // ---- runs ------------------------------------------------------------------------------------------------------------

    /// <summary>A package with the map target solution first, then <paramref name="customer"/>, and <paramref name="map"/> when given.</summary>
    private string Package(byte[] customer, string? map, bool managed = false) => Build(_root, p => p
            .Solution(MappingTargetSolution.Name, MappingTargetSolution.Build(), order: 1)
            .Solution(Customer, customer, order: 2, managed: managed),
        map is null ? null : new PackageMapsPart([("sales", map)], new SourceCatalog([CatalogTable("lead", "leadid", false, "user", ("subject", "String", []))], [])));

    private static IMetadataStore Store(MigrationPhaseHarness h) => h.App.Services.GetRequiredService<IMetadataStore>();

    private static List<(string? Table, string? Column, string Message)> Removals(List<MigrationIssue> issues, string code) =>
        [.. issues.Where(i => i.Code == code).Select(i => (i.Table, i.Column, i.Message))];

    private static string Mapped(string component) => $"{Customer}: {component} removed (lead is mapped to xv_lead).";

    private static string Missing(string component, string table) => $"{Customer}: {component} removed ({table} is not in this organization and no map names it).";

    [Fact]
    public async Task Entity_node_and_root_component_of_a_mapped_table_are_removed()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(LeadSolution(), LeadMap), null, new SchemaPhase());

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Customer))).Should().Be(1);
        Store(h).GetTable("lead").Should().BeNull("the lead node is not imported");
        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        Removals(issues, "component-of-mapped-table").Should().BeEquivalentTo(new (string?, string?, string)[]
        {
            ("xv_lead", null, Mapped("entity lead")),
            ("xv_lead", "new_score", Mapped("attribute lead.new_score")),
            ("xv_lead", "new_accountid", Mapped("attribute lead.new_accountid")),
            ("xv_lead", null, Mapped("form lead/Information")),
            ("xv_lead", null, Mapped("view lead/My Leads")),
            ("xv_lead", null, Mapped("chart lead/Leads by source")),
            ("xv_lead", null, Mapped("ribbon lead")),
            ("xv_lead", null, Mapped("relationship new_account_lead_accountid")),
        });
        issues.Should().OnlyContain(i => i.Phase == SchemaPhase.PhaseOrder || i.Code != "component-of-mapped-table");
        // The root components went with their components: the import names none it could not find.
        issues.Where(i => i.Code == "solution-warning").Should().NotContain(i => i.Message.Contains("Root component", StringComparison.Ordinal));
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemform WHERE systemformid = @id", ("@id", FormId))).Should().Be(0);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.savedquery WHERE savedqueryid = @id", ("@id", ViewId))).Should().Be(0);
    }

    [Fact]
    public async Task Lookup_of_another_table_to_a_mapped_table_is_retargeted()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(LeadSolution(), LeadMap), null, new SchemaPhase());

        var lookup = (LookupAttributeMetadata)Store(h).GetEntity("account")!.Attributes.Single(a => a.LogicalName == "new_leadid");
        lookup.Targets.Should().Equal(MappingTargetSolution.Lead);
        Store(h).GetRelationship("new_lead_account_leadid").Should().BeOfType<OneToManyRelationshipMetadata>()
            .Which.ReferencedEntity.Should().Be(MappingTargetSolution.Lead);
        (await h.IssuesAsync()).Should().NotContain(i => i.Message.Contains("new_leadid", StringComparison.Ordinal), "a retargeted lookup is kept");
    }

    [Fact]
    public async Task Custom_nn_with_a_mapped_table_is_retargeted()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(LeadSolution(), LeadMap), null, new SchemaPhase());

        var nn = Store(h).GetRelationship("new_lead_new_progetto").Should().BeOfType<ManyToManyRelationshipMetadata>().Subject;
        (nn.Entity1LogicalName, nn.Entity2LogicalName).Should().Be((MappingTargetSolution.Lead, "new_progetto"));
    }

    [Fact]
    public async Task Managed_solution_with_components_of_a_mapped_table_fails_the_run()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var act = () => h.RunAsync(Package(LeadSolution(managed: true), LeadMap, managed: true), null, new SchemaPhase());

        var message = (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message;
        message.Should().Contain($"'{Customer}'").And.Contain("managed")
            .And.Contain("entity lead").And.Contain("form lead/Information").And.Contain("view lead/My Leads").And.Contain("ribbon lead")
            .And.Contain("relationship new_lead_account_leadid → xv_lead").And.Contain("relationship new_lead_new_progetto → xv_lead");
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Customer))).Should().Be(0, "nothing of it is imported");
    }

    [Fact]
    public async Task Same_table_entries_do_not_touch_the_solution()
    {
        const string map = """
            version: 1
            tables:
              - source: account
                target: account
                columns: { name: name }
            """;
        var file = SolutionFile(false, [AccountNode(Attribute("new_code", "nvarchar", "Code", custom: true))], [], [(1, "account", 2)]);
        var rewrite = SolutionMapRewriter.Rewrite(file, MigrationMaps.Parse([map]), _ => false);
        rewrite.Changed.Should().BeFalse();
        rewrite.Zip.Should().BeSameAs(file);

        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p.Solution(Customer, file),
            new PackageMapsPart([("accounts", map)], new SourceCatalog([CatalogTable("account", "accountid", false, "user", ("name", "String", []))], []))),
            null, new SchemaPhase());

        Store(h).GetEntity("account")!.Attributes.Should().Contain(a => a.LogicalName == "new_code");
        (await h.IssuesAsync()).Should().NotContain(i => i.Code.StartsWith("component-of-", StringComparison.Ordinal));
    }

    [Fact]
    public void Solution_without_mapped_components_is_not_rewritten()
    {
        var file = SolutionFile(false, [Progetto()], [], [(1, "new_progetto", 0)]);
        var rewrite = SolutionMapRewriter.Rewrite(file, MigrationMaps.Parse([LeadMap]), _ => false);
        rewrite.Changed.Should().BeFalse();
        rewrite.Removed.Should().BeEmpty();
        rewrite.Zip.Should().Equal(file);
    }

    [Fact]
    public async Task Components_of_a_missing_microsoft_table_are_removed_without_a_map()
    {
        var file = SolutionFile(false,
            [
                new XElement("Entity",
                    new XElement("Name", new XAttribute("LocalizedName", "Quote"), new XAttribute("OriginalName", "Quote"), "Quote"),
                    new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", "Quote"),
                        new XElement("LocalizedNames", Label("LocalizedName", "Quote")),
                        new XElement("attributes", Attribute("quoteid", "primarykey", "Quote", custom: false)),
                        new XElement("OwnershipTypeMask", "UserOwned"))),
                    new XElement("FormXml", new XElement("forms", new XAttribute("type", "main"), new XElement("systemform",
                        new XElement("formid", QuoteFormId.ToString("B")), new XElement("form"),
                        new XElement("LocalizedNames", Label("LocalizedName", "Quote form")))))),
                AccountNode(Attribute("new_quoteid", "lookup", "Quote", custom: true), Attribute("new_code", "nvarchar", "Code", custom: true)),
            ],
            [OneToMany("new_quote_account_quoteid", referenced: "quote", referencing: "account", "new_quoteid")],
            [(1, "quote", 0), (1, "account", 2), (1, "msdyn_survey", 0)]);

        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p.Solution(Customer, file)), null, new SchemaPhase());

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        Removals(issues, "component-of-missing-table").Should().BeEquivalentTo(new (string?, string?, string)[]
        {
            ("quote", null, Missing("entity quote", "quote")),
            ("quote", null, Missing("form quote/Quote form", "quote")),
            ("quote", "new_quoteid", Missing("attribute account.new_quoteid", "quote")),
            ("quote", null, Missing("relationship new_quote_account_quoteid", "quote")),
            ("msdyn_survey", null, Missing("entity msdyn_survey", "msdyn_survey")),
        });
        issues.Should().NotContain(i => i.Code == "component-of-mapped-table");
        Store(h).GetTable("quote").Should().BeNull();
        var account = Store(h).GetEntity("account")!;
        account.Attributes.Should().Contain(a => a.LogicalName == "new_code", "the rest of the solution imports");
        account.Attributes.Should().NotContain(a => a.LogicalName == "new_quoteid");
    }

    [Fact]
    public async Task Custom_table_of_the_customer_is_never_removed()
    {
        var file = SolutionFile(false, [Progetto()], [], [(1, "new_progetto", 0)]);
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p.Solution(Customer, file)), null, new SchemaPhase());

        Store(h).GetTable("new_progetto").Should().NotBeNull();
        (await h.IssuesAsync()).Should().NotContain(i => i.Code.StartsWith("component-of-", StringComparison.Ordinal));
    }

    // ---- fix round 1: processes, steps, multi-table lookups, N:N with a missing end, managed R11b --------------------------------

    private static TestProcess Process(string table) => new($"MigMap{table}{Guid.NewGuid().ToString("N")[..6]}", "background-wait", PrimaryEntity: table);

    private static XElement StepOn(string table, Guid id) => new("SdkMessageProcessingStep",
        new XAttribute("Name", $"new.{table}.create"), new XAttribute("SdkMessageProcessingStepId", id.ToString("B")),
        new XElement("PluginTypeName", "Example.Plugins.OnCreate, Example.Plugins"),
        new XElement("PrimaryEntity", table),
        new XElement("Mode", 0), new XElement("Rank", 1), new XElement("Stage", 40), new XElement("SupportedDeployment", 0),
        new XElement("StateCode", 0), new XElement("StatusCode", 1));

    [Fact]
    public async Task Process_on_a_mapped_table_is_removed()
    {
        TestProcess onLead = Process("lead"), onAccount = Process("account");
        var file = SolutionFile(false, [Progetto()], [], [(1, "new_progetto", 0), (29, onLead.Id.ToString("B"), 0), (29, onAccount.Id.ToString("B"), 0)],
            processes: [onLead, onAccount]);
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(file, LeadMap), null, new SchemaPhase());

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        Removals(issues, "component-of-mapped-table").Should().Equal(("xv_lead", null, Mapped($"process lead/{onLead.Name}")));
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", ("@id", onLead.Id))).Should().Be(0);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", ("@id", onAccount.Id))).Should().Be(1, "the rest of the solution imports");
        issues.Where(i => i.Code == "solution-warning").Should().NotContain(i => i.Message.Contains("Root component", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Process_and_step_on_a_missing_table_are_removed_without_a_map()
    {
        var onQuote = Process("quote");
        var step = Guid.NewGuid();
        var file = SolutionFile(false, [AccountNode(Attribute("new_code", "nvarchar", "Code", custom: true))], [],
            [(1, "account", 2), (29, onQuote.Id.ToString("B"), 0), (92, step.ToString("B"), 0)], processes: [onQuote], steps: [StepOn("quote", step)]);
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p.Solution(Customer, file)), null, new SchemaPhase());

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        Removals(issues, "component-of-missing-table").Should().BeEquivalentTo(new (string?, string?, string)[]
        {
            ("quote", null, Missing($"process quote/{onQuote.Name}", "quote")),
            ("quote", null, Missing("step quote/new.quote.create", "quote")),
        });
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", ("@id", onQuote.Id))).Should().Be(0);
        Store(h).GetEntity("account")!.Attributes.Should().Contain(a => a.LogicalName == "new_code", "the rest of the solution imports");
        issues.Where(i => i.Code == "solution-warning").Should().NotContain(i => i.Message.Contains("Root component", StringComparison.Ordinal));
    }

    private static Func<string, bool> QuoteIsMissing => t => t == "quote";

    private static readonly MapSet NoMaps = MigrationMaps.Parse([]);

    [Fact]
    public void Multi_table_lookup_keeps_its_column_when_one_target_is_missing()
    {
        var file = SolutionFile(false, [AccountNode(Attribute("new_partyid", "lookup", "Party", custom: true))],
            [
                OneToMany("new_quote_account_partyid", referenced: "quote", referencing: "account", "new_partyid"),
                OneToMany("new_contact_account_partyid", referenced: "contact", referencing: "account", "new_partyid"),
            ],
            [(1, "account", 2), (2, "new_partyid", 0)]);
        var rewrite = SolutionMapRewriter.Rewrite(file, NoMaps, QuoteIsMissing);

        rewrite.Removed.Should().Equal("relationship new_quote_account_partyid");
        var (solution, customizations, _) = SolutionZip.Read(rewrite.Zip);
        customizations.Descendants("attribute").Should().ContainSingle(a => (string?)a.Element("LogicalName") == "new_partyid");
        customizations.Descendants("EntityRelationship").Select(r => (string?)r.Attribute("Name")).Should().Equal("new_contact_account_partyid");
        solution.Descendants("RootComponent").Should().Contain(r => (string?)r.Attribute("schemaName") == "new_partyid");
    }

    [Fact]
    public void Lookup_to_a_missing_table_takes_its_attribute_root_component()
    {
        var file = SolutionFile(false, [AccountNode(Attribute("new_quoteid", "lookup", "Quote", custom: true))],
            [OneToMany("new_quote_account_quoteid", referenced: "quote", referencing: "account", "new_quoteid")],
            [(1, "account", 2), (2, "new_quoteid", 0)]);
        var rewrite = SolutionMapRewriter.Rewrite(file, NoMaps, QuoteIsMissing);

        rewrite.Removed.Should().BeEquivalentTo("attribute account.new_quoteid", "relationship new_quote_account_quoteid");
        var (solution, _, _) = SolutionZip.Read(rewrite.Zip);
        solution.Descendants("RootComponent").Select(r => (string?)r.Attribute("schemaName")).Should().Equal("account");
    }

    [Fact]
    public void Nn_with_a_missing_end_is_removed()
    {
        var file = SolutionFile(false, [Progetto()], [ManyToMany("new_quote_new_progetto", "quote", "new_progetto")],
            [(1, "new_progetto", 0), (10, "new_quote_new_progetto", 0)]);
        var rewrite = SolutionMapRewriter.Rewrite(file, NoMaps, QuoteIsMissing);

        rewrite.Removals.Should().ContainSingle().Which.Should().Be(
            new SolutionRemoval("relationship new_quote_new_progetto", "quote", null, "quote", null));
        var (solution, customizations, _) = SolutionZip.Read(rewrite.Zip);
        customizations.Descendants("EntityRelationship").Should().BeEmpty();
        solution.Descendants("RootComponent").Select(r => (string?)r.Attribute("schemaName")).Should().Equal("new_progetto");
    }

    [Fact]
    public async Task Managed_solution_with_components_of_a_missing_table_fails_the_run()
    {
        var file = SolutionFile(true, [AccountNode(Attribute("new_code", "nvarchar", "Code", custom: true))], [], [(1, "account", 2), (1, "quote", 0)]);
        await using var h = MigrationPhaseHarness.Start(fx);
        var act = () => h.RunAsync(Build(_root, p => p.Solution(Customer, file, managed: true)), null, new SchemaPhase());

        (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message.Should().Contain("managed").And.Contain("entity quote");
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Customer))).Should().Be(0);
    }

    [Fact]
    public async Task Custom_column_of_a_mapped_table_in_a_customer_solution_lands_on_the_target_with_its_data()
    {
        // R11 takes lead.new_score and lead.new_accountid out of the customer solution; the export of a mapped table put every custom
        // column of it in the delta (whether a solution carries it or not), and R7 creates them on xv_lead.
        static JsonObject Label(string text) => new() { ["LocalizedLabels"] = new JsonArray(new JsonObject { ["Label"] = text, ["LanguageCode"] = 1033 }) };
        var delta = new JsonObject
        {
            ["columns"] = new JsonArray(
                new JsonObject
                {
                    ["table"] = "lead", ["column"] = "new_score", ["type"] = "Integer",
                    ["metadata"] = new JsonObject
                    {
                        ["@odata.type"] = "#Microsoft.Dynamics.CRM.IntegerAttributeMetadata", ["LogicalName"] = "new_score", ["SchemaName"] = "new_Score",
                        ["AttributeType"] = "Integer", ["DisplayName"] = Label("Score"), ["MinValue"] = 0, ["MaxValue"] = 100,
                    },
                },
                new JsonObject
                {
                    ["table"] = "lead", ["column"] = "new_accountid", ["type"] = "Lookup",
                    ["metadata"] = new JsonObject
                    {
                        ["@odata.type"] = "#Microsoft.Dynamics.CRM.LookupAttributeMetadata", ["LogicalName"] = "new_accountid", ["SchemaName"] = "new_AccountId",
                        ["AttributeType"] = "Lookup", ["DisplayName"] = Label("Account"), ["Targets"] = new JsonArray("account"),
                    },
                    ["relationship"] = new JsonObject
                    {
                        ["SchemaName"] = "new_account_lead_accountid", ["ReferencedEntity"] = "account", ["ReferencedAttribute"] = "accountid",
                        ["ReferencingEntity"] = "lead", ["ReferencingAttribute"] = "new_accountid", ["RelationshipType"] = "OneToManyRelationship",
                    },
                }),
        };
        var catalog = new SourceCatalog([new SourceCatalogTable("lead", "leadid", 4, "user", false,
            [new("subject", "String", [], false), new("new_score", "Integer", [], true), new("new_accountid", "Lookup", ["account"], true)])], []);
        Guid lead = Guid.NewGuid(), account = Guid.NewGuid();
        var package = Build(_root, p => p
                .Solution(Customer, LeadSolution())
                .File("schema/delta.json", Encoding.UTF8.GetBytes(delta.ToJsonString()))
                .Security("businessunit", "businessunitid")
                .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
                .Table("lead", "leadid", [Record(lead, ("subject", new StringValue("Lead 1")), ("new_score", new IntValue(7)), ("new_accountid", new LookupValue("account", account)))]),
            new PackageMapsPart([("sales", LeadMap)], catalog));

        await using var h = MigrationPhaseHarness.Start(fx);
        // The target solution is installed before the run (the map check needs the target table).
        await h.RunAsync(Build(_root, p => p.Solution(MappingTargetSolution.Name, MappingTargetSolution.Build())), null, new SchemaPhase());
        await h.RunAsync(package, null, new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase());

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error || i.Code == "schema-item-failed");
        issues.Should().Contain(i => i.Code == "component-of-mapped-table" && i.Column == "new_score");
        var target = Store(h).GetEntity(MappingTargetSolution.Lead)!;
        target.Attributes.Should().ContainSingle(a => a.LogicalName == "new_score").Which.Should().BeOfType<IntegerAttributeMetadata>();
        target.Attributes.OfType<LookupAttributeMetadata>().Should().ContainSingle(a => a.LogicalName == "new_accountid").Which.Targets.Should().Equal("account");
        (await h.ScalarAsync("SELECT new_score FROM data.xv_lead WHERE xv_leadid = @id", ("@id", lead))).Should().Be(7);
        (await h.ScalarAsync("SELECT new_accountid FROM data.xv_lead WHERE xv_leadid = @id", ("@id", lead))).Should().Be(account);
    }
}
