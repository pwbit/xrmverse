using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #143: two custom tables related one-to-many (a parent and a child with the lookup <c>new_parentid</c>), and a solution of our own
/// that carries only an <c>EntityMaps</c> node, written by hand in the shape of a real export (no file from an environment).
/// </summary>
internal sealed class AttributeMapTestKit(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>The parent table: <c>new_name</c>, <c>new_code</c> (text 100), <c>new_amount</c> (whole number), <c>new_ownparentid</c>
    /// (lookup to itself).</summary>
    public string Parent => $"new_par{_run}";

    /// <summary>The child table: <c>new_name</c>, <c>new_code</c> (text 100), <c>new_short</c> (text 50), <c>new_qty</c> (whole number),
    /// <c>new_parentid</c> (lookup to the parent).</summary>
    public string Child => $"new_chi{_run}";

    /// <summary>The one-to-many relationship from the parent to the child.</summary>
    public string Relationship => $"new_{Child}_parent";

    public string Solution => $"maps{_run}";

    public async Task CreateTablesAsync()
    {
        await TableAsync(Parent, Text("new_code", 100), Whole("new_amount"));
        await TableAsync(Child, Text("new_code", 100), Text("new_short", 50), Whole("new_qty"));
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = Relationship, ReferencedEntity = Parent, ReferencingEntity = Child,
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", SchemaName = "new_ParentId", DisplayName = Label("Parent") },
        });
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"new_{Parent}_ownparent", ReferencedEntity = Parent, ReferencingEntity = Parent,
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_ownparentid", SchemaName = "new_OwnParentId", DisplayName = Label("Own parent") },
        });
    }

    private async Task TableAsync(string name, params AttributeMetadata[] columns)
    {
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = Text("new_name", 100),
        });
        foreach (var column in columns)
            await Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = name, Attribute = column });
    }

    private static StringAttributeMetadata Text(string name, int length) => new()
    {
        LogicalName = name, SchemaName = name, MaxLength = length, DisplayName = Label(name),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
    };

    private static IntegerAttributeMetadata Whole(string name) => new()
    {
        LogicalName = name, SchemaName = name, DisplayName = Label(name),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
    };

    /// <summary>An <c>EntityMap</c> element from <paramref name="source"/> to <paramref name="target"/>.</summary>
    public static XElement Map(string source, string target, params (string Source, string Target)[] attributes) =>
        new("EntityMap", new XElement("EntitySource", source), new XElement("EntityTarget", target),
            new XElement("AttributeMaps", attributes.Select(a => new XElement("AttributeMap",
                new XElement("AttributeSource", a.Source), new XElement("AttributeTarget", a.Target)))));

    /// <summary>Imports the kit's solution with <paramref name="maps"/> as its <c>EntityMaps</c>; returns the import job id.</summary>
    public async Task<Guid> ImportAsync(params XElement[] maps)
    {
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = Zip(maps), ImportJobId = jobId });
        return jobId;
    }

    /// <summary>
    /// Writes <paramref name="maps"/> as the import writes them (as <see cref="Caller.Internal"/>, a new entity map per pair and an
    /// attribute map per column), without a solution import: the setup of the tests of what the maps do (InitializeFrom), which run
    /// on PostgreSQL too (plan 2.7a, Task 6; the import itself is a 2.7b feature there).
    /// </summary>
    public async Task WriteMapsAsync(params XElement[] maps)
    {
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            foreach (var map in maps)
            {
                var mapId = Guid.NewGuid();
                await core.ExecuteCoreAsync(new CreateRequest
                {
                    Target = new Entity(AttributeMapSchema.EntityMap, mapId)
                    {
                        [AttributeMapSchema.SourceEntity] = (string)map.Element("EntitySource")!,
                        [AttributeMapSchema.TargetEntity] = (string)map.Element("EntityTarget")!,
                    },
                }, Caller.Internal, s, default);
                foreach (var attribute in map.Element("AttributeMaps")!.Elements("AttributeMap"))
                    await core.ExecuteCoreAsync(new CreateRequest
                    {
                        Target = new Entity(AttributeMapSchema.AttributeMap, Guid.NewGuid())
                        {
                            [AttributeMapSchema.EntityMapLookup] = new EntityReference(AttributeMapSchema.EntityMap, mapId),
                            [AttributeMapSchema.SourceAttribute] = (string)attribute.Element("AttributeSource")!,
                            [AttributeMapSchema.TargetAttribute] = (string)attribute.Element("AttributeTarget")!,
                            [AttributeMapSchema.IsSystem] = false,
                        },
                    }, Caller.Internal, s, default);
            }
        });
    }

    private byte[] Zip(XElement[] maps)
    {
        var solution = new XDocument(new XElement("ImportExportXml",
            new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
            new XAttribute("generatedBy", "CrmLive"),
            new XElement("SolutionManifest",
                new XElement("UniqueName", Solution),
                new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", Solution), new XAttribute("languagecode", 1033))),
                new XElement("Descriptions"),
                new XElement("Version", "1.0.0.0"),
                new XElement("Managed", 0),
                new XElement("Publisher",
                    new XElement("UniqueName", "xrmvmaps"),
                    new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", "xrmverse map tests"), new XAttribute("languagecode", 1033))),
                    new XElement("Descriptions"),
                    new XElement("CustomizationPrefix", "new"),
                    new XElement("CustomizationOptionValuePrefix", 10000)),
                new XElement("RootComponents"),
                new XElement("MissingDependencies"))));
        var customizations = new XDocument(new XElement("ImportExportXml",
            new XElement("Entities"), new XElement("Roles"), new XElement("Workflows"), new XElement("FieldSecurityProfiles"),
            new XElement("Templates"), new XElement("EntityMaps", maps), new XElement("EntityRelationships"), new XElement("OrganizationSettings"),
            new XElement("optionsets"), new XElement("CustomControls"), new XElement("SolutionPluginAssemblies"),
            new XElement("SdkMessageProcessingSteps"), new XElement("Languages", new XElement("Language", 1033))));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, document) in new[] { ("solution.xml", solution), ("customizations.xml", customizations) })
            {
                using var stream = zip.CreateEntry(name).Open();
                document.Save(stream);
            }
            using (var types = zip.CreateEntry("[Content_Types].xml").Open())
                new XDocument(new XElement(XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"))).Save(types);
        }
        return output.ToArray();
    }
}
