using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Xrmd.Integration.Tests;

/// <summary>A custom API of a <see cref="CustomApiTestSolution"/> (plan 15, ruling X7).</summary>
/// <param name="UniqueName">The API's unique name, also its message name.</param>
/// <param name="BindingType">0 global, 1 entity, 2 entity collection.</param>
/// <param name="BoundEntity">The bound table (binding 1 or 2).</param>
/// <param name="IsFunction">A function rather than an action.</param>
/// <param name="IsPrivate">A private API.</param>
/// <param name="AllowedStepType">0 none, 1 asynchronous only, 2 synchronous and asynchronous.</param>
/// <param name="PluginTypeName">The full name of a plug-in type of <c>Xrmd.TestPlugins</c> run as the main operation; null for none.</param>
/// <param name="PrivilegeName">The <c>executeprivilegename</c>; null for none.</param>
/// <param name="Requests">The request parameters: name, type (0–12), optional, <c>logicalentityname</c>.</param>
/// <param name="Responses">The response properties: name and type (0–12).</param>
public sealed record TestApi(string UniqueName, int BindingType = 0, string? BoundEntity = null, bool IsFunction = false,
    bool IsPrivate = false, int AllowedStepType = 0, string? PluginTypeName = null, string? PrivilegeName = null,
    (string Name, int Type, bool Optional, string? Entity)[]? Requests = null, (string Name, int Type)[]? Responses = null);

/// <summary>
/// Builds a minimal unmanaged solution file (plan 15, tasks 3–8) holding the net462 <c>Xrmd.TestPlugins</c> assembly, with
/// one plug-in type per distinct <see cref="TestApi.PluginTypeName"/>, and the <c>customapis/</c> files of its APIs, as a
/// Dataverse export writes them. Its publisher is <see cref="PublisherName"/> (prefix <see cref="Prefix"/>). The ids are
/// derived from the names, so two builds of the same solution are the same solution.
/// </summary>
public static class CustomApiTestSolution
{
    /// <summary>The publisher of the built solutions.</summary>
    public const string PublisherName = "xrmverseapis";

    /// <summary>The customization prefix of <see cref="PublisherName"/>.</summary>
    public const string Prefix = "xt";

    /// <summary>The id of the plug-in assembly of the built solutions.</summary>
    public static readonly Guid AssemblyId = new("7c2d4f5a-3b1e-4d6f-9a8b-0c1d2e3f4a10");

    private const string AssemblyFullName = "Xrmd.TestPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf";

    /// <summary>The <c>plugintypeid</c> a built solution gives the plug-in type <paramref name="typeName"/>.</summary>
    public static Guid PluginTypeId(string typeName) => Derived("plugintype:" + typeName);

    /// <summary>The solution file of <paramref name="apis"/>.</summary>
    public static byte[] Build(string solutionName, params TestApi[] apis) => Build(solutionName, null, apis);

