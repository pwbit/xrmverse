using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline.Registration;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Registers plug-in assemblies, types, steps and images through the <see cref="OrganizationService"/>.
/// The registration tables are shared by the whole "xrmd" collection: every plug-in test class must call
/// <see cref="ClearAsync(XrmdFixture)"/> (at start and in DisposeAsync), which also invalidates the host's StepRegistry,
/// so no cached steps leak between classes.
/// </summary>
public static class PluginTestHelper
{
    /// <summary>The bytes of the net462 <c>Xrmd.TestPlugins.dll</c> copied next to the test assembly.</summary>
    public static byte[] TestPluginBytes { get; } = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll"));

    /// <summary>The bytes of the early-bound net462 <c>Xrmd.TestPlugins.EarlyBound.dll</c> (<c>[assembly: ProxyTypesAssembly]</c>).</summary>
    public static byte[] EarlyBoundPluginBytes { get; } =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.EarlyBound.dll"));

    /// <summary>
    /// The bytes of the net462 <c>Xrmd.TestWorkflowActivities.dll</c> (custom workflow activities built against the real
    /// <c>Microsoft.CrmSdk.Workflow</c>, plan 2.14 W17) copied next to the test assembly.
    /// </summary>
    public static byte[] TestWorkflowActivityBytes { get; } =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestWorkflowActivities.dll"));

    /// <summary>Creates a pluginassembly named <paramref name="name"/> with the base64 of <paramref name="content"/>.</summary>
    public static Task<Guid> RegisterAssemblyAsync(OrganizationService s, byte[] content, string name = "Xrmd.TestPlugins") =>
        s.CreateAsync(new Entity("pluginassembly")
        {
            ["name"] = name,
            ["content"] = Convert.ToBase64String(content),
        });

    /// <summary>
    /// The id of the pluginassembly named <paramref name="name"/>, registered with <paramref name="content"/> when there is
    /// none yet: an assembly name is unique (issue #70), so a class that registers it in several tests reuses it.
    /// </summary>
    public static async Task<Guid> EnsureAssemblyAsync(OrganizationService s, byte[] content, string name = "Xrmd.TestPlugins")
    {
        var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("pluginassembly") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false) };
        query.Criteria.AddCondition("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, name);
        var existing = (await s.RetrieveMultipleAsync(query)).Entities;
        return existing.Count > 0 ? existing[0].Id : await RegisterAssemblyAsync(s, content, name);
    }

    /// <summary>Creates a plugintype.</summary>
    public static Task<Guid> RegisterTypeAsync(OrganizationService s, Guid assemblyId, string typeName) =>
        s.CreateAsync(new Entity("plugintype")
        {
            ["typename"] = typeName,
            ["name"] = typeName,
            ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
        });

    /// <summary>Creates a sdkmessageprocessingstep for the message (and the entity filter when <paramref name="entity"/> is set).</summary>
    public static Task<Guid> RegisterStepAsync(OrganizationService s, Guid typeId, string message, string? entity, int stage,
        int mode = 0, int rank = 1, string? config = null, string? filteringAttributes = null, Guid? impersonatingUserId = null)
    {
        var step = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = $"{message}:{entity}:{stage}:{rank}",
            ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId(message)),
            ["eventhandler"] = new EntityReference("plugintype", typeId),
            ["plugintypeid"] = new EntityReference("plugintype", typeId),
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["rank"] = rank,
        };
        if (entity is not null)
            step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId(message, entity));
        if (config is not null) step["configuration"] = config;
        if (filteringAttributes is not null) step["filteringattributes"] = filteringAttributes;
        if (impersonatingUserId is not null) step["impersonatinguserid"] = new EntityReference("systemuser", impersonatingUserId.Value);
        return s.CreateAsync(step);
    }

    /// <summary>Creates a sdkmessageprocessingstepimage.</summary>
    public static Task<Guid> RegisterImageAsync(OrganizationService s, Guid stepId, int imageType, string alias, string? attributes = null)
    {
        var image = new Entity("sdkmessageprocessingstepimage")
        {
            ["name"] = alias,
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(imageType),
            ["entityalias"] = alias,
            ["messagepropertyname"] = "Target",
        };
        if (attributes is not null) image["attributes"] = attributes;
        return s.CreateAsync(image);
    }

    /// <summary>
    /// <see cref="ClearAsync(string)"/>, then invalidates the host's <see cref="StepRegistry"/> so that no cached step of
    /// a deleted registration fires in a later test.
    /// </summary>
    public static async Task ClearAsync(XrmdFixture fx)
    {
        await ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
        fx.App.Services.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>().Invalidate();
    }

    /// <summary>
    /// Deletes every step, image, custom API (with its parameters and properties), type, assembly and custom message
    /// (<c>categoryname</c> CustomAction or CustomAPI, with their filters) directly (the
    /// registration tables are shared by the "xrmd" collection). A process that keeps the message of a XAML action (W14) loses
    /// its <c>sdkmessageid</c> with it (plan 2.7b, Task 7): a later import of the process would otherwise write the reference to
    /// the deleted message back and fail ("sdkmessage With Id = … Does Not Exist").
    /// </summary>
    public static async Task ClearAsync(string connectionString)
    {
        // Plan 2.7a, Task 5 fix round 1: the run's provider and statements both engines run (no IF OBJECT_ID, no DELETE-alias-JOIN).
        await using var conn = await TestProvider.OpenAsync(connectionString);
        var d = SqlDialects.For(conn);
        var customApi = string.Join("\n", new[] { "customapirequestparameter", "customapiresponseproperty", "customapi" }
            .Select(t => d.When(d.TableExists("data", "customapi"), $"DELETE FROM data.{t}")));
        await using var cmd = TestSql.Command(conn, $"""
            DELETE FROM data.sdkmessageprocessingstepimage; DELETE FROM data.sdkmessageprocessingstep;
            {customApi}
            DELETE FROM data.plugintype; DELETE FROM data.pluginassembly;
            UPDATE data.workflow SET sdkmessageid = NULL
            WHERE sdkmessageid IN (SELECT m.sdkmessageid FROM data.sdkmessage AS m WHERE m.categoryname IN ('CustomAction', 'CustomAPI'));
            DELETE FROM data.sdkmessagefilter
            WHERE sdkmessageid IN (SELECT m.sdkmessageid FROM data.sdkmessage AS m WHERE m.categoryname IN ('CustomAction', 'CustomAPI'));
            DELETE FROM data.sdkmessage WHERE categoryname IN ('CustomAction', 'CustomAPI');
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
