using System.Globalization;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Xrmd.Pipeline;
using Xrmd.Security;
using Record = Xrmd.Model.Records.Record;

using Session = Xrmd.Integration.Tests.CoreSdkBridgeTests.Session;
using ModelRequest = Xrmd.Model.Messages.Request;
using StepRegistry = Xrmd.Pipeline.Registration.StepRegistry;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c2a: the Security rules on the model. Task 2: the row rules (<see cref="CurrencyRules"/>, <see cref="DuplicateRuleRules"/>,
/// <see cref="EnvironmentVariableRules"/>, <see cref="OrganizationSettingRules"/>, <see cref="RollupJobRules"/>) take <see cref="Record"/>
/// rows, and the core calls them directly: a write of their tables crosses no <see cref="CoreSdkBridges"/> bridge. The behaviour checks of
/// the removed row rule bridges (<c>CoreSdkBridgeTests</c>, ruling P2) are ported here onto the model members, which cross nothing.
/// Task 3: <see cref="AccessTeamRules"/> and <see cref="SecurityAdministration"/> on the model; the core's record team target is
/// <see cref="AccessTeamRules.RecordTeamTarget"/>.
/// </summary>
[Collection("xrmd")]
public sealed class SecurityModelTests(XrmdFixture fx)
{
    private static readonly Guid Unknown = CoreScenarioKit.UnknownId;

    /// <summary>The fault <paramref name="call"/> raises (null when none), checked to cross no bridge in this flow.</summary>
    private static async Task<XrmdFaultException?> NoCrossingAsync(Func<Task> call)
    {
        using var count = CoreSdkBridges.CountCrossings();
        XrmdFaultException? fault = null;
        try
        {
            await call();
        }
        catch (XrmdFaultException ex)
        {
            fault = ex;
        }
        count.Value.Should().Be(0, "a model row rule converts nothing");
        return fault;
    }

    /// <summary>
    /// Task 2 Step 1: a Create and an Update of a <c>transactioncurrency</c>, a <c>duplicaterule</c>, an <c>environmentvariablevalue</c>
    /// and an <c>organizationsetting</c> (none of them audited here) run their row rules on the model: no crossing.
    /// </summary>
    [Fact]
    public async Task Row_rules_on_a_plain_table_cross_nothing()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var s = session.S;
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var definition = await core.CreateRecordAsync(s, kit.Caller, new Record(EnvironmentVariableSchema.Definition)
            {
                [EnvironmentVariableSchema.SchemaName] = "new_smt" + suffix,
                [EnvironmentVariableSchema.DisplayName] = "smt " + suffix,
            }, default);
            var setting = await core.CreateRecordAsync(s, kit.Caller, new Record(AppModuleSchema.SettingDefinition)
            {
                ["uniquename"] = "new_smt" + suffix,
                ["displayname"] = "smt " + suffix,
            }, default);

            using var crossings = CoreSdkBridges.CountCrossings();
            var currency = await core.CreateRecordAsync(s, kit.Caller, new Record(CurrencySchema.Table)
            {
                ["currencyname"] = "smt currency",
                [CurrencySchema.Symbol] = "S$",
                [CurrencySchema.IsoCode] = "QSM",
                [CurrencySchema.CurrencyType] = new ChoiceValue(1),
                [CurrencySchema.ExchangeRate] = 2m,
                [CurrencySchema.Precision] = 2,
            }, default);
            await core.UpdateRecordAsync(s, kit.Caller, new Record(CurrencySchema.Table, currency)
            {
                ["currencyname"] = "smt currency 2",
                [CurrencySchema.ExchangeRate] = 3m,
            }, default);

            var rule = await core.CreateRecordAsync(s, kit.Caller, new Record(DuplicateDetectionSchema.Rule)
            {
                ["name"] = "smt rule",
                [DuplicateDetectionSchema.BaseEntityName] = "account",
                [DuplicateDetectionSchema.MatchingEntityName] = "account",
            }, default);
            await core.UpdateRecordAsync(s, kit.Caller, new Record(DuplicateDetectionSchema.Rule, rule) { ["name"] = "smt rule 2" }, default);

            var value = await core.CreateRecordAsync(s, kit.Caller, new Record(EnvironmentVariableSchema.Value)
            {
                [EnvironmentVariableSchema.DefinitionLookup] = new RecordReference { LogicalName = EnvironmentVariableSchema.Definition, Id = definition },
                [EnvironmentVariableSchema.ValueColumn] = "a",
            }, default);
            await core.UpdateRecordAsync(s, kit.Caller, new Record(EnvironmentVariableSchema.Value, value)
            {
                [EnvironmentVariableSchema.DefinitionLookup] = new RecordReference { LogicalName = EnvironmentVariableSchema.Definition, Id = definition },
                [EnvironmentVariableSchema.ValueColumn] = "b",
            }, default);

            var organizationSetting = await core.CreateRecordAsync(s, kit.Caller, new Record(AppModuleSchema.OrganizationSetting)
            {
                ["uniquename"] = "new_smt" + suffix,
                ["value"] = "a",
                ["settingdefinitionid"] = new RecordReference { LogicalName = AppModuleSchema.SettingDefinition, Id = setting },
            }, default);
            await core.UpdateRecordAsync(s, kit.Caller, new Record(AppModuleSchema.OrganizationSetting, organizationSetting)
            {
                ["value"] = "b",
                ["settingdefinitionid"] = new RecordReference { LogicalName = AppModuleSchema.SettingDefinition, Id = setting },
            }, default);

            crossings.Value.Should().Be(0, "the row rules of these tables run on the model");
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }

    // ---- the checks of the removed bridges (CoreSdkBridgeTests, ruling P2), on the model members ----

    [Fact]
    public async Task DemandRateInRange_refuses_a_currency_rate_out_of_range_only()
    {
        (await NoCrossingAsync(() =>
        {
            CurrencyRules.DemandRateInRange(new Record("contact") { [CurrencySchema.ExchangeRate] = 0m });
            CurrencyRules.DemandRateInRange(new Record(CurrencySchema.Table) { ["currencyname"] = "x" });
            return Task.CompletedTask;
        })).Should().BeNull();

        var fault = await NoCrossingAsync(() =>
        {
            CurrencyRules.DemandRateInRange(new Record(CurrencySchema.Table) { [CurrencySchema.ExchangeRate] = 0m });
            return Task.CompletedTask;
        });
        fault!.ErrorCode.Should().Be(ErrorCodes.InvalidExchangeRate);
        fault.HttpStatus.Should().Be(400);
    }

    /// <summary>Decision 2: a rate of another type is named by its SDK type, as today ("got a Money").</summary>
    [Fact]
    public async Task Currency_rate_of_another_type_keeps_the_sdk_type_name()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var fault = await NoCrossingAsync(() => CurrencyRules.EnforceRowAsync(new Record(CurrencySchema.Table)
        {
            ["currencyname"] = "smt money",
            [CurrencySchema.Symbol] = "S$",
            [CurrencySchema.ExchangeRate] = new MoneyValue(2m),
        }, Unknown, true, kit.Store, session.S, default));
        fault!.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().EndWith("got a Money.");
    }

    [Fact]
    public async Task EnforceRowAsync_runs_on_the_organization_and_passes_other_tables()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var contact = new Record("contact") { ["lastname"] = "smt" };
        (await NoCrossingAsync(() => CurrencyRules.EnforceRowAsync(contact, Unknown, true, kit.Store, session.S, default))).Should().BeNull();
        contact.Attributes.Should().ContainSingle();

        var organization = new Record("organization") { ["name"] = "smt" };
        (await NoCrossingAsync(() => CurrencyRules.EnforceRowAsync(organization, Unknown, false, kit.Store, session.S, default))).Should().BeNull();
        organization["name"].Should().Be("smt");

        var fault = await NoCrossingAsync(() => CurrencyRules.EnforceRowAsync(new Record("organization") { [CurrencySchema.PricingPrecision] = 9 },
            Unknown, false, kit.Store, session.S, default));
        fault!.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task ApplyServerColumnsAsync_fills_a_condition_and_leaves_other_tables()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var contact = new Record("contact");
        (await NoCrossingAsync(() => DuplicateRuleRules.ApplyServerColumnsAsync(session.S, kit.Store, "contact", contact, true, Unknown, default)))
            .Should().BeNull();
        contact.Attributes.Should().BeEmpty();

        var condition = new Record(DuplicateDetectionSchema.Condition);
        (await NoCrossingAsync(() => DuplicateRuleRules.ApplyServerColumnsAsync(session.S, kit.Store, DuplicateDetectionSchema.Condition, condition,
            true, Unknown, default))).Should().BeNull();
        condition["ignoreblankvalues"].Should().Be(false);
    }

    [Fact]
    public async Task EnvironmentVariable_ApplyRowAsync_refuses_a_definition_without_name_and_leaves_other_tables()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var contact = new Record("contact");
        (await NoCrossingAsync(() => EnvironmentVariableRules.ApplyRowAsync(session.S, "contact", contact, true, Unknown, default))).Should().BeNull();
        contact.Attributes.Should().BeEmpty();

        var fault = await NoCrossingAsync(() => EnvironmentVariableRules.ApplyRowAsync(session.S, EnvironmentVariableSchema.Definition,
            new Record(EnvironmentVariableSchema.Definition), true, Unknown, default));
        fault!.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task OrganizationSetting_ApplyRowAsync_runs_with_a_definition_only()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        (await NoCrossingAsync(async () =>
        {
            await OrganizationSettingRules.ApplyRowAsync(session.S, AppModuleSchema.OrganizationSetting,
                new Record(AppModuleSchema.OrganizationSetting) { ["value"] = "x" }, false, Unknown, default);
            await OrganizationSettingRules.ApplyRowAsync(session.S, "contact", new Record("contact"), true, Unknown, default);
        })).Should().BeNull();

        var row = new Record(AppModuleSchema.OrganizationSetting)
        {
            ["settingdefinitionid"] = new RecordReference { LogicalName = AppModuleSchema.SettingDefinition, Id = Guid.Empty },
        };
        (await NoCrossingAsync(() => OrganizationSettingRules.ApplyRowAsync(session.S, AppModuleSchema.OrganizationSetting, row, true, Unknown, default)))
            .Should().BeNull();
        row.GetValue<RecordReference>("settingdefinitionid")!.Id.Should().Be(Guid.Empty);

        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var name = "new_smt" + Guid.NewGuid().ToString("N")[..8];
        var definition = await core.CreateRecordAsync(session.S, kit.Caller,
            new Record(AppModuleSchema.SettingDefinition) { ["uniquename"] = name, ["displayname"] = name }, default);
        Record Setting() => new(AppModuleSchema.OrganizationSetting)
        {
            ["uniquename"] = name,
            ["value"] = "a",
            ["settingdefinitionid"] = new RecordReference { LogicalName = AppModuleSchema.SettingDefinition, Id = definition },
        };
        await core.CreateRecordAsync(session.S, kit.Caller, Setting(), default);
        var fault = await NoCrossingAsync(() =>
            OrganizationSettingRules.ApplyRowAsync(session.S, AppModuleSchema.OrganizationSetting, Setting(), true, Guid.NewGuid(), default));
        fault!.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        fault.Message.Should().Be($"The setting {name} already has an environment value.");
    }

    // ---- Task 3: access teams and security administration ----

    /// <summary>
    /// Task 3 Step 1: a template Create, AddUserToRecordTeam, RemoveUserFromRecordTeam (each marked as checked with the model
    /// <see cref="AccessTeamRules.RecordTeamTarget"/>, as the guard marks it), an Associate of a role to a user and AddPrivilegesRole,
    /// through the core in one transaction that is rolled back, cross no bridge.
    /// </summary>
    [Fact]
    public async Task Record_teams_and_role_grants_cross_no_security_bridge()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var (record, user, _) = await kit.RecordTeamAsync();
        var role = await kit.RoleAsync();
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var s = session.S;
        var account = kit.Store.GetTable("account")!;
        try
        {
            using var crossings = CoreSdkBridges.CountCrossings();
            var template = await core.CreateRecordAsync(s, kit.Caller, new Record(AccessTeamSchema.TeamTemplate)
            {
                ["teamtemplatename"] = "smt template " + Guid.NewGuid().ToString("N")[..8],
                [AccessTeamSchema.ObjectTypeCode] = account.ObjectTypeCode!.Value,
                [AccessTeamSchema.DefaultAccessRightsMask] = (int)AccessMask.ReadAccess,
            }, default);
            var checkedTarget = new AccessTeamRules.RecordTeamTarget(account, record.Id, user, template, AccessMask.ReadAccess, null);

            var add = new AddUserToRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template };
            AccessTeamRules.MarkChecked(add, checkedTarget);
            var added = (AddUserToRecordTeamResponse)await core.ExecuteCoreAsync(add, kit.Caller, s, default);
            added.AccessTeamId.Should().NotBeEmpty();

            var remove = new RemoveUserFromRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template };
            AccessTeamRules.MarkChecked(remove, checkedTarget);
            var removed = (RemoveUserFromRecordTeamResponse)await core.ExecuteCoreAsync(remove, kit.Caller, s, default);
            removed.AccessTeamId.Should().Be(added.AccessTeamId);

            await core.ExecuteCoreAsync(new AssociateRequest
            {
                Target = new EntityReference("systemuser", user),
                Relationship = new Relationship(SecurityAdministration.SystemUserRoles),
                RelatedEntities = [new EntityReference("role", role)],
            }, kit.Caller, s, default);
            await core.ExecuteCoreAsync(new AddPrivilegesRoleRequest
            {
                RoleId = role,
                Privileges = [new RolePrivilege { PrivilegeName = "prvReadAccount", Depth = PrivilegeDepth.Basic }],
            }, kit.Caller, s, default);

            crossings.Value.Should().Be(0, "the record team, template and role rules run on the model");
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }

    /// <summary>
    /// From <c>CoreSdkBridgeTests</c> (ruling P2): the template rules refuse a mask that is not a subset of the template rights, on the
    /// model and with no crossing. The other tables are passed by the core's own branch (<c>ApplyAccessTeamRulesAsync</c> runs the rule
    /// for a <c>teamtemplate</c> only), which the bridge's pre-filter copied.
    /// </summary>
    [Fact]
    public async Task DemandTemplateAsync_refuses_a_mask_outside_the_template_rights()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var fault = await NoCrossingAsync(() => AccessTeamRules.DemandTemplateAsync(session.S, kit.Store,
            new Record(AccessTeamSchema.TeamTemplate) { [AccessTeamSchema.DefaultAccessRightsMask] = 8 }, true, Unknown, default));
        fault!.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().EndWith("and nothing else; got 8.");
    }

    /// <summary>The masks of another type than <see cref="int"/> a caller can send a template.</summary>
    public static TheoryData<object> OtherMasks => new() { new OptionSetValue(1), new Money(1m), new EntityReference("account", Unknown), "1" };

    /// <summary>
    /// Ruling 3a (ported from the template rules' SDK twin, which Task 4 removes): the system table rules, run on the SDK request the
    /// guard checks (the <see cref="SystemTableRules.EnforceAsync(OrganizationRequest, Caller, Guid, SecurityCache, IMetadataStore, DataSession, CancellationToken)"/>
    /// twin), read the mask in as a model value, and the text still prints the value as the caller sent it.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherMasks))]
    public async Task Template_mask_of_another_type_keeps_the_sdk_text(object mask)
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var request = new CreateRequest { Target = new Entity(AccessTeamSchema.TeamTemplate) { [AccessTeamSchema.DefaultAccessRightsMask] = mask } };
        var system = await kit.Cache.SystemUserIdAsync(default);
        var fault = await Assert.ThrowsAsync<XrmdFaultException>(() =>
            SystemTableRules.EnforceAsync(request, kit.Caller, system, kit.Cache, kit.Store, session.S, default));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().EndWith($"and nothing else; got {mask}.");
    }

    /// <summary>
    /// The record team parameters and the security relationship pairs on the model (ported from the SDK twins the <c>SecurityGuard</c>
    /// called until Task 5): they answer the request's own references, and a parameter of another type keeps its SDK type name
    /// (Decision 2), as the twins did.
    /// </summary>
    [Fact]
    public void Record_team_parameters_and_pairs_answer_the_requests_own_references()
    {
        var record = new RecordReference { LogicalName = "account", Id = Guid.NewGuid() };
        var request = new ModelRequest { Name = AccessTeamRules.AddUserToRecordTeam };
        request.Parameters["Record"] = record;
        request.Parameters["SystemUserId"] = Guid.NewGuid();
        request.Parameters["TeamTemplateId"] = Guid.NewGuid();
        var (answered, userId, templateId) = AccessTeamRules.RecordTeamParameters(request);
        answered.Should().BeSameAs(record);
        userId.Should().Be((Guid)request.Parameters["SystemUserId"]!);
        templateId.Should().Be((Guid)request.Parameters["TeamTemplateId"]!);

        var wrong = new ModelRequest { Name = AccessTeamRules.AddUserToRecordTeam };
        wrong.Parameters["Record"] = new Record("account");
        FluentActions.Invoking(() => AccessTeamRules.RecordTeamParameters(wrong)).Should().Throw<XrmdFaultException>()
            .WithMessage("Required parameter 'Record' of request 'AddUserToRecordTeam' is missing or not a EntityReference.");
        var model = new ModelRequest { Name = AccessTeamRules.AddUserToRecordTeam };
        model.Parameters["Record"] = new RecordReference { LogicalName = "account", Id = Guid.NewGuid() };
        FluentActions.Invoking(() => AccessTeamRules.RecordTeamParameters(model)).Should().Throw<XrmdFaultException>()
            .WithMessage("Required parameter 'SystemUserId' of request 'AddUserToRecordTeam' is missing or not a Guid.");

        var relationship = SecurityAdministration.FindRelationship(SecurityAdministration.SystemUserRoles)!;
        var user = new RecordReference { LogicalName = "systemuser", Id = Guid.NewGuid() };
        var role = new RecordReference { LogicalName = "role", Id = Guid.NewGuid() };
        relationship.TryGetPairs(role, [user], out var pairs).Should().BeTrue();
        pairs.Should().ContainSingle();
        pairs[0].First.Should().BeSameAs(user);
        pairs[0].Second.Should().BeSameAs(role);
        relationship.TryGetPairs(role, [new RecordReference { LogicalName = "team", Id = Guid.NewGuid() }], out _).Should().BeFalse();
    }

    /// <summary>
    /// Task 4 Step 1: the system table rules and the field share rules, run on the model request of each Task 1 scenario (the guard's
    /// first two steps), change nothing in it, whether they pass or refuse, except on the field share Create: there
    /// <see cref="SystemTableRules.EnforceFieldSharesAsync(Xrmd.Model.Messages.Request, Caller, FieldSecurity, IMetadataStore, DataSession, Func{RecordReference, CancellationToken, Task}, CancellationToken)"/>
    /// answers true and the Target gains <c>objecttypecode</c>, <c>readaccess</c> and <c>updateaccess</c> and a new <c>objectid</c>.
    /// The record read the guard demands is not this test's subject: the callback passes.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardFaultGoldenTests.Names), MemberType = typeof(GuardFaultGoldenTests))]
    public async Task Only_a_field_share_create_changes_the_request(string name)
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var call = await GuardScenarios.All.Single(s => s.Name == name).Arrange(new GuardKit(kit));
        // The golden of a Create whose Target is a reference counts the accounts of the arranged name: this test leaves none behind.
        if (call.Request is CreateRequest && call.Request.Parameters["Target"] is EntityReference arranged)
            kit.OnCleanup(() => kit.Admin.DeleteAsync(arranged.LogicalName, arranged.Id));
        var model = new SdkMirror().In(call.Request)!;
        var before = Dump(model);
        var target = model.Parameters.TryGetValue("Target", out var t) ? t as Record : null;
        var attributesBefore = target is null ? null : new Dictionary<string, object?>(target.Attributes);

        var filled = false;
        await using (var session = await Session.OpenAsync(kit.ConnectionString))
        {
            try
            {
                await SystemTableRules.EnforceAsync(model, call.Caller, await kit.Cache.SystemUserIdAsync(default), kit.Cache, kit.Store, session.S, default);
            }
            catch (XrmdFaultException)
            {
                // A refusal changes nothing either.
            }
            try
            {
                filled = await SystemTableRules.EnforceFieldSharesAsync(model, call.Caller, kit.Services.GetRequiredService<FieldSecurity>(),
                    kit.Store, session.S, (_, _) => Task.CompletedTask, default);
            }
            catch (XrmdFaultException)
            {
                // Idem.
            }
        }
        kit.Cache.Invalidate();

        if (name != "FieldShareCreate")
        {
            filled.Should().BeFalse();
            Dump(model).Should().Be(before);
            return;
        }
        filled.Should().BeTrue();
        string[] filledColumns = [FieldSecurityTables.ShareObjectId, FieldSecurityTables.ShareObjectTypeCodeColumn,
            FieldSecurityTables.ShareReadAccess, FieldSecurityTables.ShareUpdateAccess];
        target!.Attributes.Keys.Should().Equal(attributesBefore!.Keys.Concat(filledColumns.Where(c => !attributesBefore.ContainsKey(c))));
        foreach (var (column, value) in attributesBefore.Where(a => !filledColumns.Contains(a.Key)))
            target[column].Should().BeSameAs(value, column);
        var objectId = target.GetValue<RecordReference>(FieldSecurityTables.ShareObjectId)!;
        objectId.Should().NotBeSameAs(attributesBefore![FieldSecurityTables.ShareObjectId]);
        objectId.LogicalName.Should().Be(GuardKit.SecuredTable);
        objectId.Id.Should().Be(((RecordReference)attributesBefore[FieldSecurityTables.ShareObjectId]!).Id);
        target[FieldSecurityTables.ShareObjectTypeCodeColumn].Should().Be(GuardKit.SecuredTable);
        target[FieldSecurityTables.ShareReadAccess].Should().Be(true);
        target[FieldSecurityTables.ShareUpdateAccess].Should().Be(false);
    }

    /// <summary>The request as text, through a mirror of its own (so the dump reads the model request's values, not a twin's).</summary>
    private static string Dump(Xrmd.Model.Messages.Request request) => CoreDump.RequestText(new SdkMirror().Out(request)!, fixedDates: true);

    /// <summary>
    /// Task 5 Step 1 (Decision 5, ruling 4a; ported from the field share rules' SDK twin, which Task 5 removes): a field share Create
    /// through the pipeline changes the caller's own <c>Entity</c> as today. On a pass, the Target holds <c>objecttypecode</c> (the table),
    /// <c>readaccess</c> and <c>updateaccess</c> as booleans and an <c>objectid</c> that is a new instance with the same table and id; the
    /// other values stay the caller's instances. On a refusal after the fill (a share that grants nothing), the Target keeps what was
    /// filled up to it.
    /// </summary>
    [Fact]
    public async Task Field_share_create_changes_the_callers_target_as_today()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var guardKit = new GuardKit(kit);
        var call = await GuardScenarios.All.Single(s => s.Name == "FieldShareCreate").Arrange(guardKit);
        var target = ((CreateRequest)call.Request).Target;
        var principal = target[FieldSecurityTables.SharePrincipalId];
        var attribute = target[FieldSecurityTables.ShareAttributeId];
        var objectId = (EntityReference)target[FieldSecurityTables.ShareObjectId];

        (await guardKit.RunAsync(call.Request, call.Caller)).Should().BeNull();

        target.Attributes.Keys.Should().Equal(FieldSecurityTables.ShareObjectId, FieldSecurityTables.ShareAttributeId,
            FieldSecurityTables.SharePrincipalId, FieldSecurityTables.ShareReadAccess, FieldSecurityTables.ShareObjectTypeCodeColumn,
            FieldSecurityTables.ShareUpdateAccess);
        target[FieldSecurityTables.SharePrincipalId].Should().BeSameAs(principal);
        var filled = (EntityReference)target[FieldSecurityTables.ShareObjectId];
        filled.Should().NotBeSameAs(objectId);
        (filled.LogicalName, filled.Id).Should().Be((GuardKit.SecuredTable, objectId.Id));
        target[FieldSecurityTables.ShareObjectTypeCodeColumn].Should().Be(GuardKit.SecuredTable);
        target[FieldSecurityTables.ShareReadAccess].Should().Be(true);
        target[FieldSecurityTables.ShareUpdateAccess].Should().Be(false);

        // A share granting nothing is refused after objectid and objecttypecode were filled: the caller's Target keeps them.
        var refused = new Entity(FieldSecurityTables.Share)
        {
            [FieldSecurityTables.ShareObjectId] = objectId, [FieldSecurityTables.ShareAttributeId] = attribute,
            [FieldSecurityTables.SharePrincipalId] = principal, [FieldSecurityTables.ShareReadAccess] = false,
        };
        var fault = await guardKit.RunAsync(new CreateRequest { Target = refused }, call.Caller);
        fault.Should().BeOfType<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        refused.Attributes.Keys.Should().Equal(FieldSecurityTables.ShareObjectId, FieldSecurityTables.ShareAttributeId,
            FieldSecurityTables.SharePrincipalId, FieldSecurityTables.ShareReadAccess, FieldSecurityTables.ShareObjectTypeCodeColumn);
        var refusedObjectId = (EntityReference)refused[FieldSecurityTables.ShareObjectId];
        refusedObjectId.Should().NotBeSameAs(objectId);
        (refusedObjectId.LogicalName, refusedObjectId.Id).Should().Be((GuardKit.SecuredTable, objectId.Id));
        refused[FieldSecurityTables.ShareObjectTypeCodeColumn].Should().Be(GuardKit.SecuredTable);
        refused[FieldSecurityTables.ShareReadAccess].Should().Be(false);
    }

    /// <summary>
    /// Task 4 Step 1: the solution import runs the system table rules on each row it writes, as an SDK request and as the import's caller
    /// (<c>SolutionImporter.EnforceSystemTableRulesAsync</c>, through the
    /// <see cref="SystemTableRules.EnforceAsync(OrganizationRequest, Caller, Guid, SecurityCache, IMetadataStore, DataSession, CancellationToken)"/>
    /// twin): a component row that writes <c>ismanaged</c> gets today's 400, on Create and on Update, and the request is left as it was.
    /// </summary>
    [Fact]
    public async Task Solution_import_keeps_the_system_table_rules()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var system = await kit.Cache.SystemUserIdAsync(default);
        OrganizationRequest[] requests =
        [
            new CreateRequest { Target = new Entity("savedquery") { ["name"] = "smt view", [SystemTableRules.IsManaged] = true } },
            new UpdateRequest { Target = new Entity("savedquery", Unknown) { [SystemTableRules.IsManaged] = false } },
        ];
        foreach (var request in requests)
        {
            var before = CoreDump.RequestText(request);
            var fault = await Assert.ThrowsAsync<XrmdFaultException>(() =>
                SystemTableRules.EnforceAsync(request, kit.Caller, system, kit.Cache, kit.Store, session.S, default));
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Be("The attribute ismanaged of savedquery is maintained by the solution layers and cannot be set.");
            CoreDump.RequestText(request).Should().Be(before);
        }
        // The layer operations write it as the internal caller.
        await SystemTableRules.EnforceAsync(requests[0], Caller.Internal, system, kit.Cache, kit.Store, session.S, default);
    }

    // ---- Task 5: the guard on the pipeline's model request ----

    private const string Attachment = "activitymimeattachment";

    /// <summary>
    /// Task 5 Step 1 (Decision 5): the guard runs on the model request the pipeline crossed, which is the instance the core gets, for an
    /// Update and for AddUserToRecordTeam (whose record team mark the core reads on that instance).
    /// </summary>
    [Fact]
    public async Task Guard_runs_on_the_pipelines_model_request()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var (record, user, template) = await kit.RecordTeamAsync();
        var account = await kit.Admin.CreateAsync(new Entity("account") { ["name"] = "smt seam account" });
        kit.OnCleanup(() => kit.Admin.DeleteAsync("account", account));
        OrganizationRequest[] requests =
        [
            new UpdateRequest { Target = new Entity("account", account) { ["name"] = "smt seam account 2" } },
            new AddUserToRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template },
        ];
        foreach (var request in requests)
        {
            var guarded = new List<ModelRequest>();
            var cored = new List<ModelRequest>();
            SecurityGuard.AuthorizedRequest.Value = guarded.Add;
            CoreOperations.MessageRequest.Value = cored.Add;
            try
            {
                (await new GuardKit(kit).RunAsync(request, kit.Caller)).Should().BeNull(request.RequestName);
            }
            finally
            {
                SecurityGuard.AuthorizedRequest.Value = null;
                CoreOperations.MessageRequest.Value = null;
            }
            guarded.Should().NotBeEmpty(request.RequestName);
            guarded[0].Name.Should().Be(request.RequestName);
            cored.Should().NotBeEmpty(request.RequestName);
            cored[0].Should().BeSameAs(guarded[0], "the core gets the model request the guard checked ({0})", request.RequestName);
        }
    }

    /// <summary>
    /// Task 5 Step 1 (Decision 6): a plug-in on the Update of an account calls AddUserToRecordTeam through its service; the guard marks
    /// the nested call's model request and the core finds the mark there, so the member is added.
    /// </summary>
    [Fact]
    public async Task Record_team_mark_survives_a_plugins_nested_call()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var (record, user, template) = await kit.RecordTeamAsync();
        var admin = kit.Admin;
        var registry = kit.Services.GetRequiredService<StepRegistry>();
        kit.OnCleanup(async () =>
        {
            await PluginTestHelper.ClearAsync(kit.ConnectionString);
            registry.Invalidate();
        });
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(admin, assembly, "Xrmd.TestPlugins.AddUserToRecordTeamPlugin");
        await PluginTestHelper.RegisterStepAsync(admin, type, "Update", "account", 40, config: $"{user}|{template}");
        registry.Invalidate();

        await admin.UpdateAsync(new Entity("account", record.Id) { ["name"] = "smt plug-in team account" });

        var members = await kit.ScalarAsync($"""
            SELECT COUNT(*) FROM data.teammembership AS tm JOIN data.team AS t ON t.teamid = tm.teamid
            WHERE t.regardingobjectid = '{record.Id}' AND t.teamtemplateid = '{template}' AND tm.systemuserid = '{user}'
            """);
        Convert.ToInt64(members, CultureInfo.InvariantCulture).Should().Be(1);
    }

    /// <summary>
    /// Task 5 Step 1 (Decision 5): a Basic User deletes their email with two attachments; the core's cascade marks each attachment
    /// Delete on its model request, which is what the guard reads, so the attachment Deletes pass without the attachment check.
    /// </summary>
    [Fact]
    public async Task Cascade_mark_is_read_on_the_model()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var (caller, _) = await new GuardKit(kit).BasicUserAsync();
        try
        {
            var email = ((CreateResponse)await ExecuteAsync(kit, caller,
                new CreateRequest { Target = new Entity("email") { ["subject"] = "smt cascade" } })).id;
            var attachments = new List<Guid>();
            foreach (var name in new[] { "a.txt", "b.txt" })
                attachments.Add(((CreateResponse)await ExecuteAsync(kit, caller, new CreateRequest
                {
                    Target = new Entity(Attachment)
                    {
                        ["objectid"] = new EntityReference("email", email), ["subject"] = name, ["filename"] = name,
                        ["body"] = Convert.ToBase64String("smt"u8.ToArray()),
                    },
                })).id);

            var guarded = new List<ModelRequest>();
            SecurityGuard.AuthorizedRequest.Value = guarded.Add;
            try
            {
                await ExecuteAsync(kit, caller, new DeleteRequest { Target = new EntityReference("email", email) });
            }
            finally
            {
                SecurityGuard.AuthorizedRequest.Value = null;
            }

            var attachmentDeletes = guarded
                .Where(r => r.Name == "Delete" && r.Parameters["Target"] is RecordReference { LogicalName: Attachment }).ToList();
            attachmentDeletes.Should().HaveCount(2);
            attachmentDeletes.Should().OnlyContain(r => CascadeHint.IsMarked(r), "the core marks its cascade on the model request the guard reads");
            guarded.Where(r => r.Parameters["Target"] is RecordReference { LogicalName: "email" }).Should().ContainSingle()
                .Which.Should().Match<ModelRequest>(r => !CascadeHint.IsMarked(r));
            foreach (var attachment in attachments)
                Convert.ToInt64(await kit.ScalarAsync($"SELECT COUNT(*) FROM data.{Attachment} WHERE activitymimeattachmentid = '{attachment}'"),
                    CultureInfo.InvariantCulture).Should().Be(0);
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }

    /// <summary>Runs <paramref name="request"/> through the pipeline as <paramref name="caller"/> at depth 1 with no session.</summary>
    private static async Task<OrganizationResponse> ExecuteAsync(CoreScenarioKit kit, Caller caller, OrganizationRequest request)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PipelineExecutor>()
            .ExecuteAsync(request, new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()), default);
    }

    /// <summary>
    /// Task 5 Step 1 (Decision 7): the guard, run on the model request of each Task 1 scenario as the pipeline now runs it, crosses no
    /// <see cref="CoreSdkBridges"/> bridge, whether it passes or refuses.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardFaultGoldenTests.Names), MemberType = typeof(GuardFaultGoldenTests))]
    public async Task Guard_crosses_no_security_bridge(string name)
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var call = await GuardScenarios.All.Single(s => s.Name == name).Arrange(new GuardKit(kit));
        // The golden of a Create whose Target is a reference counts the accounts of the arranged name: this test leaves none behind.
        if (call.Request is CreateRequest && call.Request.Parameters["Target"] is EntityReference arranged)
            kit.OnCleanup(() => kit.Admin.DeleteAsync(arranged.LogicalName, arranged.Id));
        var model = new SdkMirror().In(call.Request)!;
        await using var scope = kit.Services.CreateAsyncScope();
        var guard = scope.ServiceProvider.GetRequiredService<SecurityGuard>();
        await using (var session = await Session.OpenAsync(kit.ConnectionString))
        {
            using var crossings = CoreSdkBridges.CountCrossings();
            try
            {
                await guard.AuthorizeAsync(model, call.Caller, session.S, default);
            }
            catch (XrmdFaultException)
            {
                // A refusal crosses nothing either.
            }
            crossings.Value.Should().Be(0, "the guard runs on the model request");
        }
        kit.Cache.Invalidate();
    }

    // ---- Fix round 1 (ruling 4b): the cast error texts ----

    /// <summary>
    /// Ruling 4b: a value of the wrong type read by a rule on the model throws the <see cref="InvalidCastException"/> text that today's
    /// SDK read gave (it reaches plug-ins as the fault message), naming the SDK types: the rollup job rule (statecode), the post rule
    /// (the source of a post Create) and the guard (the owner of a Create). The text expected is the SDK's own, observed here.
    /// </summary>
    [Fact]
    public async Task Cast_errors_keep_todays_sdk_text()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var guardKit = new GuardKit(kit);
        var (system, _) = await guardKit.SystemAsync();
        var job = await guardKit.RollupJobAsync();
        var reference = new EntityReference("account", Guid.NewGuid());

        var rollup = await guardKit.RunAsync(new UpdateRequest
        {
            Target = new Entity("asyncoperation", job) { ["statecode"] = reference },
        }, system);
        rollup.Should().BeOfType<InvalidCastException>().Which.Message.Should().Be(SdkCastText<OptionSetValue>(reference))
            .And.Be("Unable to cast object of type 'Microsoft.Xrm.Sdk.EntityReference' to type 'Microsoft.Xrm.Sdk.OptionSetValue'.");

        var post = await guardKit.RunAsync(new CreateRequest
        {
            Target = new Entity("post") { ["source"] = reference, ["text"] = "smt cast" },
        }, system);
        post.Should().BeOfType<InvalidCastException>().Which.Message.Should().Be(SdkCastText<OptionSetValue>(reference));

        var (basic, _) = await guardKit.BasicUserAsync(("prvCreateAccount", DepthMask.Global));
        var choice = new OptionSetValue(1);
        var guard = await guardKit.RunAsync(new CreateRequest
        {
            Target = new Entity("account") { ["name"] = "smt cast", ["ownerid"] = choice },
        }, basic);
        guard.Should().BeOfType<InvalidCastException>().Which.Message.Should().Be(SdkCastText<EntityReference>(choice))
            .And.Be("Unable to cast object of type 'Microsoft.Xrm.Sdk.OptionSetValue' to type 'Microsoft.Xrm.Sdk.EntityReference'.");
    }

    /// <summary>The text today's SDK read (<c>GetAttributeValue&lt;T&gt;</c>) throws for <paramref name="value"/>.</summary>
    private static string SdkCastText<T>(object value)
    {
        try
        {
            new Entity("x") { ["a"] = value }.GetAttributeValue<T>("a");
        }
        catch (InvalidCastException ex)
        {
            return ex.Message;
        }
        throw new InvalidOperationException("the SDK read did not throw");
    }
}
