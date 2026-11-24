using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.15, task 3: the duplicate check of Create and Update (rulings D1, D2, D12), asked for with the optional parameter
/// <c>SuppressDuplicateDetection = false</c>, on a published rule contact → contact on <c>emailaddress1</c> (Exact Match).
/// </summary>
[Collection("xrmd")]
public sealed class DuplicateCheckTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Contact = DuplicateDetectionTestKit.Contact;
    private const string Email = DuplicateDetectionTestKit.Email;
    private const string Suppress = "SuppressDuplicateDetection";

    /// <summary>The duplicate error, <c>0x80040333</c> (Learn).</summary>
    private const int DuplicatesFound = unchecked((int)0x80040333);

    private readonly DuplicateDetectionTestKit _kit = new(fx);

    private OrganizationService Admin => _kit.Admin;

    public Task InitializeAsync() => _kit.InitializeAsync(onlineCreateUpdate: true, Contact, "account");

    public Task DisposeAsync() => _kit.DisposeAsync();

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private string Mail(string label) => _kit.Mail(label);

    private Entity NewContact(string label, string? email, Guid? id = null)
    {
        var contact = new Entity(Contact) { ["lastname"] = label + " " + _kit.Run, [Email] = email };
        if (id is { } given) contact.Id = given;
        return contact;
    }

    /// <summary>A contact created without the check (the default).</summary>
    private Task<Guid> ContactAsync(string label, string? email) => Admin.CreateAsync(NewContact(label, email));

    /// <summary>A Create through <c>CreateRequest</c>, with <c>SuppressDuplicateDetection</c> when <paramref name="suppress"/> is given.</summary>
    private async Task<Guid> CreateAsync(Entity entity, object? suppress)
    {
        var request = new CreateRequest { Target = entity };
        if (suppress is not null) request[Suppress] = suppress;
        return (Guid)(await Admin.ExecuteAsync(request)).Results["id"];
    }

    private Task UpdateAsync(Entity entity, object? suppress)
    {
        var request = new UpdateRequest { Target = entity };
        if (suppress is not null) request[Suppress] = suppress;
        return Admin.ExecuteAsync(request);
    }

    /// <summary>The published rule contact → contact (or <paramref name="table"/> → <paramref name="matchingTable"/>) on the email.</summary>
    private async Task<Guid> PublishedRuleAsync(bool caseSensitive = false, bool excludeInactive = false, string table = Contact,
        string? matchingTable = null)
    {
        var rule = await _kit.RuleAsync(Email, table, excludeInactive, caseSensitive: caseSensitive, matchingTable: matchingTable);
        await _kit.PublishAndRunAsync(rule);
        return rule;
    }

    /// <summary>A custom table with duplicate detection on and a decimal column <c>new_amount</c> of precision 2.</summary>
    private async Task<string> DecimalTableAsync()
    {
        var name = "new_dec" + _kit.Run;
        var entity = TestHelpers.Entity(name, name, "Table " + name);
        entity.IsDuplicateDetectionEnabled = new BooleanManagedProperty(true);
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new DecimalAttributeMetadata
            {
                LogicalName = "new_amount", SchemaName = "new_Amount", Precision = 2, MinValue = -1000m, MaxValue = 1000m, DisplayName = Label("Amount"),
            },
        });
        return name;
    }

    private async Task DeleteTableAsync(string name)
    {
        foreach (var rule in _kit.Rules.ToList())
            if (await _kit.ScalarAsync("SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid = @id AND baseentityname = @name",
                    ("@id", rule), ("@name", name)) is 1 or 1L)
                await Admin.DeleteAsync(DuplicateDetectionSchema.Rule, rule);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
    }

    private static void ShouldBeDuplicate(XrmdFaultException fault)
    {
        fault.ErrorCode.Should().Be(DuplicatesFound, fault.ToString());
        fault.HttpStatus.Should().Be(412);
    }

    private async Task<int> ContactsWithAsync(string email) =>
        Convert.ToInt32(await _kit.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE emailaddress1 = @e", ("@e", email)));

    // ---- Create -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_with_suppress_false_and_existing_email_fails()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        ShouldBeDuplicate(await FaultAsync(() => CreateAsync(NewContact("Second", Mail("anna")), false)));

        (await ContactsWithAsync(Mail("anna"))).Should().Be(1, "the refused Create inserted nothing");
    }

    [Fact]
    public async Task Upsert_forwards_suppress_false_to_its_inner_create_and_update()
    {
        // #76: an SDK Upsert on a standard table carries SuppressDuplicateDetection over to the Create or Update it runs.
        await ContactAsync("Anna", Mail("anna"));
        var bob = await ContactAsync("Bob", Mail("bob"));
        await PublishedRuleAsync();

        async Task UpsertAsync(Entity target, object? suppress)
        {
            var request = new UpsertRequest { Target = target };
            if (suppress is not null) request[Suppress] = suppress;
            await Admin.ExecuteAsync(request);
        }

        ShouldBeDuplicate(await FaultAsync(() => UpsertAsync(NewContact("Second", Mail("anna"), Guid.NewGuid()), false)));
        ShouldBeDuplicate(await FaultAsync(() => UpsertAsync(new Entity(Contact, bob) { [Email] = Mail("anna") }, false)));
        (await ContactsWithAsync(Mail("anna"))).Should().Be(1, "both refused Upserts wrote nothing");

        // Without the parameter the check is suppressed, as for a direct Create or Update.
        await UpsertAsync(new Entity(Contact, bob) { [Email] = Mail("anna") }, null);
        (await ContactsWithAsync(Mail("anna"))).Should().Be(2);
    }

    [Fact]
    public async Task Create_with_suppress_true_succeeds()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        await CreateAsync(NewContact("Second", Mail("anna")), true);

        (await ContactsWithAsync(Mail("anna"))).Should().Be(2);
    }

    [Fact]
    public async Task Create_without_parameter_succeeds()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        await CreateAsync(NewContact("Second", Mail("anna")), null);
        await Admin.CreateAsync(NewContact("Third", Mail("anna")));

        (await ContactsWithAsync(Mail("anna"))).Should().Be(3, "an absent parameter suppresses the check");
    }

    [Fact]
    public async Task Create_with_online_switch_off_succeeds()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();
        await _kit.SetOrganizationAsync(true, onlineCreateUpdate: false);

        await CreateAsync(NewContact("Second", Mail("anna")), false);

        (await ContactsWithAsync(Mail("anna"))).Should().Be(2);
    }

    [Fact]
    public async Task Create_with_table_flag_off_succeeds()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();
        // Turning the flag off through metadata also unpublishes the rule (D11); flip the definition alone to test the flag itself.
        var flag = _kit.Store.GetTable(Contact)!.IsDuplicateDetectionEnabled;
        InMemoryMetadata.Change(_kit.Store, Contact, t => t.IsDuplicateDetectionEnabled = new Xrmd.Model.Metadata.ManagedFlag { Value = false, CanBeChanged = true });
        try
        {
            await CreateAsync(NewContact("Second", Mail("anna")), false);
        }
        finally
        {
            InMemoryMetadata.Change(_kit.Store, Contact, t => t.IsDuplicateDetectionEnabled = flag);
        }

        (await ContactsWithAsync(Mail("anna"))).Should().Be(2);
    }

    [Fact]
    public async Task Unpublished_rule_does_not_check()
    {
        await ContactAsync("Anna", Mail("anna"));
        await _kit.RuleAsync();

        await CreateAsync(NewContact("Unpublished", Mail("anna")), false);

        var publishing = await _kit.RuleAsync();
        await _kit.PublishAsync(publishing); // the job does not run: the rule stays Publishing
        await CreateAsync(NewContact("Publishing", Mail("anna")), false);

        (await ContactsWithAsync(Mail("anna"))).Should().Be(3, "only a Published rule checks");
    }

    [Fact]
    public async Task Case_insensitive_by_default()
    {
        await ContactAsync("Anna", Mail("anna").ToUpperInvariant());
        await PublishedRuleAsync();

        ShouldBeDuplicate(await FaultAsync(() => CreateAsync(NewContact("Lower", Mail("anna")), false)));
    }

    [Fact]
    public async Task Case_sensitive_rule_lets_case_variant_through()
    {
        await ContactAsync("Anna", Mail("anna").ToUpperInvariant());
        await PublishedRuleAsync(caseSensitive: true);

        await CreateAsync(NewContact("Lower", Mail("anna")), false);
        ShouldBeDuplicate(await FaultAsync(() => CreateAsync(NewContact("Upper", Mail("anna").ToUpperInvariant()), false)));
    }

    [Fact]
    public async Task Exclude_inactive_ignores_deactivated_match()
    {
        var inactive = await ContactAsync("Inactive", Mail("inactive"));
        await Admin.UpdateAsync(new Entity(Contact, inactive) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2) });
        await ContactAsync("Active", Mail("active"));
        await PublishedRuleAsync(excludeInactive: true);

        await CreateAsync(NewContact("Second", Mail("inactive")), false);
        ShouldBeDuplicate(await FaultAsync(() => CreateAsync(NewContact("Second", Mail("active")), false)));
    }

    // ---- Update -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_to_duplicate_value_fails()
    {
        await ContactAsync("Anna", Mail("anna"));
        var other = await ContactAsync("Other", Mail("other"));
        await PublishedRuleAsync();

        ShouldBeDuplicate(await FaultAsync(() => UpdateAsync(new Entity(Contact, other) { [Email] = Mail("anna") }, false)));

        (await ContactsWithAsync(Mail("other"))).Should().Be(1, "the refused Update changed nothing");
    }

    [Fact]
    public async Task Update_of_unrelated_column_does_not_fail_on_existing_duplicate()
    {
        await ContactAsync("Anna", Mail("anna"));
        var twin = await ContactAsync("Twin", Mail("anna"));
        await PublishedRuleAsync();

        await UpdateAsync(new Entity(Contact, twin) { ["jobtitle"] = "Engineer" }, false);

        (await Admin.RetrieveAsync(Contact, twin, new Microsoft.Xrm.Sdk.Query.ColumnSet("jobtitle"))).GetAttributeValue<string>("jobtitle")
            .Should().Be("Engineer");
    }

    [Fact]
    public async Task Update_does_not_match_itself()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        await UpdateAsync(new Entity(Contact, anna) { [Email] = Mail("anna").ToUpperInvariant() }, false);
    }

    [Fact]
    public async Task Update_resending_unchanged_value_on_existing_duplicate_succeeds()
    {
        await ContactAsync("Anna", Mail("anna"));
        var twin = await ContactAsync("Twin", Mail("anna"));
        await ContactAsync("Bob", Mail("bob"));
        await PublishedRuleAsync();

        // A retrieve-modify-update client sends the matched column back unchanged (its code is the stored one).
        await UpdateAsync(new Entity(Contact, twin) { [Email] = Mail("anna"), ["jobtitle"] = "Engineer" }, false);
        await UpdateAsync(new Entity(Contact, twin) { [Email] = " " + Mail("anna").ToUpperInvariant() }, false);

        ShouldBeDuplicate(await FaultAsync(() => UpdateAsync(new Entity(Contact, twin) { [Email] = Mail("bob") }, false)));
    }

    [Fact]
    public async Task Decimal_is_coded_as_stored()
    {
        var table = await DecimalTableAsync();
        try
        {
            var stored = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "stored", ["new_amount"] = 1.23m });
            var rounded = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "rounded", ["new_amount"] = -1.225m });
            (await Admin.RetrieveAsync(table, rounded, new Microsoft.Xrm.Sdk.Query.ColumnSet("new_amount")))
                .GetAttributeValue<decimal>("new_amount").Should().Be(-1.23m, "SQL Server rounds half away from zero (not to even) to the column's scale");
            var rule = await _kit.RuleAsync("new_amount", table);
            await _kit.PublishAndRunAsync(rule);

            ShouldBeDuplicate(await FaultAsync(() => CreateAsync(new Entity(table) { ["new_name"] = "more digits", ["new_amount"] = 1.234m }, false)));
            ShouldBeDuplicate(await FaultAsync(() => CreateAsync(new Entity(table) { ["new_name"] = "half", ["new_amount"] = -1.2250m }, false)));
            // Half away from zero (not to even): 1.225 is stored as 1.23, so it is a duplicate; 1.2249 is stored as 1.22.
            ShouldBeDuplicate(await FaultAsync(() => CreateAsync(new Entity(table) { ["new_name"] = "midpoint", ["new_amount"] = 1.225m }, false)));
            await CreateAsync(new Entity(table) { ["new_name"] = "other", ["new_amount"] = 1.2249m }, false);
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    // ---- other tables, rollbacks, parameters, plug-ins ----------------------------------------------------------------

    [Fact]
    public async Task Cross_table_rule_checks_matching_table()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync(table: "account", matchingTable: Contact);
        var account = new Entity("account") { ["name"] = "Account " + _kit.Run, [Email] = Mail("anna") };

        ShouldBeDuplicate(await FaultAsync(() => CreateAsync(account, false)));

        await CreateAsync(NewContact("Contact", Mail("anna")), false); // no rule has contact as its base table
    }

    [Fact]
    public async Task Refused_create_leaves_no_code()
    {
        await ContactAsync("Anna", Mail("anna"));
        var rule = await PublishedRuleAsync();
        var codes = await _kit.CodeCountAsync(rule);
        var id = Guid.NewGuid();

        ShouldBeDuplicate(await FaultAsync(() => CreateAsync(NewContact("Refused", Mail("anna"), id), false)));

        (await _kit.CodeAsync(rule, id)).Should().BeNull();
        (await _kit.CodeCountAsync(rule)).Should().Be(codes);
    }

    [Fact]
    public async Task Plugin_rollback_leaves_no_code()
    {
        var rule = await PublishedRuleAsync();
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", Contact, 40, config: "invalid:rolled back");
        var id = Guid.NewGuid();
        try
        {
            await FaultAsync(() => CreateAsync(NewContact("Rolled back", Mail("rolledback"), id), false));
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }

        (await _kit.CodeAsync(rule, id)).Should().BeNull();
        (await ContactsWithAsync(Mail("rolledback"))).Should().Be(0);
    }

    [Fact]
    public async Task Non_boolean_parameter_is_400()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        await BadRequestAsync(() => CreateAsync(NewContact("Text", Mail("text")), "false"));
        await BadRequestAsync(() => UpdateAsync(new Entity(Contact, anna) { ["jobtitle"] = "x" }, 0));
        (await ContactsWithAsync(Mail("text"))).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plugin_service_create_request_carries_the_parameter(bool suppress)
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.CreateContactWithDuplicateDetectionPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, config: $"{Mail("anna")}|{suppress}");
        try
        {
            var create = () => Admin.CreateAsync(new Entity("account") { ["name"] = "Nested " + _kit.Run });
            if (suppress)
            {
                await create();
                (await ContactsWithAsync(Mail("anna"))).Should().Be(2);
            }
            else
            {
                (await FaultAsync(create)).ErrorCode.Should().Be(DuplicatesFound, "the plug-in's CreateRequest asked for the check");
                (await ContactsWithAsync(Mail("anna"))).Should().Be(1);
            }
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    [Fact]
    public async Task Stage20_plugin_change_is_checked()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", Contact, 20, config: $"{Email}={Mail("anna")}");
        try
        {
            ShouldBeDuplicate(await FaultAsync(() => CreateAsync(NewContact("Changed", Mail("fresh")), false)));
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }
}
