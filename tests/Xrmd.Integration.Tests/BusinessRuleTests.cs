using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.WorkflowTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 5 (W15 as amended by Task 3b): an Entity scope business rule runs on the server at stage 20 of Create and Update.
/// The parity export's rule (<c>xrmv_order</c>: amount empty → status "New Action", otherwise → the error "fdsfds"; its
/// <c>ProcessTriggerScope</c> is 2 and it has no <c>processtrigger</c> rows) gives the shapes it has; the provisional
/// <c>business-rule-entity</c> (<c>contact</c>: job title empty → set it, default a column, clear a column, client-only steps;
/// otherwise → the error "Check the job title") gives the others, scoped through an Entity <c>processtrigger</c>. The seeded contact
/// has neither of the file's <c>preferredcontactmethodcode</c> and <c>fax</c>: the default goes to <c>mobilephone</c> (the text
/// "none") and the clear to <c>telephone1</c>.
/// </summary>
[Collection("xrmd")]
public sealed class BusinessRuleTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = TestEntities.Order;
    private const string Rule = "Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66";
    private const int NewAction = 128830001, New = 128830000;

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureOrderAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await ClearAsync(fx);
    }

    public Task DisposeAsync() => ClearAsync(fx);

    private string Name(string what) => $"{what} {_run}";

    /// <summary>The export's rule on <c>xrmv_order</c>, its <c>ProcessTriggerScope</c> as given (2 in the export).</summary>
    private Task<WorkflowRow> OrderRuleAsync(int processTriggerScope = S.EntityTriggerScope) =>
        ActivateAsync(fx, Admin, Realtime(Name("Order rule"), Real(Rule), Order, category: 2) with { ProcessTriggerScope = processTriggerScope });

    /// <summary>The provisional rule on <c>contact</c>, server-side through an Entity scope <c>processtrigger</c>.</summary>
    private Task<WorkflowRow> ContactRuleAsync() =>
        ActivateAsync(fx, Admin, Realtime(Name("Contact rule"), ContactRule(), "contact", category: 2),
            new Entity(S.TriggerTable)
            {
                [S.Event] = "save", [S.PrimaryEntityTypeCode] = "contact", [S.TriggerScope] = new OptionSetValue(S.EntityTriggerScope),
            });

    /// <summary>The provisional rule on the seeded contact's columns.</summary>
    private static string ContactRule()
    {
        var xaml = WorkflowFixtures.Xaml("business-rule-entity");
        xaml = Replace(xaml, "WorkflowPropertyType.OptionSetValue, \"1\", \"Picklist\"", "WorkflowPropertyType.String, \"none\", \"String\"");
        xaml = Replace(xaml, "preferredcontactmethodcode", "mobilephone");
        return Replace(xaml, "Attribute=\"fax\"", "Attribute=\"telephone1\"");
    }

    private async Task<Entity> RetrieveAsync(string entity, Guid id, params string[] columns) =>
        await Admin.RetrieveAsync(entity, id, new ColumnSet(columns));

    private async Task<int> CountAsync(string entity, string column, string value) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = { Conditions = { new ConditionExpression(column, ConditionOperator.Equal, value) } },
        })).Entities.Count;

    [Fact]
    public async Task Entity_scope_rule_sets_value_on_create_and_update()
    {
        await OrderRuleAsync();

        var order = await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Order") });
        (await RetrieveAsync(Order, order, "xrmv_status")).GetAttributeValue<OptionSetValue>("xrmv_status").Value.Should().Be(NewAction);

        // On Update, whatever columns change: the client's status is overwritten in the Target.
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_status"] = new OptionSetValue(New) });
        (await RetrieveAsync(Order, order, "xrmv_status")).GetAttributeValue<OptionSetValue>("xrmv_status").Value.Should().Be(NewAction);
    }

    [Fact]
    public async Task Default_value_only_when_empty_on_create()
    {
        await ContactRuleAsync();

        var defaulted = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = Name("Defaulted") });
        var given = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = Name("Given"), ["mobilephone"] = "555-0142" });

        var stored = await RetrieveAsync("contact", defaulted, "jobtitle", "mobilephone");
        stored.GetAttributeValue<string>("jobtitle").Should().Be("Unknown");
        stored.GetAttributeValue<string>("mobilephone").Should().Be("none");
        (await RetrieveAsync("contact", given, "mobilephone")).GetAttributeValue<string>("mobilephone")
            .Should().Be("555-0142", "a default value never replaces a value the client sent");

        // On Update the rule runs (the job title is set again) but a default value does not apply.
        await Admin.UpdateAsync(new Entity("contact", defaulted) { ["jobtitle"] = null, ["mobilephone"] = null });
        stored = await RetrieveAsync("contact", defaulted, "jobtitle", "mobilephone");
        stored.GetAttributeValue<string>("jobtitle").Should().Be("Unknown");
        stored.Contains("mobilephone").Should().BeFalse();
    }

    [Fact]
    public async Task Show_error_blocks_save_with_rule_text()
    {
        await OrderRuleAsync();

        // The export's rule shows its error when the amount has a value.
        var fault = await TestHelpers.FaultAsync(() => Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Blocked"), ["xrmv_amount"] = 5m }));

        (fault.HttpStatus, fault.ErrorCode, fault.Message).Should().Be((400, ErrorCodes.ISVAborted, "fdsfds"));
        (await CountAsync(Order, "xrmv_name", Name("Blocked"))).Should().Be(0);

        // The provisional rule on an Update: the stored job title (overlaid with the Target) is not empty.
        await ContactRuleAsync();
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = Name("Engineer") });
        fault = await TestHelpers.FaultAsync(() => Admin.UpdateAsync(new Entity("contact", contact) { ["jobtitle"] = "Engineer" }));
        (fault.HttpStatus, fault.ErrorCode, fault.Message).Should().Be((400, ErrorCodes.ISVAborted, "Check the job title"));
        (await RetrieveAsync("contact", contact, "jobtitle")).GetAttributeValue<string>("jobtitle").Should().Be("Unknown");
    }

    [Fact]
    public async Task Client_only_actions_do_nothing_server_side()
    {
        // The branch also sets a required level and a visibility (form only) and clears a column (server-side).
        await ContactRuleAsync();

        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = Name("Client only"), ["telephone1"] = "555-0199" });

        var stored = await RetrieveAsync("contact", contact, "jobtitle", "telephone1", "lastname");
        stored.GetAttributeValue<string>("jobtitle").Should().Be("Unknown");
        stored.Contains("telephone1").Should().BeFalse("the clear value step runs on the server");
        stored.GetAttributeValue<string>("lastname").Should().Be(Name("Client only"));
    }

    [Fact]
    public async Task Form_scope_rule_does_not_run()
    {
        // The export's rule with a Form scope and no Entity trigger: imported, activated, never run on the server (UI-1).
        await OrderRuleAsync(processTriggerScope: 1);

        var order = await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Form only"), ["xrmv_amount"] = 5m });

        (await RetrieveAsync(Order, order, "xrmv_status")).Contains("xrmv_status").Should().BeFalse();
    }
}
