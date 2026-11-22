using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Pipeline;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The migration context of the core (migration M3, task 1; spec §7.5): inside <see cref="MigrationScope"/> the core keeps the
/// system columns a record carries, applies no closed-activity rule and no upload size limit, writes no audit row, and the
/// pipeline refuses to run, so no plug-in, workflow or webhook can fire.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationScopeTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await SetOrganizationAuditAsync(false);
    }

    private async Task<Caller> AdminCallerAsync() => await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);

    /// <summary>Runs <paramref name="work"/> on the host's core, in one committed transaction, inside a migration scope when <paramref name="scoped"/>.</summary>
    private async Task<T> CoreAsync<T>(Func<CoreOperations, DataSession, Caller, Task<T>> work, bool scoped = true)
    {
        var caller = await AdminCallerAsync();
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        T result;
        using (scoped ? MigrationScope.Enter() : null)
            result = await work(core, new DataSession(conn, tx), caller);
        await tx.CommitAsync();
        return result;
    }

    private async Task SetOrganizationAuditAsync(bool enabled)
    {
        var orgId = (Guid)(await AuthoringTestHelper.ScalarAsync(fx, "SELECT TOP 1 organizationid FROM data.organization"))!;
        await Admin.UpdateAsync(new Entity("organization", orgId) { ["isauditenabled"] = enabled });
    }

    private static readonly DateTime CreatedOn = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime ModifiedOn = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task Create_keeps_system_columns()
    {
        var root = await RootBusinessUnitAsync(fx);
        var child = await CreateBusinessUnitAsync(fx, root, "mig-bu-" + _run);
        var user = await CreateUserAsync(fx, child);
        var id = Guid.NewGuid();

        await CoreAsync((core, s, caller) => core.CreateAsync(s, caller, new Entity("account", id)
        {
            ["name"] = "Migrated " + _run,
            ["createdon"] = CreatedOn,
            ["modifiedon"] = ModifiedOn,
            ["createdby"] = new EntityReference("systemuser", user),
            ["modifiedby"] = new EntityReference("systemuser", user),
            ["ownerid"] = new EntityReference("systemuser", user),
            ["owningbusinessunit"] = new EntityReference("businessunit", root),
            ["statecode"] = new OptionSetValue(1),
            ["statuscode"] = new OptionSetValue(2),
        }, default));

        var row = await Admin.RetrieveAsync("account", id, new ColumnSet(true));
        row.GetAttributeValue<DateTime>("createdon").Should().Be(CreatedOn);
        row.GetAttributeValue<DateTime>("modifiedon").Should().Be(ModifiedOn);
        row.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(user);
        row.GetAttributeValue<EntityReference>("modifiedby").Id.Should().Be(user);
        row.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
        row.GetAttributeValue<EntityReference>("owningbusinessunit").Id.Should().Be(root, "the exported value wins over the owner's unit");
        row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        row.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2);
    }

    [Fact]
    public async Task Create_outside_scope_ignores_given_system_columns()
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        var caller = await AdminCallerAsync();
        var id = Guid.NewGuid();

        await CoreAsync((core, s, c) => core.CreateAsync(s, c, new Entity("account", id)
        {
            ["name"] = "Not migrated " + _run,
            ["createdon"] = CreatedOn,
            ["createdby"] = new EntityReference("systemuser", user),
        }, default), scoped: false);

        var row = await Admin.RetrieveAsync("account", id, new ColumnSet("createdon", "createdby"));
        row.GetAttributeValue<DateTime>("createdon").Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        row.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(caller.UserId);
    }

    [Fact]
    public async Task Update_keeps_given_modifiedon_and_changes_closed_activity()
    {
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Regarding " + _run });
        var task = await Admin.CreateAsync(new Entity("task")
        {
            ["subject"] = "Closed " + _run, ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(5),
        });

        await CoreAsync(async (core, s, caller) =>
        {
            await core.UpdateAsync(s, caller, new Entity("task", task)
            {
                ["subject"] = "Changed " + _run,
                ["regardingobjectid"] = new EntityReference("account", account),
                ["modifiedon"] = ModifiedOn,
            }, default);
            return 0;
        });

        var row = await Admin.RetrieveAsync("task", task, new ColumnSet("subject", "regardingobjectid", "modifiedon", "statecode"));
        row.GetAttributeValue<string>("subject").Should().Be("Changed " + _run);
        row.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(account);
        row.GetAttributeValue<DateTime>("modifiedon").Should().Be(ModifiedOn);
        row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
    }

    [Fact]
    public async Task Scope_writes_no_audit_rows()
    {
        await SetOrganizationAuditAsync(true);
        var id = Guid.NewGuid();

        await CoreAsync(async (core, s, caller) =>
        {
            await core.CreateAsync(s, caller, new Entity("account", id) { ["name"] = "Audited " + _run, ["telephone1"] = "1" }, default);
            await core.UpdateAsync(s, caller, new Entity("account", id) { ["telephone1"] = "2" }, default);
            return 0;
        });

        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT COUNT(*) FROM data.audit WHERE objectid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Scope_writes_fire_no_steps_and_queue_no_jobs()
    {
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", "account", 20, config: "accountnumber=from plug-in");
        await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", "account", 40, mode: 1, config: "accountnumber=async");
        var id = Guid.NewGuid();

        await CoreAsync((core, s, caller) => core.CreateAsync(s, caller, new Entity("account", id) { ["name"] = "No steps " + _run }, default));

        (await Admin.RetrieveAsync("account", id, new ColumnSet("accountnumber"))).GetAttributeValue<string>("accountnumber").Should().BeNull();
        (await AuthoringTestHelper.ScalarAsync(fx,
            "SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = @id AND operationtype IN (1, 10)", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Organization_service_write_inside_scope_fails_and_writes_nothing()
    {
        // Every front end (Web API, SOAP, MCP, plug-ins) writes through the organization service and its pipeline: inside the scope
        // that path fails explicitly instead of firing steps, workflows or webhooks with migrated values.
        var id = Guid.NewGuid();

        var act = async () =>
        {
            using (MigrationScope.Enter())
                await Admin.CreateAsync(new Entity("account", id) { ["name"] = "Through the service " + _run });
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("migration scope");
        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT COUNT(*) FROM data.account WHERE accountid = @id", ("@id", id))).Should().Be(0);
        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Pipeline_refuses_to_run_inside_scope()
    {
        var executor = fx.App.Services.CreateScope().ServiceProvider.GetRequiredService<PipelineExecutor>();
        var caller = await AdminCallerAsync();

        var act = async () =>
        {
            using (MigrationScope.Enter())
                await executor.ExecuteAsync(new CreateRequest { Target = new Entity("account") { ["name"] = "Refused " + _run } },
                    new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()), default);
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("migration scope");
    }

    [Fact]
    public async Task Note_body_over_max_upload_size_is_stored_in_scope()
    {
        // Over the default organization limit (5 MB): the source accepted it, so the import keeps it.
        var body = Convert.ToBase64String(new byte[6 * 1024 * 1024]);
        Entity Note(Guid id) => new("annotation", id)
        {
            ["subject"] = "Big " + _run, ["filename"] = "big.bin", ["mimetype"] = "application/octet-stream", ["documentbody"] = body,
        };

        var outside = async () => await CoreAsync((core, s, caller) => core.CreateAsync(s, caller, Note(Guid.NewGuid()), default), scoped: false);
        (await outside.Should().ThrowAsync<XrmdFaultException>()).Which.ErrorCode.Should().Be(ErrorCodes.AttachmentInvalidFileSize);

        var id = Guid.NewGuid();
        await CoreAsync((core, s, caller) => core.CreateAsync(s, caller, Note(id), default));
        (await AuthoringTestHelper.ScalarAsync(fx, "SELECT filesizeinbytes FROM data.fileattachment WHERE objectid = @id", ("@id", id)))
            .Should().Be(6L * 1024 * 1024);
    }
}
