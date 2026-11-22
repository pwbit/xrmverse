using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>Validation of Create/Update of the plug-in registration entities (before the core operation).</summary>
[Collection("xrmd")]
public sealed class RegistrationValidationTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string Plugins = "Xrmd.TestPlugins.";

    private OrganizationService _svc = null!;
    private Guid _assemblyId;
    private Guid _typeId;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
        _typeId = await PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, Plugins + "SetAttributePlugin");
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    private static async Task ShouldFailAsync(Func<Task> act, string message)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Be(message);
    }

    private Entity Step(string message, string? entity, int stage, int mode = 0)
    {
        var step = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = "s",
            ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId(message)),
            ["plugintypeid"] = new EntityReference("plugintype", _typeId),
            ["eventhandler"] = new EntityReference("plugintype", _typeId),
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["rank"] = 1,
        };
        if (entity is not null)
            step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId(message, entity));
        return step;
    }

    [Fact]
    public async Task Valid_registration_fills_assembly_metadata()
    {
        // The name filled from the content is the one of the assembly InitializeAsync registered: start without it.
        await PluginTestHelper.ClearAsync(fx);
        var id = await _svc.CreateAsync(new Entity("pluginassembly")
        {
            ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
            ["sourcetype"] = new OptionSetValue(0),
            ["isolationmode"] = new OptionSetValue(2),
        });

        var row = await _svc.RetrieveAsync("pluginassembly", id, new ColumnSet("name", "version", "culture", "publickeytoken"));
        var expected = AssemblyName.GetAssemblyName(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll"));
        row.GetAttributeValue<string>("name").Should().Be("Xrmd.TestPlugins");
        row.GetAttributeValue<string>("version").Should().Be(expected.Version!.ToString());
        row.GetAttributeValue<string>("culture").Should().Be("neutral");
        row.GetAttributeValue<string>("publickeytoken").Should()
            .Be(Convert.ToHexString(expected.GetPublicKeyToken()!).ToLowerInvariant()).And.NotBeEmpty();

        var type = await PluginTestHelper.RegisterTypeAsync(_svc, id, Plugins + "ContextRecorderPlugin");
        _typeId = type;
        var step = await _svc.CreateAsync(Step("Update", Pipe, 40, mode: 1));
        await _svc.UpdateAsync(new Entity("sdkmessageprocessingstep", step) { ["filteringattributes"] = "pb_name, PB_COUNT" });
        type.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Explicit_assembly_metadata_is_kept()
    {
        var id = await _svc.CreateAsync(new Entity("pluginassembly")
        {
            ["name"] = "Custom", ["version"] = "9.9.9.9",
            ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
        });

        var row = await _svc.RetrieveAsync("pluginassembly", id, new ColumnSet("name", "version", "culture"));
        row.GetAttributeValue<string>("name").Should().Be("Custom");
        row.GetAttributeValue<string>("version").Should().Be("9.9.9.9");
        row.GetAttributeValue<string>("culture").Should().Be("neutral");
    }

    private static async Task ShouldBeDuplicateAsync(Func<Task> act, string name)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(412);
        fault.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        fault.Message.Should().StartWith($"A plug-in assembly named '{name}' already exists");
    }

    [Theory]
    [InlineData("Xrmd.TestPlugins")]
    [InlineData("XRMD.testplugins")]
    [InlineData(null)] // the name filled from the content
    public async Task Assembly_name_is_unique_ignoring_case(string? name)
    {
        var assembly = new Entity("pluginassembly") { ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes) };
        if (name is not null) assembly["name"] = name;

        await ShouldBeDuplicateAsync(() => _svc.CreateAsync(assembly), name ?? "Xrmd.TestPlugins");

        (await _svc.RetrieveMultipleAsync(new QueryExpression("pluginassembly"))).Entities.Should().ContainSingle();
    }

    [Fact]
    public async Task Web_api_post_of_an_existing_assembly_name_is_412_duplicate_record()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/pluginassemblies")
        {
            Content = new StringContent(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "xrmd.testplugins",
                ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
            }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));

        var response = await fx.Client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.PreconditionFailed, body);
        var error = System.Text.Json.Nodes.JsonNode.Parse(body)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040237");
        error["message"]!.GetValue<string>().Should().StartWith("A plug-in assembly named 'xrmd.testplugins' already exists");
    }

    [Fact]
    public async Task Renaming_an_assembly_to_the_name_of_another_is_refused_but_its_own_name_is_kept()
    {
        var other = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes, "Other");

        await ShouldBeDuplicateAsync(() => _svc.UpdateAsync(new Entity("pluginassembly", other) { ["name"] = "xrmd.TestPlugins" }),
            "xrmd.TestPlugins");
        await _svc.UpdateAsync(new Entity("pluginassembly", _assemblyId)
            { ["name"] = "Xrmd.TestPlugins", ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes) });
        await _svc.UpdateAsync(new Entity("pluginassembly", other) { ["name"] = "OTHER" });
    }

    [Theory]
    [InlineData("AAEC")] // valid base64, not an assembly
    [InlineData("not base64!")]
    [InlineData("corrupt")]
    public async Task Assembly_content_must_be_a_valid_assembly(string content)
    {
        if (content == "corrupt")
            content = Convert.ToBase64String(PluginTestHelper.TestPluginBytes.Take(512).Concat(Enumerable.Repeat((byte)0xAB, 4096)).ToArray());

        await ShouldFailAsync(() => _svc.CreateAsync(new Entity("pluginassembly") { ["name"] = "x", ["content"] = content }),
            "The assembly content is not a valid plug-in assembly.");
        await ShouldFailAsync(() => _svc.UpdateAsync(new Entity("pluginassembly", _assemblyId) { ["content"] = content }),
            "The assembly content is not a valid plug-in assembly.");
    }

    [Theory]
    [InlineData("Xrmd.TestPlugins.Missing")]
    [InlineData("Xrmd.TestPlugins.NotAPlugin")]
    public async Task Plugin_type_must_exist_and_implement_IPlugin(string typeName)
    {
        await ShouldFailAsync(() => PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, typeName),
            $"Plug-in type '{typeName}' was not found in assembly 'Xrmd.TestPlugins' or does not implement IPlugin.");
        await ShouldFailAsync(() => _svc.UpdateAsync(new Entity("plugintype", _typeId) { ["typename"] = typeName }),
            $"Plug-in type '{typeName}' was not found in assembly 'Xrmd.TestPlugins' or does not implement IPlugin.");
    }

    [Fact]
    public async Task Registering_codeactivity_type_is_accepted()
    {
        // Plan 2.14, W17: the shape the Plugin Registration Tool sends for a custom workflow activity. The type is checked by
        // its base type's full name, from the metadata: no copy of the assembly is loaded.
        const string activity = "Xrmd.TestWorkflowActivities.CreateTaskActivity";
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestWorkflowActivityBytes, "Xrmd.TestWorkflowActivities");
        var flagged = await _svc.CreateAsync(new Entity("plugintype")
        {
            ["typename"] = activity,
            ["name"] = activity,
            ["friendlyname"] = "Create a task",
            ["workflowactivitygroupname"] = "Xrmd.TestWorkflowActivities (1.0.0.0)",
            ["isworkflowactivity"] = true,
            ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
        });
        // Without the flag the server sets it, as Dataverse does.
        var unflagged = await PluginTestHelper.RegisterTypeAsync(_svc, assemblyId, activity);

        foreach (var id in new[] { flagged, unflagged })
        {
            var row = await _svc.RetrieveAsync("plugintype", id, new ColumnSet("isworkflowactivity", "typename"));
            row.GetAttributeValue<bool?>("isworkflowactivity").Should().BeTrue();
            row.GetAttributeValue<string>("typename").Should().Be(activity);
        }
        (await _svc.RetrieveAsync("plugintype", flagged, new ColumnSet("workflowactivitygroupname")))
            .GetAttributeValue<string>("workflowactivitygroupname").Should().Be("Xrmd.TestWorkflowActivities (1.0.0.0)");
        // A plug-in type stays a plug-in.
        (await _svc.RetrieveAsync("plugintype", _typeId, new ColumnSet("isworkflowactivity")))
            .GetAttributeValue<bool?>("isworkflowactivity").Should().NotBe(true);
        System.Runtime.Loader.AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
            .Should().NotContain(a => a.GetName().Name == "Xrmd.TestWorkflowActivities", "the check reads the metadata only");

        // Review M4: an activity deriving from CodeActivity through a base class of its own is accepted; the abstract base is not.
        var derived = await PluginTestHelper.RegisterTypeAsync(_svc, assemblyId, "Xrmd.TestWorkflowActivities.DerivedActivity");
        (await _svc.RetrieveAsync("plugintype", derived, new ColumnSet("isworkflowactivity")))
            .GetAttributeValue<bool?>("isworkflowactivity").Should().BeTrue();
        await ShouldFailAsync(() => PluginTestHelper.RegisterTypeAsync(_svc, assemblyId, "Xrmd.TestWorkflowActivities.ActivityBase"),
            "Plug-in type 'Xrmd.TestWorkflowActivities.ActivityBase' was not found in assembly 'Xrmd.TestWorkflowActivities' or does not implement IPlugin.");

        // A class that is neither an activity nor a plug-in is refused, as before.
        await ShouldFailAsync(() => PluginTestHelper.RegisterTypeAsync(_svc, assemblyId, "Xrmd.TestWorkflowActivities.NotAnActivity"),
            "Plug-in type 'Xrmd.TestWorkflowActivities.NotAnActivity' was not found in assembly 'Xrmd.TestWorkflowActivities' or does not implement IPlugin.");
    }

    [Fact]
    public async Task Workflow_activity_flag_on_a_plugin_type_is_rejected()
    {
        await ShouldFailAsync(() => _svc.CreateAsync(new Entity("plugintype")
            {
                ["typename"] = Plugins + "SetAttributePlugin",
                ["pluginassemblyid"] = new EntityReference("pluginassembly", _assemblyId),
                ["isworkflowactivity"] = true,
            }),
            "Plug-in type 'Xrmd.TestPlugins.SetAttributePlugin' is marked as a workflow activity but does not derive from System.Activities.CodeActivity.");
        await ShouldFailAsync(() => _svc.UpdateAsync(new Entity("plugintype", _typeId) { ["isworkflowactivity"] = true }),
            "Plug-in type 'Xrmd.TestPlugins.SetAttributePlugin' is marked as a workflow activity but does not derive from System.Activities.CodeActivity.");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    public async Task Stage_must_be_10_20_or_40(int stage)
    {
        await ShouldFailAsync(() => _svc.CreateAsync(Step("Create", Pipe, stage)), $"Invalid stage {stage}.");
        var id = await _svc.CreateAsync(Step("Create", Pipe, 20));
        await ShouldFailAsync(() => _svc.UpdateAsync(new Entity("sdkmessageprocessingstep", id) { ["stage"] = new OptionSetValue(stage) }),
            $"Invalid stage {stage}.");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task Asynchronous_steps_must_be_on_stage_40(int stage)
    {
        await ShouldFailAsync(() => _svc.CreateAsync(Step("Create", Pipe, stage, mode: 1)),
            "Asynchronous steps must be registered on stage 40.");
    }

    [Fact]
    public async Task Filtering_attributes_only_on_Update()
    {
        var step = Step("Create", Pipe, 20);
        step["filteringattributes"] = "pb_name";
        await ShouldFailAsync(() => _svc.CreateAsync(step), "Filtering attributes are supported only for the Update and UpdateMultiple messages.");
    }

    [Fact]
    public async Task Message_filter_must_belong_to_the_message()
    {
        var step = Step("Update", null, 20);
        step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId("Create", Pipe));
        await ShouldFailAsync(() => _svc.CreateAsync(step), "The message filter does not belong to message 'Update'.");
    }

    [Fact]
    public async Task Filtering_attributes_must_exist()
    {
        var step = Step("Update", Pipe, 20);
        step["filteringattributes"] = "pb_name,pb_nope";
        await ShouldFailAsync(() => _svc.CreateAsync(step), $"Attribute 'pb_nope' not found on entity '{Pipe}'.");
    }

    [Theory]
    [InlineData("Create", 0)]
    [InlineData("Create", 2)]
    [InlineData("Delete", 1)]
    [InlineData("Delete", 2)]
    public async Task Image_type_must_be_valid_for_the_message(string message, int imageType)
    {
        var step = await _svc.CreateAsync(Step(message, Pipe, 40));

        await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, step, imageType, "img"),
            $"Image type {imageType} is not valid for message '{message}'.");
    }

    [Fact]
    public async Task Valid_images_are_accepted()
    {
        var create = await _svc.CreateAsync(Step("Create", Pipe, 40));
        var delete = await _svc.CreateAsync(Step("Delete", Pipe, 40));

        await PluginTestHelper.RegisterImageAsync(_svc, create, 1, "post");
        var image = await PluginTestHelper.RegisterImageAsync(_svc, delete, 0, "pre");
        await ShouldFailAsync(() => _svc.UpdateAsync(new Entity("sdkmessageprocessingstepimage", image) { ["imagetype"] = new OptionSetValue(1) }),
            "Image type 1 is not valid for message 'Delete'.");
    }

    // ---- #76, Task 1: steps and images on the bulk messages ----

    private async Task<string> ElasticTableAsync()
    {
        var name = "pb_regel" + Guid.NewGuid().ToString("N")[..6];
        var entity = TestHelpers.Entity(name, name, "Registration elastic");
        entity.TableType = ElasticTables.TableTypeElastic;
        await _svc.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200, DisplayName = TestHelpers.Label("Name") },
        });
        return name;
    }

    [Theory]
    [InlineData("CreateMultiple")]
    [InlineData("UpdateMultiple")]
    public async Task Bulk_step_with_a_standard_table_filter_is_accepted(string message)
    {
        await _svc.CreateAsync(Step(message, "account", 20));
        await _svc.CreateAsync(Step(message, "account", 40, mode: 1));
    }

    [Theory]
    [InlineData("CreateMultiple")]
    [InlineData("UpdateMultiple")]
    [InlineData("UpsertMultiple")]
    [InlineData("DeleteMultiple")]
    public async Task Bulk_step_without_a_filter_is_refused(string message) =>
        await ShouldFailAsync(() => _svc.CreateAsync(Step(message, null, 20)), $"Plug-in steps on '{message}' must name a table (sdkmessagefilterid).");

    [Theory]
    [InlineData("CreateMultiple")]
    [InlineData("UpdateMultiple")]
    [InlineData("UpsertMultiple")]
    [InlineData("DeleteMultiple")]
    public async Task Bulk_step_on_an_elastic_table_is_refused(string message)
    {
        var table = await ElasticTableAsync();
        await ShouldFailAsync(() => _svc.CreateAsync(Step(message, table, 20)),
            $"Plug-in steps on '{message}' are not supported for the elastic table '{table}': register on the single-record message.");
    }

    [Theory]
    [InlineData("Upsert")]
    [InlineData("UpsertMultiple")]
    [InlineData("DeleteMultiple")]
    public async Task Step_on_a_forged_filter_of_a_standard_table_is_refused(string message)
    {
        // Upsert is not an event on a standard table: a client that forges the filter row is refused.
        await TestSql.ExecAsync(fx.ConnectionString,
            "INSERT INTO data.sdkmessagefilter (sdkmessagefilterid, name, sdkmessageid, primaryobjecttypecode, secondaryobjecttypecode, " +
            "iscustomprocessingstepallowed, isvisible, availability, owningbusinessunit, createdon, modifiedon, statecode, statuscode) " +
            $"SELECT @id, @name, @msg, 'account', 'none', {TestSql.True}, {TestSql.True}, 1, " +
            "(SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL), SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1 " +
            "WHERE NOT EXISTS (SELECT 1 FROM data.sdkmessagefilter WHERE sdkmessagefilterid = @id)",
            ("@id", SdkMessageCatalog.FilterId(message, "account")), ("@name", message + ":account"),
            ("@msg", SdkMessageCatalog.MessageId(message)));
        try
        {
            await ShouldFailAsync(() => _svc.CreateAsync(Step(message, "account", 20)),
                $"'{message}' is not an event on the standard table 'account': register on Create or Update.");
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.sdkmessagefilter WHERE sdkmessagefilterid = @id",
                ("@id", SdkMessageCatalog.FilterId(message, "account")));
        }
    }

    [Fact]
    public async Task Upsert_step_without_a_filter_and_on_an_elastic_table_is_accepted()
    {
        var table = await ElasticTableAsync();
        await _svc.CreateAsync(Step("Upsert", null, 20));
        await _svc.CreateAsync(Step("Upsert", table, 20));
    }

    [Fact]
    public async Task CreateMultiple_refuses_a_pre_image_and_UpdateMultiple_a_post_image_before_stage_40()
    {
        var create = await _svc.CreateAsync(Step("CreateMultiple", "account", 40));
        foreach (var type in new[] { 0, 2 })
            await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, create, type, "img"),
                $"Image type {type} is not valid for message 'CreateMultiple'.");
        await PluginTestHelper.RegisterImageAsync(_svc, create, 1, "post");

        var update = await _svc.CreateAsync(Step("UpdateMultiple", "account", 20));
        await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, update, 1, "img"),
            "Image type 1 is not valid for message 'UpdateMultiple'.");
        await PluginTestHelper.RegisterImageAsync(_svc, update, 0, "pre");
        var update40 = await _svc.CreateAsync(Step("UpdateMultiple", "account", 40));
        await PluginTestHelper.RegisterImageAsync(_svc, update40, 2, "both");
    }

    [Fact]
    public async Task Images_on_asynchronous_and_webhook_bulk_steps_are_refused()
    {
        var text = "Images on asynchronous or webhook steps of 'UpdateMultiple' are not supported by xrmverse: the remote execution context has no image collections.";
        var async = await _svc.CreateAsync(Step("UpdateMultiple", "account", 40, mode: 1));
        await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, async, 0, "pre"), text);
        await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, async, 1, "post"), text);

        var endpoint = await _svc.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "regval hook", ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
            ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = "https://hooks.example.invalid/receive",
            [ServiceEndpointSchema.AuthValue] = "k",
        });
        var hook = Step("UpdateMultiple", "account", 40);
        hook.Attributes.Remove("plugintypeid");
        hook["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint);
        var webhook = await _svc.CreateAsync(hook);
        await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, webhook, 1, "post"), text);
    }

    [Fact]
    public async Task Step_on_an_unknown_filter_is_refused_by_the_reference_check()
    {
        var step = Step("CreateMultiple", null, 20);
        var id = Guid.NewGuid();
        step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", id);
        var fault = (await ((Func<Task>)(() => _svc.CreateAsync(step))).Should().ThrowAsync<XrmdFaultException>()).Which;
        // The core's existing reference check answers (404) before the validator runs; no new text is needed.
        fault.HttpStatus.Should().Be(404);
        fault.Message.Should().Be($"sdkmessagefilter With Id = {id} Does Not Exist");
    }

    [Fact]
    public async Task Images_on_an_asynchronous_CreateMultiple_step_are_refused()
    {
        var async = await _svc.CreateAsync(Step("CreateMultiple", "account", 40, mode: 1));
        await ShouldFailAsync(() => PluginTestHelper.RegisterImageAsync(_svc, async, 1, "post"),
            "Images on asynchronous or webhook steps of 'CreateMultiple' are not supported by xrmverse: the remote execution context has no image collections.");
    }
}