    /// <summary>
    /// The solution file of <paramref name="apis"/>; <paramref name="edit"/> may change the custom API files first (keyed by
    /// their path in the archive: add, remove or rewrite any of them).
    /// </summary>
    public static byte[] Build(string solutionName, Action<IDictionary<string, XElement>>? edit, params TestApi[] apis)
    {
        var types = apis.Select(a => a.PluginTypeName).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var apiFiles = new SortedDictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var api in apis)
        {
            var folder = $"customapis/{api.UniqueName}";
            apiFiles[$"{folder}/customapi.xml"] = Api(api);
            foreach (var (name, type, optional, entity) in api.Requests ?? [])
                apiFiles[$"{folder}/customapirequestparameters/{name}/customapirequestparameter.xml"] =
                    Field("customapirequestparameter", name, type, optional, entity);
            foreach (var (name, type) in api.Responses ?? [])
                apiFiles[$"{folder}/customapiresponseproperties/{name}/customapiresponseproperty.xml"] =
                    Field("customapiresponseproperty", name, type, null, null);
        }
        edit?.Invoke(apiFiles);

        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["solution.xml"] = Bytes(Solution(solutionName)),
            ["customizations.xml"] = Bytes(Customizations(types)),
            [$"PluginAssemblies/XrmdTestPlugins-{AssemblyId.ToString("D").ToUpperInvariant()}/XrmdTestPlugins.dll"] = PluginTestHelper.TestPluginBytes,
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(
                """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /><Default Extension="dll" ContentType="application/octet-stream" /></Types>"""),
        };
        foreach (var (path, element) in apiFiles) entries[path] = Encoding.UTF8.GetBytes(element.ToString());

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Creates <paramref name="apis"/> through the SDK as <paramref name="admin"/> (plan 2.7a, Task 6: the custom API classes run on
    /// PostgreSQL, where solution import is 2.7b): the <c>Xrmd.TestPlugins</c> assembly, one plug-in type per distinct
    /// <see cref="TestApi.PluginTypeName"/> (with the ids <see cref="Build(string, TestApi[])"/> gives them), the publisher
    /// <see cref="PublisherName"/> when it is missing, then each API with its
    /// request parameters and response properties, as the import writes them. Returns the <c>customapiid</c>s.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CreateAsync(Xrmd.Core.OrganizationService admin, params TestApi[] apis)
    {
        // The publisher the import brings: the APIs' names start with its prefix.
        var publisher = new Microsoft.Xrm.Sdk.Query.QueryExpression("publisher") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false) };
        publisher.Criteria.AddCondition("uniquename", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, PublisherName);
        if ((await admin.RetrieveMultipleAsync(publisher)).Entities.Count == 0)
            await admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("publisher")
            {
                ["uniquename"] = PublisherName, ["friendlyname"] = "xrmverse API tests", ["customizationprefix"] = Prefix,
                ["customizationoptionvalueprefix"] = 10001,
            });

        var types = apis.Select(a => a.PluginTypeName).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (types.Count > 0)
        {
            var assembly = await PluginTestHelper.EnsureAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
            foreach (var type in types)
            {
                var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("plugintype") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false) };
                query.Criteria.AddCondition("plugintypeid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, PluginTypeId(type));
                if ((await admin.RetrieveMultipleAsync(query)).Entities.Count > 0) continue;
                await admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("plugintype", PluginTypeId(type))
                {
                    ["typename"] = type, ["name"] = type, ["friendlyname"] = PluginTypeId(type).ToString("D"),
                    ["pluginassemblyid"] = new Microsoft.Xrm.Sdk.EntityReference("pluginassembly", assembly),
                });
            }
        }

        var ids = new List<Guid>();
        foreach (var api in apis)
        {
            // A later "import" of the same API replaces it, as an unmanaged import updates it.
            var existing = new Microsoft.Xrm.Sdk.Query.QueryExpression("customapi") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false) };
            existing.Criteria.AddCondition("uniquename", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, api.UniqueName);
            foreach (var old in (await admin.RetrieveMultipleAsync(existing)).Entities) await admin.DeleteAsync("customapi", old.Id);
            var row = new Microsoft.Xrm.Sdk.Entity("customapi")
            {
                ["uniquename"] = api.UniqueName, ["name"] = api.UniqueName, ["displayname"] = api.UniqueName,
                ["description"] = $"The test API {api.UniqueName}",
                ["bindingtype"] = new Microsoft.Xrm.Sdk.OptionSetValue(api.BindingType),
                ["isfunction"] = api.IsFunction, ["isprivate"] = api.IsPrivate,
                ["allowedcustomprocessingsteptype"] = new Microsoft.Xrm.Sdk.OptionSetValue(api.AllowedStepType),
                ["workflowsdkstepenabled"] = false,
            };
            if (api.BoundEntity is not null) row["boundentitylogicalname"] = api.BoundEntity;
            if (api.PrivilegeName is not null) row["executeprivilegename"] = api.PrivilegeName;
            if (api.PluginTypeName is not null) row["plugintypeid"] = new Microsoft.Xrm.Sdk.EntityReference("plugintype", PluginTypeId(api.PluginTypeName));
            var id = await admin.CreateAsync(row);
            ids.Add(id);
            foreach (var (name, type, optional, entity) in api.Requests ?? [])
            {
                var field = new Microsoft.Xrm.Sdk.Entity("customapirequestparameter")
                {
                    ["uniquename"] = name, ["name"] = name, ["displayname"] = name, ["description"] = $"The test field {name}",
                    ["type"] = new Microsoft.Xrm.Sdk.OptionSetValue(type), ["isoptional"] = optional,
                    ["customapiid"] = new Microsoft.Xrm.Sdk.EntityReference("customapi", id),
                };
                if (entity is not null) field["logicalentityname"] = entity;
                await admin.CreateAsync(field);
            }
            foreach (var (name, type) in api.Responses ?? [])
                await admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("customapiresponseproperty")
                {
                    ["uniquename"] = name, ["name"] = name, ["displayname"] = name, ["description"] = $"The test field {name}",
                    ["type"] = new Microsoft.Xrm.Sdk.OptionSetValue(type),
                    ["customapiid"] = new Microsoft.Xrm.Sdk.EntityReference("customapi", id),
                });
        }
        return ids;
    }

    private static XElement Api(TestApi api) => new("customapi", new XAttribute("uniquename", api.UniqueName),
        new XElement("allowedcustomprocessingsteptype", api.AllowedStepType),
        new XElement("bindingtype", api.BindingType),
        api.BoundEntity is null ? null : new XElement("boundentitylogicalname", api.BoundEntity),
        Labels("description", $"The test API {api.UniqueName}"),
        Labels("displayname", api.UniqueName),
        api.PrivilegeName is null ? null : new XElement("executeprivilegename", api.PrivilegeName),
        new XElement("iscustomizable", 1),
        new XElement("isfunction", api.IsFunction ? 1 : 0),
        new XElement("isprivate", api.IsPrivate ? 1 : 0),
        new XElement("name", api.UniqueName),
        api.PluginTypeName is null ? null : new XElement("plugintypeid", new XElement("plugintypeexportkey", PluginTypeId(api.PluginTypeName))),
        new XElement("workflowsdkstepenabled", 0));

    private static XElement Field(string root, string name, int type, bool? optional, string? entity) => new(root,
        new XAttribute("uniquename", name),
        Labels("description", $"The test field {name}"),
        Labels("displayname", name),
        new XElement("iscustomizable", 1),
        optional is { } o ? new XElement("isoptional", o ? 1 : 0) : null,
        entity is null ? null : new XElement("logicalentityname", entity),
        new XElement("name", name),
        new XElement("type", type));

    private static XElement Labels(string name, string text) =>
        new(name, new XAttribute("default", text), new XElement("label", new XAttribute("description", text), new XAttribute("languagecode", 1033)));

    private static XDocument Solution(string solutionName) => new(new XElement("ImportExportXml",
        new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
        new XAttribute("generatedBy", "CrmLive"),
        new XElement("SolutionManifest",
            new XElement("UniqueName", solutionName),
            new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", solutionName), new XAttribute("languagecode", 1033))),
            new XElement("Descriptions"),
            new XElement("Version", "1.0.0.0"),
            new XElement("Managed", 0),
            new XElement("Publisher",
                new XElement("UniqueName", PublisherName),
                new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", "xrmverse API tests"), new XAttribute("languagecode", 1033))),
                new XElement("Descriptions"),
                new XElement("CustomizationPrefix", Prefix),
                new XElement("CustomizationOptionValuePrefix", 10001)),
            new XElement("RootComponents",
                new XElement("RootComponent", new XAttribute("type", 91), new XAttribute("id", AssemblyId.ToString("B")),
                    new XAttribute("schemaName", AssemblyFullName), new XAttribute("behavior", 0))),
            new XElement("MissingDependencies"))));

    private static XDocument Customizations(IReadOnlyList<string> types) => new(new XElement("ImportExportXml",
        new XElement("Entities"), new XElement("Roles"), new XElement("Workflows"), new XElement("FieldSecurityProfiles"),
        new XElement("Templates"), new XElement("EntityMaps"), new XElement("EntityRelationships"), new XElement("OrganizationSettings"),
        new XElement("optionsets"), new XElement("CustomControls"),
        new XElement("SolutionPluginAssemblies",
            new XElement("PluginAssembly", new XAttribute("FullName", AssemblyFullName), new XAttribute("PluginAssemblyId", AssemblyId.ToString("D")),
                new XAttribute("CustomizationLevel", 1),
                new XElement("IsolationMode", 2),
                new XElement("SourceType", 0),
                new XElement("IntroducedVersion", "1.0"),
                new XElement("FileName", $"/PluginAssemblies/XrmdTestPlugins-{AssemblyId.ToString("D").ToUpperInvariant()}/XrmdTestPlugins.dll"),
                new XElement("PluginTypes", types.Select(t => new XElement("PluginType",
                    new XAttribute("AssemblyQualifiedName", $"{t}, {AssemblyFullName}"), new XAttribute("PluginTypeId", PluginTypeId(t).ToString("D")),
                    new XAttribute("Name", t),
                    new XElement("FriendlyName", PluginTypeId(t).ToString("D"))))))),
        new XElement("SdkMessageProcessingSteps"),
        new XElement("Languages", new XElement("Language", 1033))));

    private static Guid Derived(string text) => new(MD5.HashData(Encoding.UTF8.GetBytes("xrmverse-tests:" + text)));

    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
