using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Field sharing (plan 14, task 6, ruling F6; Review Focus 5): <c>principalobjectattributeaccess</c> rows written with plain
/// Create, Update and Delete grant read and/or update of one secured column on one record to a user or to the members of a team;
/// they are validated in the F6 order and removed with their record; a share never allows a filter on the column (F3).
/// </summary>
[Collection("xrmd")]
public sealed class FieldSharingTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = "new_fsshare";
    private const string S1 = "new_fsss1", Plain = "new_fssplain";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private Guid _root;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        _root = await RootBusinessUnitAsync(fx);
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity(Table) is not null) return;
            var entity = new EntityMetadata { LogicalName = Table, SchemaName = "new_FsShare", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new StringAttributeMetadata { LogicalName = S1, SchemaName = "new_FsSS1", MaxLength = 100, IsSecured = true },
                new StringAttributeMetadata { LogicalName = Plain, SchemaName = "new_FsSPlain", MaxLength = 100 },
            ]);
            await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        }
        finally
        {
            Lock.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A Basic User (which holds the field share privileges) with every record privilege on the table and, when
    /// <paramref name="permissions"/> is not empty, a profile with those permissions on the table's columns.
    /// </summary>
    private Task<(Guid Id, Guid ObjectId)> UserAsync(params (string Attribute, bool Read, bool Update)[] permissions) =>
        UserAsync(recordPrivileges: true, permissions);

    /// <summary>As above; without <paramref name="recordPrivileges"/>, the user holds no privilege on the table's records.</summary>
    private async Task<(Guid Id, Guid ObjectId)> UserAsync(bool recordPrivileges, params (string Attribute, bool Read, bool Update)[] permissions)
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, _root, oid);
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);
        if (recordPrivileges) await GrantAsync(fx, user, team: false, ("prvCreatenew_FsShare", DepthMask.Global), ("prvReadnew_FsShare", DepthMask.Global),
            ("prvWritenew_FsShare", DepthMask.Global), ("prvAppendnew_FsShare", DepthMask.Global), ("prvDeletenew_FsShare", DepthMask.Global));
        if (permissions.Length == 0) return (user, oid);
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "fss-" + Guid.NewGuid().ToString("N")[..8] });
        foreach (var (attribute, read, update) in permissions)
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
            {
                [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
                [FieldSecurityTables.EntityName] = Table, [FieldSecurityTables.AttributeLogicalName] = attribute,
                [FieldSecurityTables.CanRead] = new OptionSetValue(read ? 4 : 0),
                [FieldSecurityTables.CanCreate] = new OptionSetValue(0),
                [FieldSecurityTables.CanUpdate] = new OptionSetValue(update ? 4 : 0),
            });
        await Admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship(FieldSecurityTables.UserProfilesRelationship),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
        return (user, oid);
    }

    private Task<Guid> RecordAsync(string value = "one") =>
        Admin.CreateAsync(new Entity(Table) { ["new_name"] = "fss-" + Guid.NewGuid().ToString("N")[..10], [S1] = value, [Plain] = "plain" });

    private Guid AttributeId(string attribute) => Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == attribute).MetadataId!.Value;

    /// <summary>A share entity as a client writes it: no <c>objecttypecode</c>, which the service fills from <c>objectid</c>.</summary>
    private Entity Share(Guid record, string attribute, EntityReference principal, bool? read, bool? update)
    {
        var share = new Entity(FieldSecurityTables.Share)
        {
            [FieldSecurityTables.ShareObjectId] = new EntityReference(Table, record),
            [FieldSecurityTables.ShareAttributeId] = AttributeId(attribute),
            [FieldSecurityTables.SharePrincipalId] = principal,
        };
        if (read is { } r) share[FieldSecurityTables.ShareReadAccess] = r;
        if (update is { } u) share[FieldSecurityTables.ShareUpdateAccess] = u;
        return share;
    }

    private static EntityReference User(Guid id) => new("systemuser", id);

    private async Task<string?> ReadAsS1Async(Guid oid, Guid record) =>
        (await ServiceFor(fx, oid).RetrieveAsync(Table, record, new ColumnSet(S1))).GetAttributeValue<string>(S1);

    private async Task<int> ShareRowsAsync(string where, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{FieldSecurityTables.Share} WHERE {where}");
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdFaultException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The call was expected to fail.");
    }

    private static async Task RefusedAsync(Func<Task> act, int code, int status, string because)
    {
        var fault = await FaultAsync(act);
        fault.ErrorCode.Should().Be(code, because);
        fault.HttpStatus.Should().Be(status, because);
    }

    // ---- Review Focus 5 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Field_share_grants_one_record_only()
    {
        var r1 = await RecordAsync();
        var r2 = await RecordAsync();
        var (userB, oidB) = await UserAsync();
        (await ReadAsS1Async(oidB, r1)).Should().BeNull("B has no profile on the column");

        // Created with a plain Create; objecttypecode is filled from objectid, principalidtype from principalid.
        var share = await Admin.CreateAsync(Share(r1, S1, User(userB), read: true, update: null));
        (await ShareRowsAsync("principalobjectattributeaccessid = @id AND objecttypecode = @table AND principalidtype = 8 AND updateaccess = 0",
            ("@id", share), ("@table", Table))).Should().Be(1);
        var stored = await Admin.RetrieveAsync(FieldSecurityTables.Share, share, new ColumnSet(true));
        stored.GetAttributeValue<EntityReference>(FieldSecurityTables.SharePrincipalId).LogicalName.Should().Be("systemuser");

        (await ReadAsS1Async(oidB, r1)).Should().Be("one", "the share grants read on R1");
        (await ReadAsS1Async(oidB, r2)).Should().BeNull("the share is for R1 only");

        await Admin.DeleteAsync(FieldSecurityTables.Share, share);
        (await ReadAsS1Async(oidB, r1)).Should().BeNull("the share was deleted");

        await Admin.CreateAsync(Share(r1, S1, User(userB), read: true, update: false));
        (await ReadAsS1Async(oidB, r1)).Should().Be("one");
        await Admin.DeleteAsync(Table, r1);
        (await ShareRowsAsync("objectid = @record", ("@record", r1))).Should().Be(0, "the shares of a record go with it");
    }

    [Fact]
    public async Task Field_share_through_team()
    {
        var record = await RecordAsync("team-value");
        var (member, oidMember) = await UserAsync();
        var (_, oidOutsider) = await UserAsync();
        var team = await CreateTeamAsync(fx, _root, 1, member);

        await Admin.CreateAsync(Share(record, S1, new EntityReference("team", team), read: true, update: false));
        (await ShareRowsAsync("principalid = @team AND principalidtype = 9", ("@team", team))).Should().Be(1);

        (await ReadAsS1Async(oidMember, record)).Should().Be("team-value", "the share is given to a team the user is a member of");
        (await ReadAsS1Async(oidOutsider, record)).Should().BeNull("the outsider is not a member");
    }

    /// <summary>
    /// A field share never names a system-managed access team (final re-review, note 2): its members are added by
    /// AddUserToRecordTeam, whose caller is checked for the record rights of the template only, so the team must hold nothing
    /// else. The internal caller is exempt (it writes the share the Update below tries to widen).
    /// </summary>
    [Fact]
    public async Task Field_share_to_system_managed_team_is_refused()
    {
        var record = await RecordAsync();
        var team = await CreateTeamAsync(fx, _root, 1);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "UPDATE data.team SET systemmanaged = 1 WHERE teamid = @team");
            cmd.Parameters.AddWithValue("@team", team);
            await cmd.ExecuteNonQueryAsync();
        }
        var principal = new EntityReference("team", team);

        var fault = await FaultAsync(() => Admin.CreateAsync(Share(record, S1, principal, read: true, update: false)));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("system-managed access team");
        (await ShareRowsAsync("principalid = @team", ("@team", team))).Should().Be(0);

        var share = Guid.NewGuid();
        var internalShare = Share(record, S1, principal, read: true, update: false);
        internalShare.Id = share;
        await TestServices.NewPipelineExecutor(fx.App.Services).ExecuteAsync(
            new Microsoft.Xrm.Sdk.Messages.CreateRequest { Target = internalShare },
            new Xrmd.Pipeline.InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()), default);
        (await ShareRowsAsync("principalid = @team", ("@team", team))).Should().Be(1, "the internal caller is exempt");

        fault = await FaultAsync(() => Admin.UpdateAsync(new Entity(FieldSecurityTables.Share, share) { [FieldSecurityTables.ShareUpdateAccess] = true }));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.Message.Should().Contain("system-managed access team");
        (await ShareRowsAsync("principalid = @team AND updateaccess = 0", ("@team", team))).Should().Be(1);
    }

    [Fact]
    public async Task Field_share_update_access_allows_update_on_that_record()
    {
        var r1 = await RecordAsync();
        var r2 = await RecordAsync();
        var (userB, oidB) = await UserAsync();
        var b = ServiceFor(fx, oidB);

        await RefusedAsync(() => b.UpdateAsync(new Entity(Table, r1) { [S1] = "x" }), ErrorCodes.FieldUpdateDenied, 403, "no share yet");
        var share = await Admin.CreateAsync(Share(r1, S1, User(userB), read: false, update: true));

        await b.UpdateAsync(new Entity(Table, r1) { [S1] = "changed" });
        (await Admin.RetrieveAsync(Table, r1, new ColumnSet(S1))).GetAttributeValue<string>(S1).Should().Be("changed");
        await RefusedAsync(() => b.UpdateAsync(new Entity(Table, r2) { [S1] = "x" }), ErrorCodes.FieldUpdateDenied, 403,
            "the share is for R1 only");
        (await ReadAsS1Async(oidB, r1)).Should().BeNull("update access does not grant read");

        // Updating the share to read only takes the update away.
        await Admin.UpdateAsync(new Entity(FieldSecurityTables.Share, share) { [FieldSecurityTables.ShareReadAccess] = true, [FieldSecurityTables.ShareUpdateAccess] = false });
        await RefusedAsync(() => b.UpdateAsync(new Entity(Table, r1) { [S1] = "again" }), ErrorCodes.FieldUpdateDenied, 403,
            "the share no longer grants update");
        (await ReadAsS1Async(oidB, r1)).Should().Be("changed");
    }

    [Fact]
    public async Task Field_share_validation_order()
    {
        var r1 = await RecordAsync();
        var r2 = await RecordAsync();
        var (userB, _) = await UserAsync();
        var principal = User(userB);

        // 1. The column must be secured: checked before the flags (both false here).
        await RefusedAsync(() => Admin.CreateAsync(Share(r1, Plain, principal, read: false, update: false)),
            ErrorCodes.PermissionOnUnsecuredColumn, 400, "new_fssplain is not secured");

        // 2. At least one access: 0x8004F509 on create (missing flags count as false).
        await RefusedAsync(() => Admin.CreateAsync(Share(r1, S1, principal, read: false, update: false)),
            ErrorCodes.ShareWithoutAccess, 400, "the share grants nothing");
        await RefusedAsync(() => Admin.CreateAsync(Share(r1, S1, principal, read: null, update: null)),
            ErrorCodes.ShareWithoutAccess, 400, "the share grants nothing");

        var share = await Admin.CreateAsync(Share(r1, S1, principal, read: true, update: false));
        await RefusedAsync(() => Admin.UpdateAsync(new Entity(FieldSecurityTables.Share, share) { [FieldSecurityTables.ShareReadAccess] = false }),
            ErrorCodes.ShareUpdateWithoutAccess, 400, "the update leaves both false (update access stored false)");

        // 3. Duplicate (record, column, principal), after the flags check.
        await RefusedAsync(() => Admin.CreateAsync(Share(r1, S1, principal, read: false, update: false)),
            ErrorCodes.ShareWithoutAccess, 400, "the flags are checked before the duplicate");
        await RefusedAsync(() => Admin.CreateAsync(Share(r1, S1, principal, read: false, update: true)),
            ErrorCodes.ShareDuplicate, 400, "R1's column is already shared with B");

        // 4. The sharer must be able to update the column on the record, checked last.
        var (_, oidA) = await UserAsync((S1, true, false));
        var a = ServiceFor(fx, oidA);
        await RefusedAsync(() => a.CreateAsync(Share(r1, S1, principal, read: true, update: false)),
            ErrorCodes.ShareDuplicate, 400, "the duplicate is reported before the sharer's permission");
        await RefusedAsync(() => a.CreateAsync(Share(r2, S1, principal, read: true, update: false)),
            ErrorCodes.ShareWithoutUpdate, 403, "A reads the column but cannot update it");
        await RefusedAsync(() => a.UpdateAsync(new Entity(FieldSecurityTables.Share, share) { [FieldSecurityTables.ShareUpdateAccess] = true }),
            ErrorCodes.ShareWithoutUpdate, 403, "A cannot change a share of a column it cannot update");
        await RefusedAsync(() => a.DeleteAsync(FieldSecurityTables.Share, share),
            ErrorCodes.ShareWithoutUpdate, 403, "A cannot remove a share of a column it cannot update");
        (await ShareRowsAsync("objectid = @record", ("@record", r2))).Should().Be(0);

        // A user who may update (and read) the column, by profile or by a share on the record, may share it.
        var (_, oidEditor) = await UserAsync((S1, true, true));
        await ServiceFor(fx, oidEditor).CreateAsync(Share(r2, S1, principal, read: true, update: false));
        var (userC, oidC) = await UserAsync();
        await Admin.CreateAsync(Share(r2, S1, User(userC), read: false, update: true));
        var (userD, _) = await UserAsync();
        await ServiceFor(fx, oidC).CreateAsync(Share(r2, S1, User(userD), read: false, update: true));
        await RefusedAsync(() => ServiceFor(fx, oidC).CreateAsync(Share(r1, S1, User(userD), read: false, update: true)),
            ErrorCodes.ShareWithoutUpdate, 403, "C's update share is on R2 only");

        // objecttypecode, when given, must name objectid's table.
        var mismatched = Share(r1, S1, User(userD), read: true, update: false);
        mismatched[FieldSecurityTables.ShareObjectTypeCodeColumn] = "account";
        (await FaultAsync(() => Admin.CreateAsync(mismatched))).HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Filter_is_refused_even_with_a_share()
    {
        var record = await RecordAsync("filtered");
        var (userB, oidB) = await UserAsync();
        await Admin.CreateAsync(Share(record, S1, User(userB), read: true, update: true));
        var b = ServiceFor(fx, oidB);
        (await ReadAsS1Async(oidB, record)).Should().Be("filtered");

        var filtered = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1) };
        filtered.Criteria.AddCondition(S1, ConditionOperator.Equal, "filtered");
        await RefusedAsync(() => b.RetrieveMultipleAsync(filtered), ErrorCodes.FieldReadDenied, 403, "a share does not allow a filter (F3)");

        var ordered = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1) };
        ordered.AddOrder(S1, OrderType.Ascending);
        await RefusedAsync(() => b.RetrieveMultipleAsync(ordered), ErrorCodes.FieldReadDenied, 403, "nor an order");
    }

    [Fact]
    public async Task Update_only_sharer_cannot_grant_read()
    {
        var record = await RecordAsync("hidden");
        var (userE, oidE) = await UserAsync((S1, false, true)); // profile: update, no read
        var (userC, oidC) = await UserAsync();                  // an update-only share on the record
        await Admin.CreateAsync(Share(record, S1, User(userC), read: false, update: true));
        var (userD, _) = await UserAsync();
        var team = await CreateTeamAsync(fx, _root, 1, userE);
        var teamC = await CreateTeamAsync(fx, _root, 1, userC);

        // C already holds a share on the record, so its "self" is a team of its own (a share to C would be a duplicate).
        foreach (var (oid, self) in new[] { (oidE, User(userE)), (oidC, new EntityReference("team", teamC)) })
        {
            var sharer = ServiceFor(fx, oid);
            await RefusedAsync(() => sharer.CreateAsync(Share(record, S1, self, read: true, update: false)),
                ErrorCodes.ShareWithoutUpdate, 403, "a sharer that cannot read the column cannot grant read, to itself");
            await RefusedAsync(() => sharer.CreateAsync(Share(record, S1, User(userD), read: true, update: true)),
                ErrorCodes.ShareWithoutUpdate, 403, "nor to anyone else");
            (await ReadAsS1Async(oid, record)).Should().BeNull();
        }
        await RefusedAsync(() => ServiceFor(fx, oidE).CreateAsync(Share(record, S1, new EntityReference("team", team), read: true, update: false)),
            ErrorCodes.ShareWithoutUpdate, 403, "nor to its team");

        // Update-only shares are what they can give; turning one into a read share is refused.
        var given = await ServiceFor(fx, oidE).CreateAsync(Share(record, S1, User(userD), read: false, update: true));
        await RefusedAsync(() => ServiceFor(fx, oidC).UpdateAsync(new Entity(FieldSecurityTables.Share, given) { [FieldSecurityTables.ShareReadAccess] = true }),
            ErrorCodes.ShareWithoutUpdate, 403, "the update would grant read");
        (await ShareRowsAsync("principalobjectattributeaccessid = @id AND readaccess = 0", ("@id", given))).Should().Be(1);

        // Revoking needs the same rights as granting (R6d): C cannot delete a read share, but can delete an update-only one.
        var readShare = await Admin.CreateAsync(Share(record, S1, User(userE), read: true, update: false));
        await RefusedAsync(() => ServiceFor(fx, oidC).DeleteAsync(FieldSecurityTables.Share, readShare),
            ErrorCodes.ShareWithoutUpdate, 403, "C cannot read the column");
        await ServiceFor(fx, oidC).DeleteAsync(FieldSecurityTables.Share, given);
        (await ShareRowsAsync("principalobjectattributeaccessid IN (@a, @b)", ("@a", given), ("@b", readShare))).Should().Be(1);
    }

    [Fact]
    public async Task Sharer_needs_read_on_the_record()
    {
        var record = await RecordAsync();
        var (userB, _) = await UserAsync();
        var share = await Admin.CreateAsync(Share(record, S1, User(userB), read: true, update: false));

        // Full column permissions, but no privilege on the table's records.
        var (_, oidX) = await UserAsync(recordPrivileges: false, (S1, true, true));
        var x = ServiceFor(fx, oidX);
        var (userD, _) = await UserAsync();

        async Task DeniedAsync(Func<Task> act, string because)
        {
            var fault = await FaultAsync(act);
            fault.HttpStatus.Should().Be(403, because);
            fault.ErrorCode.Should().NotBe(ErrorCodes.ShareWithoutUpdate, because);
        }
        await DeniedAsync(() => x.CreateAsync(Share(record, S1, User(userD), read: true, update: false)), "create");
        await DeniedAsync(() => x.UpdateAsync(new Entity(FieldSecurityTables.Share, share) { [FieldSecurityTables.ShareUpdateAccess] = true }), "update");
        await DeniedAsync(() => x.DeleteAsync(FieldSecurityTables.Share, share), "delete");
        (await ShareRowsAsync("objectid = @record", ("@record", record))).Should().Be(1, "nothing was written, changed or deleted");
        (await ShareRowsAsync("principalobjectattributeaccessid = @id AND updateaccess = 0", ("@id", share))).Should().Be(1);
    }

    [Fact]
    public async Task Share_to_a_missing_principal_is_404()
    {
        var record = await RecordAsync();
        var fault = await FaultAsync(() => Admin.CreateAsync(Share(record, S1, User(Guid.NewGuid()), read: true, update: false)));
        fault.HttpStatus.Should().Be(404);
        (await ShareRowsAsync("objectid = @record", ("@record", record))).Should().Be(0);
    }

    [Fact]
    public async Task Unique_index_holds_one_share_per_record_column_and_principal()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        (await TestSql.IndexAsync(fx.ConnectionString, "data", "principalobjectattributeaccess", FieldSecurityTables.ShareUniqueIndex))!.Unique.Should().BeTrue();

        var record = await RecordAsync();
        var (userB, _) = await UserAsync();
        await Admin.CreateAsync(Share(record, S1, User(userB), read: true, update: false));
        await using var insert = TestSql.Command(conn, $"""
            INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objecttypecode, attributeid,
                principalid, principalidtype, readaccess, updateaccess)
            VALUES (NEWID(), @record, @table, @attribute, @principal, 8, 0, 1)
            """);
        insert.Parameters.AddWithValue("@record", record);
        insert.Parameters.AddWithValue("@table", Table);
        insert.Parameters.AddWithValue("@attribute", AttributeId(S1));
        insert.Parameters.AddWithValue("@principal", userB);
        var act = () => insert.ExecuteNonQueryAsync();
        TestSql.Dialect.Classify((await act.Should().ThrowAsync<System.Data.Common.DbException>()).Which).Should().Be(DbErrorKind.UniqueViolation);
    }
}
