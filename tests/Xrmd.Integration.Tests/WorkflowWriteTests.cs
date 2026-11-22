using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;
using static Xrmd.Integration.Tests.WorkflowTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.21, Task 5: public Create and Update of <c>workflow</c> are accepted when valid (A16, lifting 2.14's W3), and activating or
/// deactivating a definition demands the owner (or <c>prvActOnBehalfOfAnotherUser</c>) and the activation privilege of its kind (A17).
/// The definitions are the parity export's XAML (<c>tests/fixtures/workflows/real</c>) on <c>xrmv_order</c>.
/// </summary>
[Collection("xrmd")]
public sealed class WorkflowWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Order = TestEntities.Order;
    private const string OnCreate = "OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66";
    private const string OnUpdate = "OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66";
    private const string Rule = "Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66";
    private const string Recalculate = "Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66";
    private const string ActivateSynchronous = "prvActivateSynchronousWorkflow";
    private const string ActivateBusinessRule = "prvActivateBusinessRule";

    private static readonly (string, DepthMask)[] Author =
    [
        ("prvCreateWorkflow", DepthMask.Global), ("prvReadWorkflow", DepthMask.Global), ("prvWriteWorkflow", DepthMask.Global),
    ];

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureOrderAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await ActionWorkflowTests.ClearActionsAsync(fx);
    }

    public Task DisposeAsync() => ActionWorkflowTests.ClearActionsAsync(fx);

    // ---- definitions ------------------------------------------------------------------------------------------------------

    private string Name(string what) => $"{what} {_run}";

    /// <summary>The export's OnCreate, a real-time workflow on create of an order (post-operation).</summary>
    private Entity Realtime(string? name = null) => new(S.Table)
    {
        [S.Name] = name ?? Name("Realtime"), [S.TypeColumn] = new OptionSetValue(1), [S.CategoryColumn] = new OptionSetValue(0),
        [S.ModeColumn] = new OptionSetValue(1), [S.ScopeColumn] = new OptionSetValue(4), [S.PrimaryEntity] = Order,
        [S.TriggerOnCreate] = true, [S.CreateStage] = new OptionSetValue(40), [S.Xaml] = Real(OnCreate),
    };

    /// <summary>The export's OnUpdate, a background workflow with a wait.</summary>
    private Entity Background(string? name = null) => new(S.Table)
    {
        [S.Name] = name ?? Name("Background"), [S.CategoryColumn] = new OptionSetValue(0), [S.ModeColumn] = new OptionSetValue(0),
        [S.ScopeColumn] = new OptionSetValue(1), [S.PrimaryEntity] = Order, [S.TriggerOnUpdateAttributeList] = "xrmv_amount",
        [S.Xaml] = Real(OnUpdate),
    };

    /// <summary>The export's business rule (Entity scope).</summary>
    private Entity BusinessRule() => new(S.Table)
    {
        [S.Name] = Name("Rule"), [S.TypeColumn] = new OptionSetValue(1), [S.CategoryColumn] = new OptionSetValue(2),
        [S.ModeColumn] = new OptionSetValue(1), [S.ScopeColumn] = new OptionSetValue(4), [S.PrimaryEntity] = Order,
        [S.ProcessTriggerScope] = new OptionSetValue(S.EntityTriggerScope), [S.Xaml] = Real(Rule),
    };

    /// <summary>The export's Recalculate action, bound to the order, with a message name of this run.</summary>
    private Entity Action() => new(S.Table)
    {
        [S.Name] = Name("Recalculate"), [S.TypeColumn] = new OptionSetValue(1), [S.CategoryColumn] = new OptionSetValue(3),
        [S.ModeColumn] = new OptionSetValue(0), [S.ScopeColumn] = new OptionSetValue(4), [S.PrimaryEntity] = Order,
        [S.UniqueName] = $"new_Recalc{_run}", [S.Xaml] = Real(Recalculate),
    };

    private static Entity With(Entity e, string column, object? value)
    {
        e[column] = value;
        return e;
    }

    private Task<Entity> RetrieveAsync(Guid id) => Admin.RetrieveAsync(S.Table, id, new ColumnSet(true));

    private async Task<List<Entity>> ActivationsOfAsync(Guid definitionId) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression(S.ParentWorkflowId, ConditionOperator.Equal, definitionId) } },
        })).Entities.ToList();

    private async Task<int> StateOfAsync(Guid id) => (await RetrieveAsync(id)).GetAttributeValue<OptionSetValue>("statecode").Value;

    private static Task ActivateAsync(OrganizationService service, Guid id) => SetStateAsync(service, id, S.ActivatedState, S.ActivatedStatus);

    private async Task<(Guid User, OrganizationService Service)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var (user, service, _) = await UserWithObjectIdAsync(privileges);
        return (user, service);
    }

    private async Task<(Guid User, OrganizationService Service, Guid ObjectId)> UserWithObjectIdAsync(
        params (string Privilege, DepthMask Depth)[] privileges)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, privileges);
        return (user, ServiceFor(fx, objectId), objectId);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string json, Guid? objectId = null)
    {
        var req = new HttpRequestMessage(method, Api + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    // ---- A16: Create ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sdk_create_with_valid_xaml_gives_draft_definition()
    {
        var id = await Admin.CreateAsync(Realtime());

        var row = await RetrieveAsync(id);
        row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(S.DraftState);
        row.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(S.DraftStatus);
        row.GetAttributeValue<OptionSetValue>(S.TypeColumn).Value.Should().Be(1);
        row.GetAttributeValue<string>(S.Xaml).Should().Be(Real(OnCreate));
        row.GetAttributeValue<EntityReference>("ownerid").Id.Should().NotBeEmpty();
        (await ActivationsOfAsync(id)).Should().BeEmpty();

        // A definition without a type is a definition (type 1; Dataverse's default from memory, checklist 1a): the registry and the
        // activation find it.
        var untyped = Background();
        var untypedId = await Admin.CreateAsync(untyped);
        (await RetrieveAsync(untypedId)).GetAttributeValue<OptionSetValue>(S.TypeColumn).Value.Should().Be(1);
        await ActivateAsync(Admin, untypedId);
        (await ActivationsOfAsync(untypedId)).Should().ContainSingle();
        await DeactivateAsync(Admin, untypedId);
    }

    [Fact]
    public async Task Web_api_post_with_valid_xaml_gives_draft_definition()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = Name("Web"), ["type"] = 1, ["category"] = 0, ["mode"] = 1, ["scope"] = 4, ["primaryentity"] = Order,
            ["triggeroncreate"] = true, ["createstage"] = 40, ["xaml"] = Real(OnCreate),
        });

        var post = await SendAsync(HttpMethod.Post, "workflows", body);

        post.StatusCode.Should().Be(HttpStatusCode.NoContent, await post.Content.ReadAsStringAsync());
        var id = Guid.Parse(post.Headers.GetValues("OData-EntityId").Single().Split('(', ')')[1]);
        var row = await RetrieveAsync(id);
        row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(S.DraftState);
        row.GetAttributeValue<string>(S.Name).Should().Be(Name("Web"));
    }

    [Fact]
    public async Task Create_activated_is_refused()
    {
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), "statecode", new OptionSetValue(S.ActivatedState)))))
            .Should().Contain("Draft");
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), "statuscode", new OptionSetValue(S.ActivatedStatus)))))
            .Should().Contain("Draft");
    }

    [Fact]
    public async Task Create_type_2_is_refused()
    {
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.TypeColumn, new OptionSetValue(2))))).Should().Contain("type");
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.TypeColumn, new OptionSetValue(3))))).Should().Contain("type");
    }

    [Fact]
    public async Task Create_category_5_is_refused()
    {
        foreach (var category in new[] { 1, 4, 5, 6 })
            (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.CategoryColumn, new OptionSetValue(category)))))
                .Should().Contain($"category {category}");
        var without = Realtime();
        without.Attributes.Remove(S.CategoryColumn);
        (await BadRequestAsync(() => Admin.CreateAsync(without))).Should().Contain("category");
    }

    [Fact]
    public async Task Create_with_unsupported_steps_is_refused_listing_them()
    {
        var e = new Entity(S.Table)
        {
            [S.Name] = Name("Unsupported"), [S.CategoryColumn] = new OptionSetValue(0), [S.ModeColumn] = new OptionSetValue(0),
            [S.PrimaryEntity] = "account", [S.TriggerOnCreate] = true, [S.Xaml] = WorkflowFixtures.Xaml("unsupported-expression"),
        };

        var message = await BadRequestAsync(() => Admin.CreateAsync(e));

        message.Should().Contain("0/0").And.Contain("0/1").And.Contain("0/2");
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            Criteria = { Conditions = { new ConditionExpression(S.Name, ConditionOperator.Equal, Name("Unsupported")) } },
        })).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_with_malformed_xaml_is_refused_with_position()
    {
        var message = await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.Xaml, "<Activity>\n  <Sequence>\n</Activity>")));
        message.Should().Contain("not well-formed").And.Contain("line 3").And.Contain("position");

        // No XAML at all is refused too.
        var none = Realtime();
        none.Attributes.Remove(S.Xaml);
        (await BadRequestAsync(() => Admin.CreateAsync(none))).Should().Contain("XAML");
    }

    [Fact]
    public async Task Create_with_wait_in_realtime_is_refused()
    {
        var message = await BadRequestAsync(() => Admin.CreateAsync(With(Background(), S.ModeColumn, new OptionSetValue(1))));

        message.Should().Contain("wait").And.Contain("real-time");
    }

    [Fact]
    public async Task Unsupportedsteps_column_is_refused_on_create_and_update()
    {
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.UnsupportedStepsColumn, "none"))))
            .Should().Contain(S.UnsupportedStepsColumn);
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.UnsupportedStepsColumn, null))))
            .Should().Contain(S.UnsupportedStepsColumn);

        var id = await Admin.CreateAsync(Realtime());
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.UnsupportedStepsColumn] = "x" })))
            .Should().Contain(S.UnsupportedStepsColumn);
        (await RetrieveAsync(id)).GetAttributeValue<string>(S.UnsupportedStepsColumn).Should().BeNull();
    }

    [Fact]
    public async Task Activeworkflowid_and_sdkmessageid_are_refused()
    {
        var other = await Admin.CreateAsync(Realtime(Name("Other")));
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.ActiveWorkflowId, new EntityReference(S.Table, other)))))
            .Should().Contain(S.ActiveWorkflowId);
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), S.ParentWorkflowId, new EntityReference(S.Table, other)))))
            .Should().Contain(S.ParentWorkflowId);
        (await BadRequestAsync(() => Admin.CreateAsync(With(Action(), S.SdkMessageId, new EntityReference("sdkmessage", Guid.NewGuid())))))
            .Should().Contain(S.SdkMessageId);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, other) { [S.ActiveWorkflowId] = new EntityReference(S.Table, other) })))
            .Should().Contain(S.ActiveWorkflowId);
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), "ismanaged", true)))).Should().Contain("ismanaged");
        (await BadRequestAsync(() => Admin.CreateAsync(With(Realtime(), SystemTableRules.ComponentXml, "<Workflow/>"))))
            .Should().Contain(SystemTableRules.ComponentXml);
    }

    // ---- A16 through the bulk messages and Upsert (final review) ------------------------------------------------------------

    /// <summary>The Create targets the A16 checks refuse, each with a text of its refusal: malformed XAML, an unsupported step, an Activated row.</summary>
    private IEnumerable<(string What, Entity Target, string Says)> RefusedCreates() =>
    [
        ("malformed XAML", With(Realtime(Name("Malformed")), S.Xaml, "<Activity>\n  <Sequence>\n</Activity>"), "not well-formed"),
        ("unsupported steps", new Entity(S.Table)
        {
            [S.Name] = Name("Unsupported"), [S.CategoryColumn] = new OptionSetValue(0), [S.ModeColumn] = new OptionSetValue(0),
            [S.PrimaryEntity] = "account", [S.TriggerOnCreate] = true, [S.Xaml] = WorkflowFixtures.Xaml("unsupported-expression"),
        }, "0/0"),
        ("an Activated row", With(Realtime(Name("Activated")), "statecode", new OptionSetValue(S.ActivatedState)), "Draft"),
    ];

    /// <summary>The definitions of this test's run (their names end with its run tag).</summary>
    private async Task<int> RowsOfRunAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT COUNT(*) FROM data.workflow WHERE name LIKE @run", conn);
        cmd.Parameters.AddWithValue("@run", "%" + _run);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static EntityCollection Workflows(params Entity[] targets) => new(targets.ToList()) { EntityName = S.Table };

    [Fact]
    public async Task Create_multiple_refuses_each_invalid_definition_and_writes_nothing()
    {
        foreach (var (what, bad, says) in RefusedCreates())
        {
            var good = Realtime(Name("Bulk good"));
            good.Id = Guid.NewGuid();

            var message = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Workflows(good, bad) }));

            message.Should().Contain(says, "CreateMultiple refuses {0}", what);
            (await RowsOfRunAsync()).Should().Be(0, "the valid target of the refused CreateMultiple ({0}) is rolled back", what);
        }
    }

    [Fact]
    public async Task Update_multiple_refuses_each_invalid_definition_and_writes_nothing()
    {
        var first = await Admin.CreateAsync(Realtime(Name("First")));
        var second = await Admin.CreateAsync(Realtime(Name("Second")));
        var activated = await Admin.CreateAsync(Realtime(Name("Activated")));
        await ActivateAsync(Admin, activated);
        var refused = new (string What, Entity Target, string? Says)[]
        {
            ("malformed XAML", new Entity(S.Table, second) { [S.Xaml] = "<Activity>\n  <Sequence>\n</Activity>" }, "not well-formed"),
            ("unsupported steps", new Entity(S.Table, second) { [S.Xaml] = WorkflowFixtures.Xaml("unsupported-expression") }, null),
            ("an Activated row", new Entity(S.Table, activated) { [S.Name] = Name("Renamed activated") }, "Draft"),
        };

        foreach (var (what, bad, says) in refused)
        {
            var rename = new Entity(S.Table, first) { [S.Name] = Name("Renamed first") };

            var message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateMultipleRequest { Targets = Workflows(rename, bad) }));

            if (says is not null) message.Should().Contain(says, "UpdateMultiple refuses {0}", what);
            (await RetrieveAsync(first)).GetAttributeValue<string>(S.Name).Should().Be(Name("First"), "the rename is rolled back ({0})", what);
            (await RetrieveAsync(second)).GetAttributeValue<string>(S.Xaml).Should().Be(Real(OnCreate), "{0} is not written", what);
            (await RetrieveAsync(activated)).GetAttributeValue<string>(S.Name).Should().Be(Name("Activated"), "{0} is not written", what);
        }
    }

    [Fact]
    public async Task Upsert_refuses_each_invalid_definition_and_writes_nothing()
    {
        // Upsert of a new id runs an inner Create; UpsertMultiple one per target.
        foreach (var (what, bad, says) in RefusedCreates())
        {
            bad.Id = Guid.NewGuid();
            (await BadRequestAsync(() => Admin.ExecuteAsync(new UpsertRequest { Target = bad }))).Should().Contain(says, "Upsert refuses {0}", what);
            var good = Realtime(Name("Upsert good"));
            good.Id = Guid.NewGuid();
            (await BadRequestAsync(() => Admin.ExecuteAsync(new UpsertMultipleRequest { Targets = Workflows(good, bad) })))
                .Should().Contain(says, "UpsertMultiple refuses {0}", what);
            (await RowsOfRunAsync()).Should().Be(0, "nothing of the refused Upserts ({0}) is written", what);
        }

        // Upsert of an existing id runs an inner Update.
        var draft = await Admin.CreateAsync(Realtime(Name("Draft")));
        var activated = await Admin.CreateAsync(Realtime(Name("Activated")));
        await ActivateAsync(Admin, activated);
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(S.Table, draft) { [S.Xaml] = "<Activity>\n  <Sequence>\n</Activity>" },
        }))).Should().Contain("not well-formed");
        await BadRequestAsync(() => Admin.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(S.Table, draft) { [S.Xaml] = WorkflowFixtures.Xaml("unsupported-expression") },
        }));
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(S.Table, activated) { [S.Name] = Name("Renamed activated") },
        }))).Should().Contain("Draft");
        (await RetrieveAsync(draft)).GetAttributeValue<string>(S.Xaml).Should().Be(Real(OnCreate));
        (await RetrieveAsync(activated)).GetAttributeValue<string>(S.Name).Should().Be(Name("Activated"));
    }

    // ---- A16: Update ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_of_activated_definition_is_refused()
    {
        var id = await Admin.CreateAsync(Realtime());
        await ActivateAsync(Admin, id);

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.Name] = Name("Renamed") }))).Should().Contain("Draft");

        (await RetrieveAsync(id)).GetAttributeValue<string>(S.Name).Should().Be(Name("Realtime"));
        // Its owner is still changed, and its activation follows the owner (as after an Assign); then its state (SetState).
        var (user, _) = await UserAsync(("prvReadWorkflow", DepthMask.Basic));
        await Admin.UpdateAsync(new Entity(S.Table, id) { ["ownerid"] = new EntityReference("systemuser", user) });
        (await RetrieveAsync(id)).GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
        (await ActivationsOfAsync(id)).Should().ContainSingle().Which.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
        await DeactivateAsync(Admin, id);
        await Admin.UpdateAsync(new Entity(S.Table, id) { [S.Name] = Name("Renamed") });
        (await RetrieveAsync(id)).GetAttributeValue<string>(S.Name).Should().Be(Name("Renamed"));
    }

    [Fact]
    public async Task Update_of_draft_definition_is_validated()
    {
        var id = await Admin.CreateAsync(Background());

        await Admin.UpdateAsync(new Entity(S.Table, id) { [S.Name] = Name("Renamed"), [S.Description] = "Edited" });
        (await RetrieveAsync(id)).GetAttributeValue<string>(S.Description).Should().Be("Edited");

        // The merged row is checked: real-time with the stored XAML's wait.
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.ModeColumn] = new OptionSetValue(1) })))
            .Should().Contain("wait");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.Xaml] = "<Activity" }))).Should().Contain("not well-formed");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.Xaml] = WorkflowFixtures.Xaml("unsupported-expression") })))
            .Should().Contain("0/0");
        // A state change goes alone (SetState): with content it would activate the stored XAML, not the new one.
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id)
        {
            ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2), [S.Description] = "And activate",
        }))).Should().Contain("SetState");

        var row = await RetrieveAsync(id);
        row.GetAttributeValue<OptionSetValue>(S.ModeColumn).Value.Should().Be(0);
        row.GetAttributeValue<string>(S.Xaml).Should().Be(Real(OnUpdate));
        row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);

        // The XAML of a valid process replaces it.
        await Admin.UpdateAsync(new Entity(S.Table, id) { [S.Xaml] = Real(OnCreate) });
        (await RetrieveAsync(id)).GetAttributeValue<string>(S.Xaml).Should().Be(Real(OnCreate));
    }

    [Fact]
    public async Task Category_entity_and_uniquename_cannot_change()
    {
        var id = await Admin.CreateAsync(Realtime());
        var action = await Admin.CreateAsync(Action());

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.CategoryColumn] = new OptionSetValue(2) })))
            .Should().Contain(S.CategoryColumn);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.PrimaryEntity] = "account" })))
            .Should().Contain(S.PrimaryEntity);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.TypeColumn] = new OptionSetValue(2) })))
            .Should().Contain(S.TypeColumn);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, action) { [S.UniqueName] = $"new_Other{_run}" })))
            .Should().Contain(S.UniqueName);

        // The same values are no change.
        await Admin.UpdateAsync(new Entity(S.Table, id) { [S.CategoryColumn] = new OptionSetValue(0), [S.PrimaryEntity] = Order });
        await Admin.UpdateAsync(new Entity(S.Table, action) { [S.UniqueName] = $"new_Recalc{_run}" });
        (await RetrieveAsync(action)).GetAttributeValue<string>(S.UniqueName).Should().Be($"new_Recalc{_run}");
    }

    // ---- R9: an Update re-validates only what it touches; fix round 1 -----------------------------------------------------

    [Fact]
    public async Task Imported_draft_with_unsupported_steps_can_be_renamed()
    {
        var row = await SaveAsync(fx, WorkflowFixtures.Definition("unsupported-expression", Name("Imported")));

        await Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.Name] = Name("Renamed"), [S.Description] = "Edited" });

        (await RetrieveAsync(row.Id)).GetAttributeValue<string>(S.Name).Should().Be(Name("Renamed"));
        // Touching a column the parser reads validates the whole row again.
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.Xaml] = row.Xaml }))).Should().Contain("0/0");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.TriggerOnDelete] = true }))).Should().Contain("0/0");
        // And SetState still validates before anything runs: it activates, listing the steps (2.14, W4), and they fail when triggered.
    }

    [Fact]
    public async Task Imported_category_5_draft_can_be_renamed()
    {
        var row = await SaveAsync(fx, WorkflowFixtures.Definition("realtime-create-update", Name("Modern flow"), category: 5) with { Xaml = null });

        await Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.Name] = Name("Renamed flow") });

        (await RetrieveAsync(row.Id)).GetAttributeValue<string>(S.Name).Should().Be(Name("Renamed flow"));
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.Xaml] = Real(OnCreate) }))).Should().Contain("category 5");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.CategoryColumn] = new OptionSetValue(0) })))
            .Should().Contain(S.CategoryColumn);
        (await RetrieveAsync(row.Id)).GetAttributeValue<string>(S.Xaml).Should().BeNull();
    }

    [Fact]
    public async Task Update_naming_the_row_by_its_workflowid_attribute_is_checked()
    {
        var id = await Admin.CreateAsync(Background());

        await Admin.UpdateAsync(new Entity(S.Table) { [S.WorkflowId] = id, [S.Description] = "By attribute" });
        (await RetrieveAsync(id)).GetAttributeValue<string>(S.Description).Should().Be("By attribute");

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table) { [S.WorkflowId] = id, [S.ModeColumn] = new OptionSetValue(1) })))
            .Should().Contain("wait");
        await ActivateAsync(Admin, id);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table) { [S.WorkflowId] = id, [S.Description] = "Activated" })))
            .Should().Contain("Draft");
        await DeactivateAsync(Admin, id);
        (await RetrieveAsync(id)).GetAttributeValue<OptionSetValue>(S.ModeColumn).Value.Should().Be(0);
    }

    [Fact]
    public async Task Pre_operation_plugin_adding_layer_columns_is_refused()
    {
        var id = await Admin.CreateAsync(Background());
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", S.Table, 20, config: $"{SystemTableRules.ComponentXml}=<Workflow/>");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", S.Table, 20, config: "ismanaged=true");

            (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.Description] = "Layered" })))
                .Should().Contain(SystemTableRules.ComponentXml);
            (await BadRequestAsync(() => Admin.CreateAsync(Realtime(Name("Managed"))))).Should().Contain("ismanaged");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }

        (await RetrieveAsync(id)).GetAttributeValue<string>(S.Description).Should().BeNull();
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            Criteria = { Conditions = { new ConditionExpression(S.Name, ConditionOperator.Equal, Name("Managed")) } },
        })).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Pre_operation_plugin_that_widens_target_is_rechecked()
    {
        var id = await Admin.CreateAsync(Background());
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetTypedAttributePlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", S.Table, 20, config: $"{S.ModeColumn}=option:1");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", S.Table, 20, config: $"{S.TypeColumn}=option:2");

            (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, id) { [S.Name] = Name("Renamed") }))).Should().Contain("wait");
            (await BadRequestAsync(() => Admin.CreateAsync(Realtime(Name("Widened"))))).Should().Contain("type");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }

        var row = await RetrieveAsync(id);
        row.GetAttributeValue<string>(S.Name).Should().Be(Name("Background"));
        row.GetAttributeValue<OptionSetValue>(S.ModeColumn).Value.Should().Be(0);
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            Criteria = { Conditions = { new ConditionExpression(S.Name, ConditionOperator.Equal, Name("Widened")) } },
        })).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Processtrigger_create_is_still_refused()
    {
        var id = await Admin.CreateAsync(Realtime());

        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(S.TriggerTable)
        {
            [S.Event] = "change", [S.ProcessId] = new EntityReference(S.Table, id),
        }))).Should().Contain(S.TriggerTable);
        var post = await SendAsync(HttpMethod.Post, "processtriggers", """{"event":"load"}""");
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- A17: Create and Update privileges ----------------------------------------------------------------------------------

    [Fact]
    public async Task Create_needs_prvCreateWorkflow()
    {
        var (_, reader) = await UserAsync(("prvReadWorkflow", DepthMask.Global), ("prvWriteWorkflow", DepthMask.Global));

        var fault = await FaultAsync(() => reader.CreateAsync(Realtime()));

        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvCreateWorkflow");

        var (user, author) = await UserAsync(Author);
        var id = await author.CreateAsync(Realtime(Name("Authored")));
        (await RetrieveAsync(id)).GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
    }

    [Fact]
    public async Task Update_needs_prvWriteWorkflow_at_record_depth()
    {
        var (_, author) = await UserAsync(
            ("prvCreateWorkflow", DepthMask.Basic), ("prvReadWorkflow", DepthMask.Global), ("prvWriteWorkflow", DepthMask.Basic));
        var own = await author.CreateAsync(Realtime(Name("Own")));
        var others = await Admin.CreateAsync(Realtime(Name("Others")));

        await author.UpdateAsync(new Entity(S.Table, own) { [S.Description] = "Mine" });
        var fault = await FaultAsync(() => author.UpdateAsync(new Entity(S.Table, others) { [S.Description] = "Not mine" }));

        fault.HttpStatus.Should().Be(403);
        (await RetrieveAsync(own)).GetAttributeValue<string>(S.Description).Should().Be("Mine");
        (await RetrieveAsync(others)).GetAttributeValue<string>(S.Description).Should().BeNull();
    }

    // ---- A17: activation privileges -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Activate_realtime_without_privilege_is_403()
    {
        var (user, author, objectId) = await UserWithObjectIdAsync(Author);
        var id = await author.CreateAsync(Realtime());

        var fault = await FaultAsync(() => ActivateAsync(author, id));

        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain(ActivateSynchronous);
        // The Web API's PATCH of the state is the same SetState (fix round 1, minor 4).
        var patch = await SendAsync(HttpMethod.Patch, $"workflows({id})", """{"statecode":1,"statuscode":2}""", objectId);
        patch.StatusCode.Should().Be(HttpStatusCode.Forbidden, await patch.Content.ReadAsStringAsync());
        (await patch.Content.ReadAsStringAsync()).Should().Contain(ActivateSynchronous);
        (await StateOfAsync(id)).Should().Be(S.DraftState);
        (await ActivationsOfAsync(id)).Should().BeEmpty();

        await GrantAsync(fx, user, team: false, (ActivateSynchronous, DepthMask.Global));
        await ActivateAsync(author, id);
        (await ActivationsOfAsync(id)).Should().ContainSingle();
        await DeactivateAsync(author, id);
    }

    [Fact]
    public async Task Activate_action_without_privilege_is_403()
    {
        var (user, author) = await UserAsync(Author);
        var id = await author.CreateAsync(Action());

        var fault = await FaultAsync(() => ActivateAsync(author, id));

        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain(ActivateSynchronous);
        (await StateOfAsync(id)).Should().Be(S.DraftState);
        (await RetrieveAsync(id)).GetAttributeValue<EntityReference>(S.SdkMessageId).Should().BeNull();

        await GrantAsync(fx, user, team: false, (ActivateSynchronous, DepthMask.Global));
        await ActivateAsync(author, id);
        (await StateOfAsync(id)).Should().Be(S.ActivatedState);
    }

    [Fact]
    public async Task Activate_business_rule_needs_prvActivateBusinessRule()
    {
        var (user, author) = await UserAsync([.. Author, (ActivateSynchronous, DepthMask.Global)]);
        var id = await author.CreateAsync(BusinessRule());

        var fault = await FaultAsync(() => ActivateAsync(author, id));

        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain(ActivateBusinessRule);
        (await StateOfAsync(id)).Should().Be(S.DraftState);

        await GrantAsync(fx, user, team: false, (ActivateBusinessRule, DepthMask.Global));
        await ActivateAsync(author, id);
        (await StateOfAsync(id)).Should().Be(S.ActivatedState);
        await DeactivateAsync(author, id);
        (await StateOfAsync(id)).Should().Be(S.DraftState);
    }

    [Fact]
    public async Task Activate_background_needs_no_extra_privilege()
    {
        var (_, author) = await UserAsync(Author);
        var id = await author.CreateAsync(Background());

        await ActivateAsync(author, id);

        (await StateOfAsync(id)).Should().Be(S.ActivatedState);
        (await ActivationsOfAsync(id)).Should().ContainSingle();
        await DeactivateAsync(author, id);
        (await StateOfAsync(id)).Should().Be(S.DraftState);
    }

    [Fact]
    public async Task Non_owner_cannot_deactivate()
    {
        var id = await Admin.CreateAsync(Realtime());
        await ActivateAsync(Admin, id);
        var (_, other) = await UserAsync([.. Author, (ActivateSynchronous, DepthMask.Global)]);

        var fault = await FaultAsync(() => DeactivateAsync(other, id));

        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain(PrivilegeNames.ActOnBehalfOfAnotherUser);
        (await StateOfAsync(id)).Should().Be(S.ActivatedState);
        (await ActivationsOfAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Act_on_behalf_privilege_lets_non_owner_activate()
    {
        var id = await Admin.CreateAsync(Realtime());
        var (user, other) = await UserAsync([.. Author, (ActivateSynchronous, DepthMask.Global)]);
        (await FaultAsync(() => ActivateAsync(other, id))).HttpStatus.Should().Be(403);

        await GrantAsync(fx, user, team: false, (PrivilegeNames.ActOnBehalfOfAnotherUser, DepthMask.Global));
        await ActivateAsync(other, id);

        (await StateOfAsync(id)).Should().Be(S.ActivatedState);
        await DeactivateAsync(other, id);
        (await StateOfAsync(id)).Should().Be(S.DraftState);
    }

    [Fact]
    public async Task Solution_import_activation_is_unchanged()
    {
        // An imported real-time process owned by a user who holds none of the activation privileges: the import (Caller.Internal)
        // activates it as in 2.14.
        var (user, _) = await UserAsync(("prvReadWorkflow", DepthMask.Basic));
        var row = await SaveAsync(fx, WorkflowTestHelper.Realtime(Name("Imported"), Real(OnCreate), Order) with
        {
            TriggerOnCreate = true, CreateStage = 40, OwnerId = new EntityReference("systemuser", user),
        });

        await using (var scope = fx.App.Services.CreateAsyncScope())
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
                        await scope.ServiceProvider.GetRequiredService<WorkflowActivation>()
                .ActivateAsync(new DataSession(conn, null), Caller.Internal, row.Id, default);
        }

        (await StateOfAsync(row.Id)).Should().Be(S.ActivatedState);
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle();
    }

    // ---- A17: the standard roles -------------------------------------------------------------------------------------------

    private async Task<Dictionary<string, int>> PrivilegesOfAsync(Guid role)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("""
            SELECT p.name, rp.privilegedepthmask FROM data.roleprivileges AS rp JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            WHERE rp.roleid = @role
            """, conn);
        cmd.Parameters.AddWithValue("@role", role);
        var privileges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) privileges.Add(rd.GetString(0), rd.GetInt32(1));
        return privileges;
    }

    [Fact]
    public async Task Customizer_holds_activation_privileges()
    {
        PrivilegeNames.ActivateSynchronousWorkflow.Should().Be(ActivateSynchronous);
        PrivilegeNames.ActivateBusinessRule.Should().Be(ActivateBusinessRule);

        var customizer = await PrivilegesOfAsync(StandardRoles.SystemCustomizer);

        customizer.Should().ContainKey(ActivateSynchronous).WhoseValue.Should().Be((int)DepthMask.Global);
        customizer.Should().ContainKey(ActivateBusinessRule).WhoseValue.Should().Be((int)DepthMask.Global);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT COUNT(*) FROM data.privilege WHERE privilegeid IN (@a, @b)", conn);
        cmd.Parameters.AddWithValue("@a", PrivilegeNames.IdOf(ActivateSynchronous));
        cmd.Parameters.AddWithValue("@b", PrivilegeNames.IdOf(ActivateBusinessRule));
        (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(2);
        await using var step = TestSql.Command("SELECT COUNT(*) FROM meta.upgrade_step WHERE name = 'workflow-activation-role-privileges'", conn);
        (Convert.ToInt32(await step.ExecuteScalarAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Basic_user_does_not()
    {
        var basic = await PrivilegesOfAsync(StandardRoles.BasicUser);

        basic.Should().NotContainKey(ActivateSynchronous);
        basic.Should().NotContainKey(ActivateBusinessRule);
    }
}
