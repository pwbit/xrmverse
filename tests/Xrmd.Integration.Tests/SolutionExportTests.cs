using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// ExportSolution (unmanaged): the export of the imported fixture round-trips (<see cref="XmlNormalizer"/>), the plug-in
/// assembly comes out byte for byte, and every option xrmverse cannot honor is refused (400) before anything is read.
/// </summary>
[Collection("solution-import")]
public sealed class SolutionExportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Fixture = SolutionFixtureZip.FixtureName;
    private static readonly Guid ViewId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e31");
    private static readonly Guid SetAttributeTypeId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e11");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ClearAsync();
    }

    public Task DisposeAsync() => ClearAsync();

    private const string SharedOptionSet = "xv_shared";
    private const string Second = "xv_second";

    private async Task ClearAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        if (Store.GetOptionSet(SharedOptionSet) is not null)
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteOptionSetAsync(s, SharedOptionSet, default));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<string> ImportFixtureAsync()
    {
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = jobId });
        return (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
    }

    private static async Task<byte[]> ExportAsync(OrganizationService service, string name = Fixture, bool managed = false) =>
        ((ExportSolutionResponse)await service.ExecuteAsync(new ExportSolutionRequest { SolutionName = name, Managed = managed }))
        .ExportSolutionFile;

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    private static XDocument Document(byte[] bytes) => XDocument.Load(new MemoryStream(bytes));

    private async Task<(XDocument Solution, XDocument Customizations)> ExportDocumentsAsync(string name = Fixture)
    {
        var entries = Entries(await ExportAsync(Admin, name));
        return (Document(entries["solution.xml"]), Document(entries["customizations.xml"]));
    }

    /// <summary>A second solution of the fixture's publisher (after an import of the fixture), holding <paramref name="components"/>.</summary>
    private async Task SecondSolutionAsync(params (int Type, Guid Id, bool DoNotIncludeSubcomponents)[] components)
    {
        var publisherId = (Guid)(await ScalarAsync("SELECT publisherid FROM data.publisher WHERE uniquename = N'xrmverse'"))!;
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Second, ["friendlyname"] = "Second", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
        foreach (var (type, id, doNotInclude) in components)
            await Admin.ExecuteAsync(new AddSolutionComponentRequest
            {
                ComponentType = type, ComponentId = id, SolutionUniqueName = Second, DoNotIncludeSubcomponents = doNotInclude,
            });
    }

    private Guid EntityId(string logicalName) => Store.GetEntity(logicalName)!.MetadataId!.Value;

    private Guid AttributeId(string entity, string attribute) =>
        Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == attribute).MetadataId!.Value;

    private static XElement? EntityElement(XDocument customizations, string logicalName) =>
        customizations.Root!.Element("Entities")!.Elements("Entity")
            .SingleOrDefault(e => e.Element("Name")!.Value.Equals(logicalName, StringComparison.OrdinalIgnoreCase));

    private static List<string> AttributeNames(XElement entity) =>
        entity.Element("EntityInfo")!.Element("entity")!.Element("attributes")!.Elements("attribute").Select(a => a.Element("Name")!.Value).ToList();

    private static List<(string Type, string Key, string Behavior)> Roots(XDocument solution) =>
        solution.Descendants("RootComponent").Select(r => ((string)r.Attribute("type")!,
            (string?)r.Attribute("schemaName") ?? (string)r.Attribute("id")!, (string)r.Attribute("behavior")!)).ToList();

    private static List<(string Required, string Dependent)> MissingDependencies(XDocument solution) =>
        solution.Descendants("MissingDependency").Select(d =>
            ($"{d.Element("Required")!.Attribute("type")!.Value}:{d.Element("Required")!.Attribute("schemaName")!.Value}",
                $"{d.Element("Dependent")!.Attribute("type")!.Value}:{d.Element("Dependent")!.Attribute("schemaName")!.Value}")).ToList();

    private static IEnumerable<XElement> Attributes(XDocument customizations) =>
        customizations.Root!.Element("Entities")!.Elements("Entity").SelectMany(e => e.Element("EntityInfo")!.Element("entity")!
            .Element("attributes")!.Elements("attribute"));

    private static string EntityOf(XElement attribute) =>
        attribute.Parent!.Parent!.Attribute("Name")!.Value.ToLowerInvariant();

    private static string NameOf(XElement attribute) => attribute.Element("Name")!.Value.ToLowerInvariant();

    private static bool IsSystem(XElement attribute) => attribute.Element("IsCustomField")!.Value == "0";

    /// <summary>
    /// A system attribute (or one xrmverse generates with another, the money <c>_base</c>) reduced to what the import applies
    /// to xrmverse's own attribute (T8 ruling I-1): its labels, its required level and the labels of its state and status
    /// options. Any other difference was logged by the import as a warning naming the property.
    /// </summary>
    private static void ReduceToApplied(XElement attribute, bool generated)
    {
        var keep = generated ? new[] { "Name" } : ["Name", "RequiredLevel", "displaynames", "Descriptions", "optionset"];
        foreach (var child in attribute.Elements().Where(c => !keep.Contains(c.Name.LocalName)).ToList()) child.Remove();
        attribute.RemoveAttributes();
        if (attribute.Element("optionset") is { } set)
        {
            // The options with their value and labels (the states of a table are fixed: their other attributes are xrmverse's).
            if ((set.Element("states") ?? set.Element("statuses")) is { } options)
                set.ReplaceWith(new XElement("optionset", new XElement(options.Name, options.Elements().Select(o =>
                    new XElement(o.Name, o.Attribute("value"), o.Element("labels"))))));
            else set.Remove();
        }
    }

    private static void ShouldBeSameDocument(string actual, string expected)
    {
        if (actual == expected) return;
        var a = actual.Split('\n');
        var e = expected.Split('\n');
        var line = Enumerable.Range(0, Math.Min(a.Length, e.Length)).FirstOrDefault(i => a[i] != e[i], Math.Min(a.Length, e.Length));
        string Around(string[] lines) => string.Join('\n', lines.Skip(Math.Max(0, line - 8)).Take(16));
        actual.Should().Be(expected, "the documents differ at line {0}:\n--- exported ---\n{1}\n--- fixture ---\n{2}", line + 1, Around(a), Around(e));
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_after_import_round_trips()
    {
        var log = await ImportFixtureAsync();

        var zip = await ExportAsync(Admin);

        var entries = Entries(zip);
        entries.Keys.Should().Contain(["[Content_Types].xml", "solution.xml", "customizations.xml"]);
        var (fixtureSolution, fixtureCustomizations, fixtureFiles) = SolutionZip.Read(SolutionFixtureZip.Build());
        entries["PluginAssemblies/XrmdTestPlugins-6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E10/XrmdTestPlugins.dll"]
            .Should().Equal(SolutionFixtureZip.TestPluginBytes, "the plug-in assembly comes out byte for byte");
        entries["WebResources/xv_/scripts/project.js6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E41"]
            .Should().Equal(fixtureFiles["WebResources/xv_/scripts/project.js6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E41"]);

        // solution.xml: the same, publisher and root components included.
        ShouldBeSameDocument(XmlNormalizer.Normalize(Document(entries["solution.xml"])), XmlNormalizer.Normalize(fixtureSolution));

        // customizations.xml: the same, but for what the import reported it did not apply (each one checked against the log).
        var exported = Document(entries["customizations.xml"]);
        var expected = new XDocument(fixtureCustomizations);

        // 1. The system attributes xrmverse does not generate: the import logged each one as not created.
        foreach (var attribute in Attributes(expected).Where(IsSystem).ToList())
        {
            var warning = $"System attribute '{EntityOf(attribute)}.{NameOf(attribute)}' is not created: xrmverse does not generate it.";
            if (!log.Contains(warning, StringComparison.Ordinal)) continue;
            Attributes(exported).Should().NotContain(a => EntityOf(a) == EntityOf(attribute) && NameOf(a) == NameOf(attribute));
            attribute.Remove();
        }
        // 2. The system attributes xrmverse generates that a Dataverse organization-owned table does not have.
        var extra = Attributes(exported)
            .Where(a => !Attributes(expected).Any(e => EntityOf(e) == EntityOf(a) && NameOf(e) == NameOf(a))).ToList();
        extra.Select(a => $"{EntityOf(a)}.{NameOf(a)}").Should().BeEquivalentTo("xv_tag.owninguser", "xv_tag.owningteam", "xv_tag.owningbusinessunit");
        extra.Should().OnlyContain(a => IsSystem(a));
        extra.ForEach(a => a.Remove());
        // 3. System attributes and the generated money _base: compared on what the import applies (T8 ruling I-1).
        foreach (var document in new[] { exported, expected })
        {
            foreach (var attribute in Attributes(document).Where(IsSystem)) ReduceToApplied(attribute, generated: false);
            foreach (var attribute in Attributes(document).Where(a => NameOf(a).EndsWith("_base", StringComparison.Ordinal)))
                ReduceToApplied(attribute, generated: true);
        }
        // The _base has the Dataverse metadata (plan 2.10): nothing to report.
        log.Should().NotContain("Attribute 'xv_project.xv_budget_base' is generated by xrmverse and differs from the file in");
        // 4. The system relationships: xrmverse derives its own (other names), the export writes none (ruling).
        expected.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
            .Where(r => r.Attribute("Name")!.Value is not ("xv_project_xv_task_ProjectId" or "xv_project_xv_tag")).Remove();
        // 5. The description of the custom relationship: xrmverse has none, the import logged it.
        log.Should().Contain("The description of relationship 'xv_project_xv_task_ProjectId' ('The tasks of a project.') is not kept");
        expected.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
            .Single(r => r.Attribute("Name")!.Value == "xv_project_xv_task_ProjectId").Element("RelationshipDescription")!
            .Element("Descriptions")!.RemoveNodes();
        // 6. The referencing navigation property is the lookup's SchemaName (Dataverse's default), as the file has it: nothing to log.
        log.Should().NotContain("The referencing navigation property");

        ShouldBeSameDocument(XmlNormalizer.Normalize(exported), XmlNormalizer.Normalize(expected));
    }

    [Fact]
    public async Task Exported_file_imports_again_as_the_same_solution()
    {
        await ImportFixtureAsync();
        var first = await ExportAsync(Admin);

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = first, ImportJobId = Guid.NewGuid() });
        var second = await ExportAsync(Admin);

        XmlNormalizer.Normalize(Document(Entries(second)["customizations.xml"]))
            .Should().Be(XmlNormalizer.Normalize(Document(Entries(first)["customizations.xml"])));
        XmlNormalizer.Normalize(Document(Entries(second)["solution.xml"]))
            .Should().Be(XmlNormalizer.Normalize(Document(Entries(first)["solution.xml"])));
    }

    [Fact]
    public async Task Export_writes_the_rows_as_they_are_now()
    {
        await ImportFixtureAsync();
        await Admin.UpdateAsync(new Entity("savedquery", ViewId)
        {
            ["name"] = "All Projects",
            ["fetchxml"] = "<fetch version=\"1.0\" mapping=\"logical\"><entity name=\"xv_project\"><attribute name=\"xv_name\" /></entity></fetch>",
        });

        var customizations = Document(Entries(await ExportAsync(Admin))["customizations.xml"]);

        var view = customizations.Descendants("savedquery").Single();
        view.Element("fetchxml")!.Element("fetch")!.Element("entity")!.Elements("attribute").Select(a => (string)a.Attribute("name")!)
            .Should().Equal("xv_name");
        view.Element("LocalizedNames")!.Elements("LocalizedName")
            .Single(n => (string)n.Attribute("languagecode")! == "1033").Attribute("description")!.Value.Should().Be("All Projects");
        view.Element("LocalizedNames")!.Elements("LocalizedName")
            .Single(n => (string)n.Attribute("languagecode")! == "1040").Attribute("description")!.Value.Should().Be("Progetti attivi");
    }

    [Fact]
    public async Task Export_of_a_solution_made_with_metadata_messages()
    {
        var publisherId = await Admin.CreateAsync(new Entity("publisher")
        {
            ["uniquename"] = "xrmverse", ["friendlyname"] = "xrmverse", ["customizationprefix"] = "xv", ["customizationoptionvalueprefix"] = 10000,
        });
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Fixture, ["friendlyname"] = "Made here", ["version"] = "2.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = "xv_Project", LogicalName = "xv_project", DisplayName = new Label("Project", 1033),
                DisplayCollectionName = new Label("Projects", 1033), OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = "xv_Name", LogicalName = "xv_name", MaxLength = 100, DisplayName = new Label("Name", 1033),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
            },
            SolutionUniqueName = Fixture,
        });

        var (solution, customizations, files) = SolutionZip.Read(await ExportAsync(Admin));
        var package = CustomizationsReader.Read(solution, customizations, files);

        package.Solution.Version.Should().Be("2.0.0.0");
        package.Solution.FriendlyName.UserLocalizedLabel!.Label.Should().Be("Made here");
        package.Publisher.CustomizationPrefix.Should().Be("xv");
        package.RootComponents.Should().ContainSingle().Which.Should().Be((ComponentType.Entity, "xv_project", 0));
        var project = package.Entities.Should().ContainSingle().Subject;
        project.PrimaryNameAttribute.Should().Be("xv_name");
        project.Attributes.Select(a => a.LogicalName).Should().Contain(["xv_projectid", "xv_name", "createdon", "statecode"]);
        package.Lookups.Select(l => l.LogicalName).Should().Contain("ownerid");
        customizations.Root!.Element("Entities")!.Element("Entity")!.Element("RibbonDiffXml").Should().NotBeNull("an entity has its empty ribbon");
        // Nothing kept from a file: the defaults of a custom table and column created through the SDK.
        var info = customizations.Root!.Element("Entities")!.Element("Entity")!.Element("EntityInfo")!.Element("entity")!;
        info.Element("HasRelatedNotes")!.Value.Should().Be("False");
        info.Element("ChangeTrackingEnabled")!.Value.Should().Be("0");
        info.Element("attributes")!.Elements("attribute").Single(a => a.Element("Name")!.Value == "xv_name").Element("DisplayMask")!.Value
            .Should().Be("PrimaryName|ValidForAdvancedFind|ValidForForm|ValidForGrid|RequiredForForm");
    }

    /// <summary>Creates xv_project in the solution <see cref="Fixture"/> with a file column and three image columns (the last one primary).</summary>
    private async Task ProjectWithFileAndImageColumnsAsync()
    {
        var publisherId = await Admin.CreateAsync(new Entity("publisher")
        {
            ["uniquename"] = "xrmverse", ["friendlyname"] = "xrmverse", ["customizationprefix"] = "xv", ["customizationoptionvalueprefix"] = 10000,
        });
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Fixture, ["friendlyname"] = "Files", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = "xv_Project", LogicalName = "xv_project", DisplayName = new Label("Project", 1033),
                DisplayCollectionName = new Label("Projects", 1033), OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata { SchemaName = "xv_Name", LogicalName = "xv_name", MaxLength = 100, DisplayName = new Label("Name", 1033) },
            SolutionUniqueName = Fixture,
        });
        // Created in an order that is not the alphabetical one: the image creation order decides the next primary image.
        AttributeMetadata[] columns =
        [
            new FileAttributeMetadata { SchemaName = "xv_Contract", LogicalName = "xv_contract", MaxSizeInKB = 65536, DisplayName = new Label("Contract", 1033) },
            new ImageAttributeMetadata { SchemaName = "xv_Zeta", LogicalName = "xv_zeta", CanStoreFullImage = true, DisplayName = new Label("Zeta", 1033) },
            new ImageAttributeMetadata { SchemaName = "xv_Alpha", LogicalName = "xv_alpha", MaxSizeInKB = 2048, DisplayName = new Label("Alpha", 1033) },
            new ImageAttributeMetadata { SchemaName = "xv_Mid", LogicalName = "xv_mid", DisplayName = new Label("Mid", 1033) },
        ];
        foreach (var column in columns)
            await Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = "xv_project", Attribute = column, SolutionUniqueName = Fixture });
        // A later image column is made primary with an update (true is ignored on its create).
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = "xv_project", Attribute = new ImageAttributeMetadata { LogicalName = "xv_mid", IsPrimaryImage = true },
        });
    }

    private void ShouldHaveTheFileAndImageColumns()
    {
        var project = Store.GetEntity("xv_project")!;
        T Column<T>(string name) where T : AttributeMetadata => project.Attributes.OfType<T>().Single(a => a.LogicalName == name);
        Column<FileAttributeMetadata>("xv_contract").MaxSizeInKB.Should().Be(65536);
        var zeta = Column<ImageAttributeMetadata>("xv_zeta");
        (zeta.CanStoreFullImage, zeta.IsPrimaryImage, zeta.MaxSizeInKB).Should().Be((true, false, 10240));
        var alpha = Column<ImageAttributeMetadata>("xv_alpha");
        (alpha.CanStoreFullImage, alpha.IsPrimaryImage, alpha.MaxSizeInKB).Should().Be((false, false, 2048));
        Column<ImageAttributeMetadata>("xv_mid").IsPrimaryImage.Should().BeTrue();
        foreach (var (companion, main) in new[] { ("xv_contract_name", "xv_contract"), ("xv_zetaid", "xv_zeta"), ("xv_alpha_timestamp", "xv_alpha"), ("xv_mid_url", "xv_mid") })
            project.Attributes.Single(a => a.LogicalName == companion).AttributeOf.Should().Be(main);
        Store.GetRelationship("xv_project_FileAttachments").Should().NotBeNull("the first file or image column links the table to fileattachment");
        Store.GetEntity(FileColumns.AttachmentEntity)!.Attributes.OfType<LookupAttributeMetadata>()
            .Single(a => a.LogicalName == FileColumns.AttachmentLookup).Targets.Should().Contain("xv_project");
    }

    [Fact]
    public async Task Solution_export_import_round_trips_file_and_image_attributes()
    {
        await ProjectWithFileAndImageColumnsAsync();
        await Store.LoadAsync(); // the cache reloaded orders the attributes by name: the export must keep the creation order
        var first = await ExportAsync(Admin);

        var customizations = Document(Entries(first)["customizations.xml"]);
        var attributes = customizations.Descendants("attribute").ToDictionary(a => a.Element("LogicalName")!.Value);
        attributes["xv_contract"].Element("Type")!.Value.Should().Be("file");
        attributes["xv_contract"].Element("MaxSizeInKB")!.Value.Should().Be("65536");
        attributes["xv_zeta"].Element("Type")!.Value.Should().Be("image");
        (attributes["xv_zeta"].Element("CanStoreFullImage")!.Value, attributes["xv_zeta"].Element("IsPrimaryImage")!.Value).Should().Be(("1", "0"));
        attributes["xv_mid"].Element("IsPrimaryImage")!.Value.Should().Be("1");
        attributes.Keys.Should().NotContain(["xv_contract_name", "xv_zetaid", "xv_zeta_timestamp", "xv_zeta_url"],
            "the companions are created with their main column");
        attributes.Keys.Where(k => k is "xv_zeta" or "xv_alpha" or "xv_mid").Should().Equal(["xv_zeta", "xv_alpha", "xv_mid"],
            "image columns are written in their creation order, which an import repeats");
        customizations.Descendants("EntityRelationship").Select(r => (string?)r.Attribute("Name"))
            .Should().NotContain("xv_project_FileAttachments", "the relationship to fileattachment is the server's, made with the first file or image column");

        // Into an organization without the table: created with its columns, companions, relationship and primary image.
        await ClearAsync();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = first, ImportJobId = Guid.NewGuid() });
        ShouldHaveTheFileAndImageColumns();
        var second = await ExportAsync(Admin);
        XmlNormalizer.Normalize(Document(Entries(second)["customizations.xml"])).Should().Be(XmlNormalizer.Normalize(customizations));

        // Again over the existing table: nothing changes (IsPrimaryImage false is not a request to drop the flag).
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = first, ImportJobId = Guid.NewGuid() });
        ShouldHaveTheFileAndImageColumns();

        // The creation order came through: deleting the primary image makes the oldest remaining one primary.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "xv_project", LogicalName = "xv_mid" });
        Store.GetEntity("xv_project")!.Attributes.OfType<ImageAttributeMetadata>().Single(a => a.IsPrimaryImage == true).LogicalName.Should().Be("xv_zeta");
    }

    [Fact]
    public async Task Import_of_a_file_with_the_companions_and_the_fileattachment_relationship_creates_them_once()
    {
        await ProjectWithFileAndImageColumnsAsync();
        var entries = Entries(await ExportAsync(Admin));
        // What a file made elsewhere may carry besides the columns: their companions and the relationship to fileattachment.
        var customizations = Document(entries["customizations.xml"]);
        XElement Companion(string physicalName, string type, params XElement[] typeSpecific) => new("attribute",
            new XAttribute("PhysicalName", physicalName), new XElement("Type", type), new XElement("Name", physicalName.ToLowerInvariant()),
            new XElement("LogicalName", physicalName.ToLowerInvariant()), new XElement("RequiredLevel", "none"),
            new XElement("ValidForUpdateApi", "0"), new XElement("ValidForCreateApi", "0"), new XElement("IsCustomField", "1"),
            new XElement("IsSecured", "0"), new XElement("SourceType", "0"), new XElement("AutoNumberFormat"), typeSpecific);
        customizations.Descendants("attributes").Single().Add(
            Companion("xv_Contract_Name", "nvarchar", new XElement("Format", "text"), new XElement("MaxLength", "255")),
            Companion("xv_ZetaId", "uniqueidentifier"),
            Companion("xv_Zeta_Timestamp", "bigint"),
            Companion("xv_Zeta_URL", "nvarchar", new XElement("Format", "url"), new XElement("MaxLength", "200")));
        customizations.Root!.Element("EntityRelationships")!.Add(new XElement("EntityRelationship", new XAttribute("Name", "xv_project_FileAttachments"),
            new XElement("EntityRelationshipType", "OneToMany"), new XElement("IsCustomRelationship", "1"),
            new XElement("ReferencingEntityName", "FileAttachment"), new XElement("ReferencedEntityName", "xv_project"),
            new XElement("ReferencingAttributeName", "ObjectId")));
        var file = SolutionZip.Write(Document(entries["solution.xml"]), customizations,
            entries.Where(e => e.Key is not ("solution.xml" or "customizations.xml" or "[Content_Types].xml")).ToDictionary());
        await ClearAsync();

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });

        ShouldHaveTheFileAndImageColumns();
        var names = Store.GetEntity("xv_project")!.Attributes.Select(a => a.LogicalName).ToList();
        names.Count(n => n == "xv_contract_name").Should().Be(1);
        names.Count(n => n == "xv_zeta_url").Should().Be(1);
    }

    /// <summary>The export of <see cref="ProjectWithFileAndImageColumnsAsync"/>, changed by <paramref name="change"/>, as a solution file.</summary>
    private async Task<byte[]> ChangedExportAsync(Action<XDocument> change)
    {
        var entries = Entries(await ExportAsync(Admin));
        var customizations = Document(entries["customizations.xml"]);
        change(customizations);
        return SolutionZip.Write(Document(entries["solution.xml"]), customizations,
            entries.Where(e => e.Key is not ("solution.xml" or "customizations.xml" or "[Content_Types].xml")).ToDictionary());
    }

    [Fact]
    public async Task A_primary_image_that_is_not_created_is_not_carried_over_and_the_import_says_so()
    {
        await ProjectWithFileAndImageColumnsAsync();
        var file = await ChangedExportAsync(c =>
        {
            var attributes = c.Descendants("attributes").Single();
            attributes.Elements("attribute").Single(a => a.Element("LogicalName")!.Value == "xv_mid").Element("IsPrimaryImage")!.Value = "0";
            var entityImage = new XElement(attributes.Elements("attribute").Single(a => a.Element("LogicalName")!.Value == "xv_zeta"));
            entityImage.SetAttributeValue("PhysicalName", "EntityImage");
            entityImage.Element("Name")!.Value = entityImage.Element("LogicalName")!.Value = "entityimage";
            entityImage.Element("IsCustomField")!.Value = "0";
            entityImage.Element("IsPrimaryImage")!.Value = "1";
            attributes.Add(entityImage);
        });
        await ClearAsync();
        var jobId = Guid.NewGuid();

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = jobId });

        Store.GetEntity("xv_project")!.Attributes.OfType<ImageAttributeMetadata>().Single(a => a.IsPrimaryImage == true).LogicalName.Should().Be("xv_zeta");
        var log = (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
        log.Should().Contain("The primary image of 'xv_project' in the file is 'entityimage', which is not created: the primary image is not carried over. "
                             + "'xv_zeta' is the primary image only because it is the first image column of the table.");
    }

    [Fact]
    public async Task Two_primary_images_in_one_table_are_refused()
    {
        await ProjectWithFileAndImageColumnsAsync();
        var file = await ChangedExportAsync(c => c.Descendants("attribute")
            .Single(a => a.Element("LogicalName")!.Value == "xv_alpha").Element("IsPrimaryImage")!.Value = "1");

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() }));

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("more than one primary image (xv_alpha, xv_mid)");
    }

    [Fact]
    public async Task Export_of_Managed_true_is_400()
    {
        await ImportFixtureAsync();
        var fault = await FaultAsync(() => ExportAsync(Admin, managed: true));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("Managed");
    }

    [Theory]
    [InlineData("ExportAutoNumberingSettings")]
    [InlineData("ExportCalendarSettings")]
    [InlineData("ExportCustomizationSettings")]
    [InlineData("ExportEmailTrackingSettings")]
    [InlineData("ExportGeneralSettings")]
    [InlineData("ExportMarketingSettings")]
    [InlineData("ExportOutlookSynchronizationSettings")]
    [InlineData("ExportRelationshipRoles")]
    [InlineData("ExportIsvConfig")]
    [InlineData("ExportSales")]
    [InlineData("ExportExternalApplications")]
    public async Task Export_options_xrmverse_cannot_honor_are_400_naming_them(string option)
    {
        var request = new ExportSolutionRequest { SolutionName = Fixture, Managed = false };
        request[option] = true;

        var fault = await FaultAsync(() => Admin.ExecuteAsync(request));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(option);
    }

    [Fact]
    public async Task Export_target_version_components_and_unknown_parameters_are_400_naming_them()
    {
        (await FaultAsync(() => Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Fixture, TargetVersion = "9.0.0.0" })))
            .Message.Should().Contain("TargetVersion");
        (await FaultAsync(() => Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = Fixture, ExportComponentsParams = new ExportComponentsParams(),
        }))).Message.Should().Contain("ExportComponentsParams");
        var unknown = new ExportSolutionRequest { SolutionName = Fixture };
        unknown["ExportSomethingNew"] = true;
        (await FaultAsync(() => Admin.ExecuteAsync(unknown))).Message.Should().Contain("ExportSomethingNew");
        // The defaults ServiceClient sends (false, null) are accepted.
        var defaults = new ExportSolutionRequest
        {
            SolutionName = Fixture, Managed = false, ExportAutoNumberingSettings = false, ExportCalendarSettings = false, TargetVersion = null,
        };
        await ImportFixtureAsync();
        await Admin.ExecuteAsync(defaults);
    }

    [Fact]
    public async Task Export_requires_prvExportCustomization()
    {
        await ImportFixtureAsync();
        var root = await RootBusinessUnitAsync(fx);
        var basicOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, basicOid), StandardRoles.BasicUser);
        var customizerOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, customizerOid), StandardRoles.SystemCustomizer);

        var fault = await FaultAsync(() => ExportAsync(ServiceFor(fx, basicOid)));
        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvExportCustomization");

        (await ExportAsync(ServiceFor(fx, customizerOid))).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Export_of_unknown_solution_is_404()
    {
        var fault = await FaultAsync(() => ExportAsync(Admin, "NoSuchSolution"));
        fault.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Export_of_the_system_solutions_is_400()
    {
        (await FaultAsync(() => ExportAsync(Admin, SystemSeed.DefaultSolutionName))).HttpStatus.Should().Be(400);
        (await FaultAsync(() => ExportAsync(Admin, SystemSeed.ActiveSolutionName))).HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Step_on_a_message_without_a_known_dataverse_id_is_400_naming_it()
    {
        await ImportFixtureAsync();
        var stepId = await PluginTestHelper.RegisterStepAsync(Admin, SetAttributeTypeId, "Assign", "xv_project", 40);
        await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = ComponentType.SdkMessageProcessingStep, ComponentId = stepId, SolutionUniqueName = Fixture,
        });

        var fault = await FaultAsync(() => ExportAsync(Admin));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("'Assign'").And.Contain("Dataverse");
    }

    [Fact]
    public async Task Steps_carry_the_dataverse_message_ids()
    {
        await ImportFixtureAsync();
        var customizations = Document(Entries(await ExportAsync(Admin))["customizations.xml"]);

        customizations.Descendants("SdkMessageProcessingStep").Select(s => Guid.Parse(s.Element("SdkMessageId")!.Value))
            .Should().BeEquivalentTo([DataverseSdkMessages.IdOf("Create")!.Value, DataverseSdkMessages.IdOf("Update")!.Value]);
        customizations.Descendants("SdkMessageProcessingStep").Select(s => s.Element("SdkMessageFilterId")!.Value)
            .Should().BeEquivalentTo([SdkMessageCatalog.FilterId("Create", "xv_project").ToString("D"),
                SdkMessageCatalog.FilterId("Update", "xv_project").ToString("D")]);
    }

    [Fact]
    public async Task Kept_unsupported_nodes_are_written_again_in_place()
    {
        var workflow = new XElement("Workflow", new XAttribute("WorkflowId", "{6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e98}"), new XAttribute("Name", "w"));
        // An entity node xrmverse does not apply (charts are applied since plan 3.5 UI-1f).
        var config = new XElement("CustomControlDefaultConfig", new XElement("PrimaryEntityTypeCode", "xv_project"));
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                c.Root!.Element("Workflows")!.Add(new XElement(workflow));
                c.Root!.Element("Entities")!.Elements("Entity").Single(e => e.Element("Name")!.Value == "xv_Project")
                    .Add(new XElement("CustomControlDefaultConfigs", new XElement(config)));
            }),
        });

        var customizations = Document(Entries(await ExportAsync(Admin))["customizations.xml"]);

        var roots = customizations.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        roots.IndexOf("Workflows").Should().Be(roots.IndexOf("Roles") + 1, "a kept node takes its place in the export");
        XNode.DeepEquals(customizations.Root!.Element("Workflows")!.Element("Workflow"), workflow).Should().BeTrue();
        var project = customizations.Root!.Element("Entities")!.Elements("Entity").Single(e => e.Element("Name")!.Value == "xv_Project");
        XNode.DeepEquals(project.Element("CustomControlDefaultConfigs")!.Element("CustomControlDefaultConfig"), config).Should().BeTrue();
    }

    // ---- fix round 1 ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Global_option_set_the_solution_does_not_hold_is_a_missing_dependency()
    {
        await ImportFixtureAsync();
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(new Label("Low", 1033), 100000000)])
            {
                Name = SharedOptionSet, DisplayName = new Label("Shared", 1033),
            },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "xv_project",
            Attribute = new PicklistAttributeMetadata
            {
                SchemaName = "xv_Level", LogicalName = "xv_level", DisplayName = new Label("Level", 1033),
                OptionSet = new OptionSetMetadata { IsGlobal = true, Name = SharedOptionSet },
            },
        });

        var (solution, _) = await ExportDocumentsAsync();

        MissingDependencies(solution).Should().Equal(("9:xv_shared", "2:xv_level"));
        var dependency = solution.Descendants("MissingDependency").Single();
        dependency.Element("Required")!.Attribute("displayName")!.Value.Should().Be("Shared");
        dependency.Element("Dependent")!.Attribute("parentSchemaName")!.Value.Should().Be("xv_project");
    }

    [Fact]
    public async Task Behavior_0_includes_the_relationships_where_the_entity_is_the_referenced_side()
    {
        await ImportFixtureAsync();
        await SecondSolutionAsync((ComponentType.Entity, EntityId("xv_project"), false));

        var (solution, customizations) = await ExportDocumentsAsync(Second);

        customizations.Root!.Element("EntityRelationships")!.Elements("EntityRelationship").Select(r => r.Attribute("Name")!.Value)
            .Should().Contain("xv_project_xv_task_ProjectId");
        var task = EntityElement(customizations, "xv_task");
        task.Should().NotBeNull("the referencing entity is written as a shell with its lookup");
        AttributeNames(task!).Should().BeEquivalentTo("xv_taskid", "xv_name", "xv_projectid");
        MissingDependencies(solution).Should().Contain(("1:xv_task", "10:xv_project_xv_task_ProjectId"));
        Roots(solution).Should().Equal(("1", "xv_project", "0"));
    }

    [Fact]
    public async Task Behavior_1_writes_the_primary_attributes_and_those_the_solution_holds()
    {
        await ImportFixtureAsync();
        await SecondSolutionAsync((ComponentType.Entity, EntityId("xv_project"), true), (ComponentType.Attribute, AttributeId("xv_project", "xv_count"), false));

        var (solution, customizations) = await ExportDocumentsAsync(Second);

        AttributeNames(EntityElement(customizations, "xv_project")!).Should().BeEquivalentTo("xv_projectid", "xv_name", "xv_count");
        customizations.Root!.Element("EntityRelationships")!.Elements().Should().BeEmpty();
        customizations.Descendants("savedquery").Should().BeEmpty();
        Roots(solution).Should().Equal(("1", "xv_project", "1"));
        MissingDependencies(solution).Should().BeEmpty();
    }

    [Fact]
    public async Task Behavior_2_writes_the_entity_as_a_shell()
    {
        await ImportFixtureAsync();
        await SecondSolutionAsync((ComponentType.Entity, EntityId("xv_project"), true));
        await ScalarAsync("UPDATE data.solutioncomponent SET rootcomponentbehavior = 2 WHERE objectid = @id AND solutionid = " +
                          "(SELECT solutionid FROM data.solution WHERE uniquename = @n)", ("@id", EntityId("xv_project")), ("@n", Second));

        var (solution, customizations) = await ExportDocumentsAsync(Second);

        AttributeNames(EntityElement(customizations, "xv_project")!).Should().BeEquivalentTo("xv_projectid", "xv_name");
        Roots(solution).Should().Equal(("1", "xv_project", "2"));
    }

    [Fact]
    public async Task Relationship_without_its_entities_brings_the_referencing_shell_and_its_missing_dependency()
    {
        await ImportFixtureAsync();
        await SecondSolutionAsync((ComponentType.EntityRelationship, Store.GetRelationship("xv_project_xv_task_ProjectId")!.MetadataId!.Value, false));

        var (solution, customizations) = await ExportDocumentsAsync(Second);

        AttributeNames(EntityElement(customizations, "xv_task")!).Should().BeEquivalentTo("xv_taskid", "xv_name", "xv_projectid");
        Roots(solution).Should().Equal(("10", "xv_project_xv_task_ProjectId", "0"));
        MissingDependencies(solution).Should().BeEquivalentTo([
            ("1:xv_task", "10:xv_project_xv_task_ProjectId"), ("1:xv_project", "10:xv_project_xv_task_ProjectId")]);
    }

    [Fact]
    public async Task Attribute_without_its_entity_is_a_root_component_of_a_shell()
    {
        await ImportFixtureAsync();
        var countId = AttributeId("xv_project", "xv_count");
        await SecondSolutionAsync((ComponentType.Attribute, countId, false));

        var (solution, customizations) = await ExportDocumentsAsync(Second);

        AttributeNames(EntityElement(customizations, "xv_project")!).Should().BeEquivalentTo("xv_projectid", "xv_name", "xv_count");
        Roots(solution).Should().Equal(("2", $"{{{countId:D}}}", "0"));
        MissingDependencies(solution).Should().Equal(("1:xv_project", "2:xv_count"));
    }

    [Fact]
    public async Task Many_to_many_menu_is_merged_with_the_navigation_property_names()
    {
        await ImportFixtureAsync();
        var (_, customizations) = await ExportDocumentsAsync();

        var roles = customizations.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
            .Single(r => r.Attribute("Name")!.Value == "xv_project_xv_tag").Descendants("EntityRelationshipRole").ToList();
        roles.Should().HaveCount(2);
        foreach (var role in roles)
        {
            role.Element("NavPaneDisplayOption").Should().NotBeNull("the kept menu configuration is written");
            role.Element("NavigationPropertyName")!.ElementsAfterSelf().Select(e => e.Name.LocalName).Should().Equal("RelationshipRoleType");
        }
        roles.Select(r => r.Element("NavigationPropertyName")!.Value).Should().Equal(
            Store.GetManyToManyRelationships().Single(r => r.SchemaName == "xv_project_xv_tag") is var m
                ? new[] { m.Entity1NavigationPropertyName, m.Entity2NavigationPropertyName }
                : []);
    }

    [Fact]
    public async Task View_and_form_created_in_xrmverse_are_written_from_their_columns()
    {
        await ImportFixtureAsync();
        var viewId = await Admin.CreateAsync(new Entity("savedquery")
        {
            ["name"] = "Made here", ["returnedtypecode"] = "xv_project", ["querytype"] = 0, ["isdefault"] = false,
            ["fetchxml"] = "<fetch><entity name=\"xv_project\"><attribute name=\"xv_name\" /></entity></fetch>",
            ["layoutxml"] = "<grid name=\"resultset\" object=\"10000\" jump=\"xv_name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"xv_projectid\"><cell name=\"xv_name\" width=\"300\" /></row></grid>",
        });
        var formId = await Admin.CreateAsync(new Entity("systemform")
        {
            ["name"] = "Made here", ["objecttypecode"] = "xv_project", ["type"] = new OptionSetValue(2),
            ["formxml"] = "<form><tabs /></form>",
        });

        var (_, customizations) = await ExportDocumentsAsync();

        var view = customizations.Descendants("savedquery").Single(v => Guid.Parse(v.Element("savedqueryid")!.Value) == viewId);
        view.Element("fetchxml")!.Element("fetch")!.Element("entity")!.Attribute("name")!.Value.Should().Be("xv_project");
        view.Element("layoutxml")!.Element("grid").Should().NotBeNull();
        view.Element("querytype")!.Value.Should().Be("0");
        view.Element("LocalizedNames")!.Element("LocalizedName")!.Attribute("description")!.Value.Should().Be("Made here");
        var form = customizations.Descendants("systemform").Single(f => Guid.Parse(f.Element("formid")!.Value) == formId);
        form.Parent!.Attribute("type")!.Value.Should().Be("main");
        form.Element("form")!.Element("tabs").Should().NotBeNull();
        form.Element("LocalizedNames")!.Element("LocalizedName")!.Attribute("description")!.Value.Should().Be("Made here");
    }

    [Fact]
    public async Task DeleteEntity_takes_its_views_and_forms_and_the_solution_still_exports()
    {
        await ImportFixtureAsync();
        var formIds = new List<Guid>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("SELECT systemformid FROM data.systemform WHERE objecttypecode = N'xv_project'", conn);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) formIds.Add(rd.GetGuid(0));
        }
        formIds.Should().NotBeEmpty();
        (await ScalarAsync("SELECT COUNT(*) FROM data.savedquery WHERE returnedtypecode = N'xv_project'")).Should().Be(1);

        // What the delete rules require first: the steps registered on it, the lookup and the many-to-many relationship to it.
        await Admin.DeleteAsync("sdkmessageprocessingstep", new Guid("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21"));
        await Admin.DeleteAsync("sdkmessageprocessingstep", new Guid("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e22"));
        // A deleted step takes its images with it (out of every solution too).
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepid = @id",
            ("@id", new Guid("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e22")))).Should().Be(0);
        await Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = "xv_project_xv_task_ProjectId" });
        await Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = "xv_project_xv_tag" });
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = "xv_project" });

        (await ScalarAsync("SELECT COUNT(*) FROM data.savedquery WHERE returnedtypecode = N'xv_project'")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemform WHERE objecttypecode = N'xv_project'")).Should().Be(0);
        foreach (var id in formIds.Append(ViewId))
            (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid = @id", ("@id", id))).Should().Be(0);

        var (_, customizations) = await ExportDocumentsAsync();
        EntityElement(customizations, "xv_project").Should().BeNull();
        customizations.Descendants("savedquery").Should().BeEmpty();
        customizations.Descendants("systemform").Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateEntity_of_a_kept_table_setting_is_400_and_the_export_keeps_the_imported_value()
    {
        await ImportFixtureAsync();

        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = "xv_project", IsQuickCreateEnabled = true },
        }));
        message.Should().Contain("IsQuickCreateEnabled");

        var (_, customizations) = await ExportDocumentsAsync();
        var entity = EntityElement(customizations, "xv_project")!.Element("EntityInfo")!.Element("entity")!;
        entity.Element("IsQuickCreateEnabled")!.Value.Should().Be("0");
        entity.Element("ChangeTrackingEnabled")!.Value.Should().Be("1");
        entity.Element("IsAuditEnabled")!.Value.Should().Be("1");
    }

    /// <summary>A setting's value as the export writes it (a flag as 1/0, an empty element as null), for comparing.</summary>
    private static string? SettingText(object? value) => value switch
    {
        null or "" => null,
        bool flag => flag ? "1" : "0",
        BooleanManagedProperty managed => SettingText(managed.Value),
        Enum e => e.ToString().ToLowerInvariant(),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string? ExportedText(string value) => value switch
    {
        "" => null,
        "True" or "true" => "1",
        "False" or "false" => "0",
        _ => value.ToLowerInvariant(),
    };

    /// <summary>
    /// Every leaf setting of <paramref name="element"/> that xrmverse does not model and that names a property of
    /// <paramref name="metadata"/> (HasRelatedNotes/Activities as HasNotes/Activities): (name, exported, retrieved).
    /// </summary>
    private static List<(string Name, string? Exported, string? Retrieved)> Settings(XElement element, object metadata, UnmodelledShape shape) =>
        element.Elements()
            .Where(e => !e.HasElements && !shape.Children.Contains(e.Name.LocalName))
            .Select(e => (e, Property: metadata.GetType().GetProperty(e.Name.LocalName switch
            {
                "HasRelatedNotes" => nameof(EntityMetadata.HasNotes),
                "HasRelatedActivities" => nameof(EntityMetadata.HasActivities),
                var n => n,
            })))
            .Where(x => x.Property is not null)
            .Select(x => (x.e.Name.LocalName, ExportedText(x.e.Value), SettingText(x.Property!.GetValue(metadata))?.ToLowerInvariant()))
            .ToList();

    [Fact]
    public async Task Retrieve_of_an_imported_table_shows_the_settings_the_export_writes()
    {
        await ImportFixtureAsync();
        var (_, customizations) = await ExportDocumentsAsync();

        foreach (var logicalName in new[] { "xv_project", "xv_tag" })
        {
            var exported = EntityElement(customizations, logicalName)!.Element("EntityInfo")!.Element("entity")!;
            var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
                new RetrieveEntityRequest { LogicalName = logicalName, EntityFilters = EntityFilters.All })).EntityMetadata;
            var settings = Settings(exported, entity, UnmodelledXml.EntityShape);
            settings.Select(s => s.Name).Should().Contain(["IsQuickCreateEnabled", "EntityColor"]);
            // Modelled since plan 2.11 (not in the kept settings): the read and the export still agree.
            SettingText(entity.HasNotes).Should().Be(ExportedText(exported.Element("HasRelatedNotes")!.Value));
            SettingText(entity.HasActivities).Should().Be(ExportedText(exported.Element("HasRelatedActivities")!.Value));
            // Modelled since plan 2.13 (not in the kept settings): the read and the export still agree.
            SettingText(entity.ChangeTrackingEnabled).Should().Be(exported.Element("ChangeTrackingEnabled")!.Value);
            SettingText(entity.CanChangeTrackingBeEnabled).Should().Be(exported.Element("CanChangeTrackingBeEnabled")!.Value);
            settings.Where(s => s.Exported != s.Retrieved).Should().BeEmpty($"RetrieveEntity of {logicalName} shows what the export writes");

            foreach (var attributeElement in exported.Element("attributes")!.Elements("attribute"))
            {
                var attributeName = attributeElement.Element("LogicalName")!.Value;
                var attribute = entity.Attributes.Single(a => a.LogicalName == attributeName);
                var attributeSettings = Settings(attributeElement, attribute, UnmodelledXml.AttributeShape);
                attributeSettings.Select(s => s.Name).Should().Contain("IsSortableEnabled");
                attributeSettings.Where(s => s.Exported != s.Retrieved).Should().BeEmpty($"{logicalName}.{attributeName} shows what the export writes");

                var retrieved = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(
                    new RetrieveAttributeRequest { EntityLogicalName = logicalName, LogicalName = attributeName })).AttributeMetadata;
                Settings(attributeElement, retrieved, UnmodelledXml.AttributeShape).Where(s => s.Exported != s.Retrieved).Should().BeEmpty();
            }
        }

        var project = ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = "xv_project" })).EntityMetadata;
        project.ChangeTrackingEnabled.Should().BeTrue("the fixture's xv_Project has ChangeTrackingEnabled 1");
        project.IsAuditEnabled.Value.Should().BeTrue();
        var all = ((RetrieveAllEntitiesResponse)await Admin.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity }))
            .EntityMetadata.Single(e => e.LogicalName == "xv_tag");
        all.ChangeTrackingEnabled.Should().BeFalse();
        all.IsAuditEnabled.Value.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateEntity_and_UpdateAttribute_of_an_imported_table_retrieved_and_sent_back_pass()
    {
        await ImportFixtureAsync();
        var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = "xv_project", EntityFilters = EntityFilters.All })).EntityMetadata;
        entity.ChangeTrackingEnabled.Should().BeTrue();
        entity.DisplayName = Label("Projects relabelled");
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = entity });
        Store.GetEntity("xv_project")!.DisplayName.LocalizedLabels.Should().Contain(l => l.Label == "Projects relabelled");

        var attribute = entity.Attributes.Single(a => a.LogicalName == "xv_count");
        attribute.DisplayName = Label("Count relabelled");
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = "xv_project", Attribute = attribute });
        Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_count").DisplayName.LocalizedLabels
            .Should().Contain(l => l.Label == "Count relabelled");

        var (_, customizations) = await ExportDocumentsAsync();
        EntityElement(customizations, "xv_project")!.Element("EntityInfo")!.Element("entity")!.Element("ChangeTrackingEnabled")!.Value.Should().Be("1");
    }

    [Fact]
    public async Task A_view_of_an_entity_not_in_the_metadata_is_400_naming_it()
    {
        await ImportFixtureAsync();
        var solutionId = (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", Fixture)))!;
        var orphan = Guid.NewGuid();
        await ScalarAsync("""
            INSERT INTO data.savedquery (savedqueryid, name, returnedtypecode, querytype) VALUES (@id, N'Orphan', N'xv_gone', 0);
            INSERT INTO data.solutioncomponent (solutioncomponentid, solutionid, objectid, componenttype, rootcomponentbehavior)
            VALUES (NEWID(), @s, @id, 26, 0);
            """, ("@id", orphan), ("@s", solutionId));

        var fault = await FaultAsync(() => ExportAsync(Admin));

        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.Message.Should().Contain("xv_gone");
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<somethingelse />")]
    public async Task Kept_component_xml_that_cannot_be_used_is_an_explicit_error_naming_the_component(string xml)
    {
        await ImportFixtureAsync();
        await ScalarAsync("UPDATE data.savedquery SET xrmverse_componentxml = @xml WHERE savedqueryid = @id", ("@xml", xml), ("@id", ViewId));

        var fault = await FaultAsync(() => ExportAsync(Admin));

        fault.HttpStatus.Should().Be(500);
        fault.Message.Should().Contain(ViewId.ToString("D"));
    }

    [Fact]
    public async Task Export_option_values_that_are_not_bool_are_400_naming_them()
    {
        foreach (var option in new[] { "Managed", "ExportSales" })
        {
            var request = new ExportSolutionRequest { SolutionName = Fixture };
            request[option] = "true";
            var fault = await FaultAsync(() => Admin.ExecuteAsync(request));
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain(option);
        }
    }

    [Fact]
    public async Task Export_is_refused_from_a_plug_in()
    {
        await ImportFixtureAsync();
        var me = (WhoAmIResponse)await Admin.ExecuteAsync(new WhoAmIRequest());
        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);

        var fault = await FaultAsync(() => core.ExecuteNonPipelineAsync(new ExportSolutionRequest { SolutionName = Fixture },
            new Caller(me.UserId, me.BusinessUnitId), new DataSession(conn, null), default));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("plug-in");
    }

    [Fact]
    public async Task What_xrmverse_does_not_model_is_kept_and_updated_by_a_new_import()
    {
        static byte[] File(string quickCreate, string mask, string secret, string hidden, string webHidden) => SolutionFixtureZip.Build(c =>
        {
            c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project").Element("IsQuickCreateEnabled")!.Value = quickCreate;
            var count = c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count");
            count.Element("DisplayMask")!.Value = mask;
            count.Element("IsDataSourceSecret")!.Value = secret;
            c.Root!.Element("optionsets")!.Descendants("option").First().SetAttributeValue("IsHidden", hidden);
            c.Descendants("WebResource").Single().Element("IsHidden")!.Value = webHidden;
        });
        static (string, string, string, string, string) Read(XDocument c)
        {
            var info = EntityElement(c, "xv_project")!.Element("EntityInfo")!.Element("entity")!;
            var count = info.Element("attributes")!.Elements("attribute").Single(a => a.Element("Name")!.Value == "xv_count");
            return (info.Element("IsQuickCreateEnabled")!.Value, count.Element("DisplayMask")!.Value, count.Element("IsDataSourceSecret")!.Value,
                c.Root!.Element("optionsets")!.Descendants("option").First().Attribute("IsHidden")!.Value,
                c.Descendants("WebResource").Single().Element("IsHidden")!.Value);
        }

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = File("1", "ValidForAdvancedFind", "1", "1", "1") });
        Read((await ExportDocumentsAsync()).Customizations).Should().Be(("1", "ValidForAdvancedFind", "1", "1", "1"));

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = File("0", "ValidForGrid", "0", "0", "0") });
        Read((await ExportDocumentsAsync()).Customizations).Should().Be(("0", "ValidForGrid", "0", "0", "0"));
    }

    private static XElement InfoOf(XDocument customizations, string logicalName) =>
        EntityElement(customizations, logicalName)!.Element("EntityInfo")!.Element("entity")!;

    private static XElement ColumnOf(XDocument customizations, string table, string column) =>
        InfoOf(customizations, table).Element("attributes")!.Elements("attribute").Single(a => a.Element("Name")!.Value == column);

    private static List<string> Names(XElement element) => element.Elements().Select(e => e.Name.LocalName).ToList();

    [Fact]
    public async Task Solution_round_trips_is_audit_enabled()
    {
        List<string>? projectOrder = null, countOrder = null;
        var file = SolutionFixtureZip.Build(c =>
        {
            var project = c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
            project.Element("IsAuditEnabled")!.Value = "1";
            c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Tag").Element("IsAuditEnabled")!.Value = "1";
            var count = c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count");
            count.Element("IsAuditEnabled")!.Value = "0";
            projectOrder = Names(project);
            countOrder = Names(count);
        });
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });

        void AssertMetadata()
        {
            Store.GetEntity("xv_project")!.IsAuditEnabled!.Value.Should().BeTrue();
            Store.GetEntity("xv_tag")!.IsAuditEnabled!.Value.Should().BeTrue();
            var count = Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_count").IsAuditEnabled!;
            (count.Value, count.CanBeChanged).Should().Be((false, true));
            Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_name").IsAuditEnabled!.Value.Should().BeTrue();
        }
        AssertMetadata();

        var first = await ExportAsync(Admin);
        var exported = Document(Entries(first)["customizations.xml"]);
        InfoOf(exported, "xv_project").Element("IsAuditEnabled")!.Value.Should().Be("1");
        InfoOf(exported, "xv_tag").Element("IsAuditEnabled")!.Value.Should().Be("1");
        ColumnOf(exported, "xv_project", "xv_count").Element("IsAuditEnabled")!.Value.Should().Be("0");
        ColumnOf(exported, "xv_project", "xv_name").Element("IsAuditEnabled")!.Value.Should().Be("1");
        Names(InfoOf(exported, "xv_project")).IndexOf("IsAuditEnabled").Should().Be(projectOrder!.IndexOf("IsAuditEnabled"), "the element keeps its place");
        Names(InfoOf(exported, "xv_project")).IndexOf("CanModifyAuditSettings").Should().Be(projectOrder.IndexOf("CanModifyAuditSettings"));
        Names(ColumnOf(exported, "xv_project", "xv_count")).IndexOf("IsAuditEnabled").Should().Be(countOrder!.IndexOf("IsAuditEnabled"));

        // Into a fresh organization (nothing imported): the same values come back, and the export is the same.
        await ClearAsync();
        Store.GetEntity("xv_project").Should().BeNull();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = first, ImportJobId = Guid.NewGuid() });
        AssertMetadata();
        XmlNormalizer.Normalize(Document(Entries(await ExportAsync(Admin))["customizations.xml"])).Should().Be(XmlNormalizer.Normalize(exported));

        // A re-import with another value applies it (the table and the column can change).
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project").Element("IsAuditEnabled")!.Value = "0";
                c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count").Element("IsAuditEnabled")!.Value = "1";
            }),
            ImportJobId = Guid.NewGuid(),
        });
        Store.GetEntity("xv_project")!.IsAuditEnabled!.Value.Should().BeFalse();
        Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_count").IsAuditEnabled!.Value.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_value_for_a_fixed_audit_setting_is_warned_on_the_first_and_the_second_import()
    {
        byte[] File() => SolutionFixtureZip.Build(c =>
            c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project").Element("attributes")!.Elements("attribute")
                .Single(a => a.Element("Name")!.Value == "createdon").Element("IsAuditEnabled")!.Value = "1");

        foreach (var import in new[] { "first", "second" })
        {
            var jobId = Guid.NewGuid();
            await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = File(), ImportJobId = jobId });
            var log = (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
            log.Should().Contain("IsAuditEnabled").And.Contain("createdon", $"the {import} import reports the value it does not apply");
            var createdOn = Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "createdon").IsAuditEnabled!;
            (createdOn.Value, createdOn.CanBeChanged).Should().Be((false, false));
        }
    }

    [Fact]
    public async Task Upgrade_moves_kept_audit_settings_into_the_metadata()
    {
        // An organization imported before the setting was modelled: no value in the metadata, the file's elements in the kept XML.
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                var project = c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
                project.Element("IsAuditEnabled")!.Value = "1";
                project.Element("CanModifyAuditSettings")!.Value = "0";
                c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count").Element("IsAuditEnabled")!.Value = "0";
            }),
            ImportJobId = Guid.NewGuid(),
        });
        var before = await ExportAsync(Admin);
        var beforeDocument = Document(Entries(before)["customizations.xml"]);

        var oldKept = new Dictionary<(string Table, string? Column), string>();
        foreach (var entity in Store.GetAllEntities().Where(e => e.LogicalName is "xv_project" or "xv_tag"))
        {
            var info = InfoOf(beforeDocument, entity.LogicalName);
            var keptEntity = XElement.Parse((string)(await ScalarAsync("SELECT unmodelled_xml FROM meta.entity WHERE logical_name = @n", ("@n", entity.LogicalName)))!);
            foreach (var name in new[] { "IsAuditEnabled", "CanModifyAuditSettings" })
                keptEntity.Element(name)!.ReplaceWith(new XElement(name, info.Element(name)!.Value));
            await ScalarAsync("UPDATE meta.entity SET unmodelled_xml = @x, definition = JSON_MODIFY(definition, '$.IsAuditEnabled', NULL) WHERE logical_name = @n",
                ("@x", keptEntity.ToString(SaveOptions.DisableFormatting)), ("@n", entity.LogicalName));
            foreach (var attribute in entity.Attributes)
            {
                var element = info.Element("attributes")!.Elements("attribute").SingleOrDefault(a => a.Element("Name")!.Value == attribute.LogicalName);
                var kept = (string?)await ScalarAsync("SELECT unmodelled_xml FROM meta.attribute WHERE entity_logical_name = @e AND logical_name = @n",
                    ("@e", entity.LogicalName), ("@n", attribute.LogicalName));
                var keptXml = kept is null ? null : XElement.Parse(kept);
                if (keptXml?.Element("IsAuditEnabled") is { } marker && element is not null)
                    marker.ReplaceWith(new XElement("IsAuditEnabled", element.Element("IsAuditEnabled")!.Value));
                await ScalarAsync("UPDATE meta.attribute SET unmodelled_xml = @x, definition = JSON_MODIFY(definition, '$.IsAuditEnabled', NULL) " +
                                  "WHERE entity_logical_name = @e AND logical_name = @n",
                    ("@x", keptXml is null ? DBNull.Value : keptXml.ToString(SaveOptions.DisableFormatting)), ("@e", entity.LogicalName), ("@n", attribute.LogicalName));
            }
        }

        var factory = TestProvider.Factory(fx.ConnectionString);
        var old = new MetadataStore(factory);
        await old.LoadAsync();
        old.GetEntity("xv_project")!.IsAuditEnabled.Should().BeNull("the setting is not in the metadata yet");
        (await ScalarAsync("SELECT unmodelled_xml FROM meta.entity WHERE logical_name = N'xv_project'"))!.ToString().Should().Contain("<IsAuditEnabled>1</IsAuditEnabled>");

        await AuditSettingsUpgrade.EnsureAsync(old, factory);
        var stamp = old.VersionStamp;

        var project = old.GetEntity("xv_project")!;
        (project.IsAuditEnabled!.Value, project.IsAuditEnabled.CanBeChanged).Should().Be((true, false));
        var count = project.Attributes.Single(a => a.LogicalName == "xv_count").IsAuditEnabled!;
        (count.Value, count.CanBeChanged).Should().Be((false, true));
        project.Attributes.Should().OnlyContain(a => a.IsAuditEnabled != null);
        old.GetEntity("xv_tag")!.IsAuditEnabled!.Value.Should().BeFalse();
        var keptAfter = XElement.Parse(old.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!);
        UnmodelledXml.IsMarker(keptAfter.Element("IsAuditEnabled")!).Should().BeTrue("the element is a marker now");
        UnmodelledXml.IsMarker(keptAfter.Element("CanModifyAuditSettings")!).Should().BeTrue();

        // The shared store reads what the upgrade wrote; the export is what it was.
        await Store.LoadAsync();
        var after = Document(Entries(await ExportAsync(Admin))["customizations.xml"]);
        XmlNormalizer.Normalize(after).Should().Be(XmlNormalizer.Normalize(beforeDocument));

        // A second run changes nothing.
        await AuditSettingsUpgrade.EnsureAsync(old, factory);
        old.VersionStamp.Should().Be(stamp);
        var reloaded = new MetadataStore(factory);
        await reloaded.LoadAsync();
        await AuditSettingsUpgrade.EnsureAsync(reloaded, factory);
        reloaded.VersionStamp.Should().Be(old.VersionStamp);
    }

    [Fact]
    public async Task Reimport_that_adds_an_attribute_keeps_what_the_file_gives_it()
    {
        await ImportFixtureAsync();
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                var count = c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count");
                var added = new XElement(count);
                added.SetAttributeValue("PhysicalName", "xv_New");
                added.Element("Name")!.Value = "xv_new";
                added.Element("LogicalName")!.Value = "xv_new";
                added.Element("DisplayMask")!.Value = "ValidForGrid";
                added.Element("IsAuditEnabled")!.Value = "0";
                count.AddAfterSelf(added);
            }),
        });

        var (_, customizations) = await ExportDocumentsAsync();

        var added = EntityElement(customizations, "xv_project")!.Element("EntityInfo")!.Element("entity")!.Element("attributes")!
            .Elements("attribute").Single(a => a.Element("Name")!.Value == "xv_new");
        added.Element("DisplayMask")!.Value.Should().Be("ValidForGrid");
        added.Element("IsAuditEnabled")!.Value.Should().Be("0");
    }

    [Fact]
    public async Task A_shell_in_a_file_does_not_overwrite_what_another_solution_keeps()
    {
        await ImportFixtureAsync();
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            ImportJobId = jobId,
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project").Element("IsQuickCreateEnabled")!.Value = "1";
                var count = c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count");
                count.Element("DisplayMask")!.Value = "ValidForGrid";
                count.Element("IsDataSourceSecret")!.Value = "1";
            }, s =>
            {
                s.Descendants("UniqueName").First().Value = "xv_other";
                s.Descendants("RootComponent").Single(r => (string?)r.Attribute("schemaName") == "xv_project").SetAttributeValue("behavior", "2");
            }),
        });

        var (_, customizations) = await ExportDocumentsAsync();

        var info = EntityElement(customizations, "xv_project")!.Element("EntityInfo")!.Element("entity")!;
        info.Element("IsQuickCreateEnabled")!.Value.Should().Be("0", "the fixture's value stays: the other file has the entity as a shell");
        var count = info.Element("attributes")!.Elements("attribute").Single(a => a.Element("Name")!.Value == "xv_count");
        count.Element("DisplayMask")!.Value.Should().Be("ValidForAdvancedFind|ValidForForm|ValidForGrid");
        count.Element("IsDataSourceSecret")!.Value.Should().Be("0");

        var log = (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
        log.Should().Contain("What the file gives entity 'xv_project' that xrmverse does not model is not applied: the file has the entity as a shell");
        log.Should().Contain("What the file gives attribute 'xv_project.xv_count' that xrmverse does not model is not applied: the file has the entity as a shell");
        log.Should().NotContain("attribute 'xv_project.xv_name' that xrmverse does not model", "an attribute whose XML is the same is not reported");
    }

    [Fact]
    public async Task Kept_component_xml_cannot_be_written_by_a_request()
    {
        await ImportFixtureAsync();
        var webResourceId = Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e41");
        const string forged = "<WebResource><Name>forged</Name></WebResource>";
        var keptResource = await ScalarAsync("SELECT xrmverse_componentxml FROM data.webresource WHERE webresourceid = @id", ("@id", webResourceId));
        var keptView = await ScalarAsync("SELECT xrmverse_componentxml FROM data.savedquery WHERE savedqueryid = @id", ("@id", ViewId));
        keptResource.Should().NotBeNull();

        (await FaultAsync(() => Admin.UpdateAsync(new Entity("webresource", webResourceId) { ["xrmverse_componentxml"] = forged })))
            .Should().Match<XrmdFaultException>(e => e.HttpStatus == 400 && e.Message.Contains("xrmverse_componentxml") && e.Message.Contains("webresource"));
        (await FaultAsync(() => Admin.UpdateAsync(new Entity("savedquery", ViewId) { ["xrmverse_componentxml"] = "<savedquery />" })))
            .Should().Match<XrmdFaultException>(e => e.HttpStatus == 400 && e.Message.Contains("xrmverse_componentxml") && e.Message.Contains("savedquery"));
        (await FaultAsync(() => Admin.CreateAsync(new Entity("webresource")
            {
                ["name"] = "xv_/forged.js", ["webresourcetype"] = new OptionSetValue(3), ["content"] = "",
                ["xrmverse_componentxml"] = forged,
            })))
            .Should().Match<XrmdFaultException>(e => e.HttpStatus == 400 && e.Message.Contains("xrmverse_componentxml"));

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/data/v9.2/webresourceset({webResourceId})")
        {
            Content = new StringContent($$"""{ "xrmverse_componentxml": "{{forged}}" }""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("xrmverse_componentxml");

        (await ScalarAsync("SELECT xrmverse_componentxml FROM data.webresource WHERE webresourceid = @id", ("@id", webResourceId)))
            .Should().Be(keptResource);
        (await ScalarAsync("SELECT xrmverse_componentxml FROM data.savedquery WHERE savedqueryid = @id", ("@id", ViewId))).Should().Be(keptView);
    }

    [Fact]
    public async Task An_attribute_without_a_display_mask_is_exported_without_one()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
                c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count").Element("DisplayMask")!.Remove()),
        });

        var (_, customizations) = await ExportDocumentsAsync();

        EntityElement(customizations, "xv_project")!.Element("EntityInfo")!.Element("entity")!.Element("attributes")!
            .Elements("attribute").Single(a => a.Element("Name")!.Value == "xv_count").Element("DisplayMask").Should().BeNull();
    }

    // ---- plan 4.1a: IsValidForQueue -----------------------------------------------------------------------------------

    [Fact]
    public async Task SolutionRoundTrip_KeepsIsValidForQueue()
    {
        // The file has no element for xv_project; the table is enabled after the import, then exported and imported again.
        await ImportFixtureAsync();
        await Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = "xv_project", IsValidForQueue = new BooleanManagedProperty(true) },
        });

        var exported = await ExportAsync(Admin);
        var customizations = Document(Entries(exported)["customizations.xml"]);
        var project = Info(customizations, "xv_project");
        project.Element("IsValidForQueue")!.Value.Should().Be("1");
        project.Elements().Last().Name.LocalName.Should().Be("IsValidForQueue", "a setting the file did not have goes last");
        Info(customizations, "xv_tag").Element("IsValidForQueue").Should().BeNull("off and not in the file: not written");
        var retrieved = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = "xv_project", EntityFilters = EntityFilters.Entity })).EntityMetadata;
        retrieved.IsValidForQueue.Value.Should().BeTrue();

        // A new organization's view of the file: the table is created from it enabled for queues.
        await SolutionImportTests.ClearImportAsync(fx);
        Store.GetEntity("xv_project").Should().BeNull();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = exported, ImportJobId = Guid.NewGuid() });
        Store.GetEntity("xv_project")!.IsValidForQueue!.Value.Should().BeTrue();
        Store.GetRelationship("xv_project_QueueItems").Should().NotBeNull();
        Info(Document(Entries(await ExportAsync(Admin))["customizations.xml"]), "xv_project").Element("IsValidForQueue")!.Value.Should().Be("1");
    }

    // ---- plan 2.11: activity tables and the HasActivities/HasNotes flags (ruling A19) ---------------------------------------

    private static XElement Info(XDocument customizations, string logicalName) =>
        EntityElement(customizations, logicalName)!.Element("EntityInfo")!.Element("entity")!;

    [Fact]
    public async Task Export_writes_IsActivity_ActivityTypeMask_and_flags_from_metadata()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = CustomActivitySolution.Build(), ImportJobId = Guid.NewGuid() });
        // The metadata changes after the import: the export writes it, not what the file said.
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = "xv_project", HasNotes = true, HasActivities = true } });

        var (_, customizations) = await ExportDocumentsAsync();

        var sms = Info(customizations, CustomActivitySolution.Table);
        sms.Element("IsActivity")!.Value.Should().Be("1");
        sms.Element("ActivityTypeMask")!.Value.Should().Be("1");
        sms.Element("HasRelatedNotes")!.Value.Should().Be("True");
        sms.Element("HasRelatedActivities")!.Value.Should().Be("False", "the file had the element: it is written in its place");
        var project = Info(customizations, "xv_project");
        project.Element("IsActivity")!.Value.Should().Be("0");
        project.Element("ActivityTypeMask")!.Value.Should().BeEmpty();
        project.Element("HasRelatedNotes")!.Value.Should().Be("True");
        project.Element("HasRelatedActivities")!.Value.Should().Be("True");
        var names = project.Elements().Select(e => e.Name.LocalName).ToList();
        names.IndexOf("HasRelatedNotes").Should().Be(names.IndexOf("HasRelatedActivities") - 1, "both keep the place the file gave them");
        Info(customizations, "xv_tag").Element("HasRelatedNotes")!.Value.Should().Be("False");

        // A read shows what the export writes.
        var retrieved = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = CustomActivitySolution.Table, EntityFilters = EntityFilters.Entity })).EntityMetadata;
        retrieved.IsActivity.Should().BeTrue();
        retrieved.ActivityTypeMask.Should().Be(1);
        retrieved.HasNotes.Should().BeTrue();
        retrieved.HasActivities.Should().BeFalse();
    }

    [Fact]
    public async Task Export_omits_a_false_flag_the_file_did_not_have()
    {
        // A real export writes HasRelatedNotes and HasRelatedActivities only when True (tests/fixtures/solutions/RealExports).
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
                c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Tag").Elements()
                    .Where(e => e.Name.LocalName is "HasRelatedNotes" or "HasRelatedActivities").Remove()),
            ImportJobId = Guid.NewGuid(),
        });

        var (_, customizations) = await ExportDocumentsAsync();

        Info(customizations, "xv_tag").Element("HasRelatedNotes").Should().BeNull();
        Info(customizations, "xv_tag").Element("HasRelatedActivities").Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_writes_a_flag_switched_on_after_the_import_last_when_the_file_did_not_have_it(bool activities)
    {
        // The kept XML has no element for the flag: it goes last in the entity, notes before activities, as in a real export
        // (tests/fixtures/solutions/RealExports: after IsSolutionAware).
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
                c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Tag").Elements()
                    .Where(e => e.Name.LocalName is "HasRelatedNotes" or "HasRelatedActivities").Remove()),
            ImportJobId = Guid.NewGuid(),
        });
        await Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = "xv_tag", HasNotes = true, HasActivities = activities ? true : null },
        });

        var (_, customizations) = await ExportDocumentsAsync();

        var names = Info(customizations, "xv_tag").Elements().Select(e => e.Name.LocalName).ToList();
        if (activities) names.TakeLast(2).Should().Equal("HasRelatedNotes", "HasRelatedActivities");
        else
        {
            names.Last().Should().Be("HasRelatedNotes");
            names.Should().NotContain("HasRelatedActivities");
        }
        Info(customizations, "xv_tag").Element("HasRelatedNotes")!.Value.Should().Be("True");
    }

    [Fact]
    public async Task Export_of_custom_activity_carries_only_its_own_columns()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = CustomActivitySolution.Build(), ImportJobId = Guid.NewGuid() });

        var (_, customizations) = await ExportDocumentsAsync();

        customizations.Root!.Element("Entities")!.Elements("Entity").Select(e => e.Element("Name")!.Value.ToLowerInvariant())
            .Should().BeEquivalentTo("xv_project", "xv_tag", "xv_task", "xv_sms");
        var sms = EntityElement(customizations, CustomActivitySolution.Table)!;
        var exported = AttributeNames(sms);
        exported.Should().OnlyHaveUniqueItems();
        exported.Should().BeEquivalentTo(Store.GetEntity(CustomActivitySolution.Table)!.Attributes.Select(a => a.LogicalName),
            "the activity's own columns, the derived ones included, and nothing of the pointer, the parties or the notes");
        XElement Column(string name) => sms.Element("EntityInfo")!.Element("entity")!.Element("attributes")!.Elements("attribute")
            .Single(a => a.Element("Name")!.Value == name);
        Column("to").Element("Type")!.Value.Should().Be("partylist");
        Column("to").Element("IsCustomField")!.Value.Should().Be("0");
        Column("activitytypecode").Element("Type")!.Value.Should().Be("entityname");
        Column("regardingobjectid").Element("Type")!.Value.Should().Be("lookup");
        Column("xv_phonenumber").Element("IsCustomField")!.Value.Should().Be("1");
        Column("activityid").Element("Type")!.Value.Should().Be("primarykey");
        // The relationships of the activity model are derived: none is written.
        customizations.Root!.Element("EntityRelationships")!.Elements("EntityRelationship").Select(r => (string)r.Attribute("Name")!)
            .Should().BeEquivalentTo("xv_project_xv_task_ProjectId", "xv_project_xv_tag");
    }

    [Fact]
    public async Task Round_trip_of_custom_activity_is_stable()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = CustomActivitySolution.Build(), ImportJobId = Guid.NewGuid() });
        var first = await ExportAsync(Admin);

        // Into an organization without the solution (everything the import made is removed first).
        await ClearAsync();
        Store.GetEntity(CustomActivitySolution.Table).Should().BeNull();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = first, ImportJobId = Guid.NewGuid() });
        var second = await ExportAsync(Admin);

        Store.GetEntity(CustomActivitySolution.Table)!.IsActivity.Should().BeTrue();
        ShouldBeSameDocument(XmlNormalizer.Normalize(Document(Entries(second)["customizations.xml"])),
            XmlNormalizer.Normalize(Document(Entries(first)["customizations.xml"])));
    }
}
