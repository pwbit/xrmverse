using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <see cref="FieldSecurity"/> (plan 14, task 3, rulings F1, F2 and F10): who bypasses field security, the merge of the
/// profiles of a user and of its teams, the invalidation of the cached permissions by profile, membership and metadata
/// changes, and the field shares that apply to a caller.
/// </summary>
[Collection("xrmd")]
public sealed class FieldSecurityServiceTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = "fs_svc";
    private const string Alpha = "fs_alpha", Beta = "fs_beta", Gamma = "fs_gamma", Delta = "fs_delta";
    private const string UserProfiles = "systemuserprofiles_association", TeamProfiles = "teamprofiles_association";
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private Guid _root;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private FieldSecurity Fields => fx.App.Services.GetRequiredService<FieldSecurity>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        _root = await RootBusinessUnitAsync(fx);
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity(Table) is not null) return;
            // fs_alpha and fs_beta are secured, fs_gamma and fs_delta are not.
            var entity = new EntityMetadata { LogicalName = Table, SchemaName = "fs_Svc", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new StringAttributeMetadata { LogicalName = Alpha, SchemaName = "fs_Alpha", MaxLength = 100, IsSecured = true },
                new StringAttributeMetadata { LogicalName = Beta, SchemaName = "fs_Beta", MaxLength = 100, IsSecured = true },
                new StringAttributeMetadata { LogicalName = Gamma, SchemaName = "fs_Gamma", MaxLength = 100 },
                new StringAttributeMetadata { LogicalName = Delta, SchemaName = "fs_Delta", MaxLength = 100 },
            ]);
            await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "fs_name", SchemaName = "fs_Name", MaxLength = 100 });
        }
        finally
        {
            Lock.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Caller CallerOf(Guid user, Guid? impersonator = null) => new(user, _root, impersonator);

    private Task<Guid> NewProfileAsync() =>
        Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "fss-" + Guid.NewGuid().ToString("N")[..8] });

    /// <summary>A permission of <paramref name="profile"/> on <paramref name="attribute"/>; a null right is left unset (NULL).</summary>
    private Task<Guid> PermitAsync(Guid profile, string attribute, bool? read = null, bool? create = null, bool? update = null)
    {
        var permission = new Entity(FieldSecurityTables.Permission)
        {
            [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
            [FieldSecurityTables.EntityName] = Table, [FieldSecurityTables.AttributeLogicalName] = attribute,
        };
        if (read is { } r) permission[FieldSecurityTables.CanRead] = new OptionSetValue(r ? 4 : 0);
        if (create is { } c) permission[FieldSecurityTables.CanCreate] = new OptionSetValue(c ? 4 : 0);
        if (update is { } u) permission[FieldSecurityTables.CanUpdate] = new OptionSetValue(u ? 4 : 0);
        return Admin.CreateAsync(permission);
    }

    private static AssociateRequest Associate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private static DisassociateRequest Disassociate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private async Task<Guid> AdminUserIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<T> InSessionAsync<T>(Func<DataSession, Task<T>> read)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await read(new DataSession(conn, null));
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Administrator_and_internal_bypass_field_security()
    {
        var admin = await AdminUserIdAsync();
        var system = await Cache(fx).SystemUserIdAsync(default);
        var user = await CreateUserAsync(fx, _root);

        (await Fields.ForAsync(Caller.Internal, Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);
        (await Fields.ForAsync(CallerOf(system), Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);
        (await Fields.ForAsync(CallerOf(admin), Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);
        FieldPermissionSet.Unrestricted.CanRead(Alpha).Should().BeTrue();
        FieldPermissionSet.Unrestricted.CanCreate(Alpha).Should().BeTrue();
        FieldPermissionSet.Unrestricted.CanUpdate(Alpha).Should().BeTrue();
        FieldPermissionSet.Unrestricted.UnreadableSecured.Should().BeEmpty();

        // A System Administrator through an owner team bypasses too (SecurityProfile.IsSystemAdministrator).
        var viaTeam = await CreateUserAsync(fx, _root);
        await AssignRoleAsync(fx, await CreateTeamAsync(fx, _root, 0, viaTeam), StandardRoles.SystemAdministrator, team: true);
        (await Fields.ForAsync(CallerOf(viaTeam), Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);

        // Any other user is checked: with no profile, no secured column is allowed, an unsecured one always is.
        var set = await Fields.ForAsync(CallerOf(user), Table, default);
        set.Should().NotBeSameAs(FieldPermissionSet.Unrestricted);
        set.CanRead(Alpha).Should().BeFalse();
        set.CanCreate(Beta).Should().BeFalse();
        set.CanUpdate(Beta).Should().BeFalse();
        set.CanRead(Gamma).Should().BeTrue();
        set.CanUpdate("fs_name").Should().BeTrue();
        set.UnreadableSecured.Should().BeEquivalentTo([Alpha, Beta]);

        // Impersonation, as SecurityGuard: the caller bypasses only when the user and the impersonator both do.
        (await Fields.ForAsync(CallerOf(user, impersonator: admin), Table, default)).CanRead(Alpha).Should().BeFalse("the user is restricted");
        var plainAsAdmin = await Fields.ForAsync(CallerOf(admin, impersonator: user), Table, default);
        plainAsAdmin.Should().NotBeSameAs(FieldPermissionSet.Unrestricted, "the impersonator is restricted");
        plainAsAdmin.CanRead(Alpha).Should().BeFalse();
        plainAsAdmin.CanRead(Gamma).Should().BeTrue();
        (await Fields.ForAsync(CallerOf(admin, impersonator: viaTeam), Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);
        // A SYSTEM impersonator is not a principal (SecurityCache.PrincipalsAsync): the user decides.
        (await Fields.ForAsync(CallerOf(admin, impersonator: system), Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);
        (await Fields.ForAsync(CallerOf(user, impersonator: system), Table, default)).CanRead(Alpha).Should().BeFalse();

        Fields.IsSecured(Table, Alpha).Should().BeTrue();
        Fields.IsSecured(Table, Gamma).Should().BeFalse();
        Fields.IsSecured(Table, "fs_missing").Should().BeFalse();
        Fields.IsSecured("fs_no_such_table", Alpha).Should().BeFalse();
    }

    [Fact]
    public async Task Permissions_merge_user_and_team_profiles_with_or()
    {
        var user = await CreateUserAsync(fx, _root);
        var accessTeam = await CreateTeamAsync(fx, _root, 1, user);

        var readProfile = await NewProfileAsync();
        await PermitAsync(readProfile, Alpha, read: true, update: false); // cancreate stays NULL: Not Allowed
        var updateProfile = await NewProfileAsync();
        await PermitAsync(updateProfile, Alpha, read: false, create: false, update: true);
        await PermitAsync(updateProfile, Beta, create: true);

        await Admin.ExecuteAsync(Associate(new EntityReference("systemuser", user), UserProfiles,
            new EntityReference(FieldSecurityTables.Profile, readProfile)));
        await Admin.ExecuteAsync(Associate(new EntityReference(FieldSecurityTables.Profile, updateProfile), TeamProfiles,
            new EntityReference("team", accessTeam)));

        var set = await Fields.ForAsync(CallerOf(user), Table, default);
        set.CanRead(Alpha).Should().BeTrue();
        set.CanUpdate(Alpha).Should().BeTrue();
        set.CanCreate(Alpha).Should().BeFalse();
        set.CanRead(Beta).Should().BeFalse();
        set.CanCreate(Beta).Should().BeTrue();
        set.CanUpdate(Beta).Should().BeFalse();
        set.UnreadableSecured.Should().BeEquivalentTo([Beta]);
    }

    [Fact]
    public async Task Impersonation_ands_the_permissions_of_the_user_and_the_impersonator()
    {
        var user = await CreateUserAsync(fx, _root);
        var impersonator = await CreateUserAsync(fx, _root);
        var userProfile = await NewProfileAsync();
        await PermitAsync(userProfile, Alpha, read: true, create: true);
        await PermitAsync(userProfile, Beta, update: true);
        var impersonatorProfile = await NewProfileAsync();
        await PermitAsync(impersonatorProfile, Alpha, read: true);
        await PermitAsync(impersonatorProfile, Beta, read: true, update: true);
        await Admin.ExecuteAsync(Associate(new EntityReference("systemuser", user), UserProfiles,
            new EntityReference(FieldSecurityTables.Profile, userProfile)));
        await Admin.ExecuteAsync(Associate(new EntityReference("systemuser", impersonator), UserProfiles,
            new EntityReference(FieldSecurityTables.Profile, impersonatorProfile)));

        var set = await Fields.ForAsync(CallerOf(user, impersonator), Table, default);
        set.CanRead(Alpha).Should().BeTrue("both may read it");
        set.CanCreate(Alpha).Should().BeFalse("only the user may create it");
        set.CanRead(Beta).Should().BeFalse("only the impersonator may read it");
        set.CanUpdate(Beta).Should().BeTrue("both may update it");
        set.CanRead(Gamma).Should().BeTrue();
        set.UnreadableSecured.Should().BeEquivalentTo([Beta]);

        // Each alone keeps its own set.
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanCreate(Alpha).Should().BeTrue();
        (await Fields.ForAsync(CallerOf(impersonator), Table, default)).CanRead(Beta).Should().BeTrue();
    }

    [Fact]
    public async Task Gaining_and_losing_the_system_administrator_role_changes_the_bypass()
    {
        var user = await CreateUserAsync(fx, _root);
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Alpha).Should().BeFalse();

        await AssignRoleAsync(fx, user, StandardRoles.SystemAdministrator);
        (await Fields.ForAsync(CallerOf(user), Table, default)).Should().BeSameAs(FieldPermissionSet.Unrestricted);

        var administration = fx.App.Services.GetRequiredService<SecurityAdministration>();
        await TestEntities.InSessionAsync(fx.ConnectionString,
            s => administration.RemoveRoleAsync(s, new EntityReference("systemuser", user), StandardRoles.SystemAdministrator, default));
        var set = await Fields.ForAsync(CallerOf(user), Table, default);
        set.Should().NotBeSameAs(FieldPermissionSet.Unrestricted);
        set.CanRead(Alpha).Should().BeFalse();
    }

    [Fact]
    public async Task Team_profile_grants_and_revokes_without_restart()
    {
        var user = await CreateUserAsync(fx, _root);
        var team = await CreateTeamAsync(fx, _root, 0, user);
        var profile = new EntityReference(FieldSecurityTables.Profile, await NewProfileAsync());
        await PermitAsync(profile.Id, Beta, read: true);
        var teamRef = new EntityReference("team", team);

        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Beta).Should().BeFalse();

        await Admin.ExecuteAsync(Associate(teamRef, TeamProfiles, profile));
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Beta).Should().BeTrue("the team's new profile applies at once");

        await Admin.ExecuteAsync(Disassociate(teamRef, TeamProfiles, profile));
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Beta).Should().BeFalse("the removed profile no longer applies");

        await Admin.ExecuteAsync(Associate(teamRef, TeamProfiles, profile));
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Beta).Should().BeTrue();

        // Removing the user from the team takes the team's profiles away.
        var administration = fx.App.Services.GetRequiredService<SecurityAdministration>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => administration.RemoveMemberAsync(s, team, user, default));
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Beta).Should().BeFalse("the user left the team");
    }

    [Fact]
    public async Task Securing_a_column_invalidates_the_cache()
    {
        var user = await CreateUserAsync(fx, _root);
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Delta).Should().BeTrue("the column is not secured");
        Fields.IsSecured(Table, Delta).Should().BeFalse();

        try
        {
            await Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = Table, Attribute = new StringAttributeMetadata { LogicalName = Delta, IsSecured = true },
            });

            Fields.IsSecured(Table, Delta).Should().BeTrue();
            var set = await Fields.ForAsync(CallerOf(user), Table, default);
            set.CanRead(Delta).Should().BeFalse("the column is secured and the user has no profile");
            set.UnreadableSecured.Should().Contain(Delta);
        }
        finally
        {
            await Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = Table, Attribute = new StringAttributeMetadata { LogicalName = Delta, IsSecured = false },
            });
        }

        Fields.IsSecured(Table, Delta).Should().BeFalse();
        (await Fields.ForAsync(CallerOf(user), Table, default)).CanRead(Delta).Should().BeTrue("unsecured again");
    }

    [Fact]
    public async Task Deleting_a_secured_column_drops_it_from_the_secured_columns()
    {
        const string Temp = "fs_temp";
        var user = await CreateUserAsync(fx, _root);
        // Through the store (the messages demand a publisher prefix); the store notifies after the commit either way.
        await Store.AddAttributeAsync(Table, new StringAttributeMetadata { LogicalName = Temp, SchemaName = "fs_Temp", MaxLength = 20, IsSecured = true });
        try
        {
            Fields.IsSecured(Table, Temp).Should().BeTrue();
            (await Fields.ForAsync(CallerOf(user), Table, default)).Secured.Should().Contain(Temp);
        }
        finally
        {
            await TestEntities.InSessionAsync(fx.ConnectionString, session => Store.DeleteAttributeAsync(session, Table, Temp, default));
        }

        Fields.IsSecured(Table, Temp).Should().BeFalse("the column is gone");
        var set = await Fields.ForAsync(CallerOf(user), Table, default);
        set.Secured.Should().NotContain(Temp);
        set.UnreadableSecured.Should().BeEquivalentTo([Alpha, Beta]);
    }

    [Fact]
    public async Task Shares_apply_to_the_user_and_its_teams_merged_with_or()
    {
        var user = await CreateUserAsync(fx, _root);
        var other = await CreateUserAsync(fx, _root);
        var team = await CreateTeamAsync(fx, _root, 1, user);
        Guid recordA = Guid.NewGuid(), recordB = Guid.NewGuid(), recordC = Guid.NewGuid();
        var attributes = Store.GetEntity(Table)!.Attributes.ToDictionary(a => a.LogicalName, a => a.MetadataId!.Value);

        async Task ShareAsync(Guid record, string attribute, Guid principal, bool isTeam, bool read, bool update)
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, $"""
                INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                    attributeid, principalid, principalidtype, readaccess, updateaccess)
                VALUES (NEWID(), @record, @otc, @table, @attribute, @principal, @ptype, @read, @update)
                """);
            cmd.Parameters.AddWithValue("@record", record);
            cmd.Parameters.AddWithValue("@otc", Store.GetEntity(Table)!.ObjectTypeCode!.Value);
            cmd.Parameters.AddWithValue("@table", Table);
            cmd.Parameters.AddWithValue("@attribute", attributes[attribute]);
            cmd.Parameters.AddWithValue("@principal", principal);
            cmd.Parameters.AddWithValue("@ptype", isTeam ? 9 : 8);
            cmd.Parameters.AddWithValue("@read", read);
            cmd.Parameters.AddWithValue("@update", update);
            await cmd.ExecuteNonQueryAsync();
        }

        await ShareAsync(recordA, Alpha, user, isTeam: false, read: true, update: false);
        await ShareAsync(recordA, Alpha, team, isTeam: true, read: false, update: true); // OR with the user's row
        await ShareAsync(recordA, Beta, team, isTeam: true, read: true, update: false);
        await ShareAsync(recordB, Beta, user, isTeam: false, read: false, update: true);
        await ShareAsync(recordB, Alpha, user, isTeam: false, read: true, update: true); // asked for here, not in the second call
        await ShareAsync(recordC, Alpha, other, isTeam: false, read: true, update: true); // someone else's share
        Cache(fx).Invalidate();

        var shares = await InSessionAsync(s => Fields.SharesAsync(CallerOf(user), Table, [recordA, recordB, recordC], [Alpha, Beta, Gamma], s, default));

        shares.Keys.Should().BeEquivalentTo([recordA, recordB]);
        shares[recordA].Read.Should().BeEquivalentTo([Alpha, Beta]);
        shares[recordA].Update.Should().BeEquivalentTo([Alpha]);
        shares[recordB].Read.Should().BeEquivalentTo([Alpha]);
        shares[recordB].Update.Should().BeEquivalentTo([Alpha, Beta]);

        var onlyBeta = await InSessionAsync(s => Fields.SharesAsync(CallerOf(user), Table, [recordB], [Beta], s, default));
        onlyBeta[recordB].Read.Should().BeEmpty();
        onlyBeta[recordB].Update.Should().BeEquivalentTo([Beta]);

        (await InSessionAsync(s => Fields.SharesAsync(CallerOf(other), Table, [recordA], [Alpha], s, default))).Should().BeEmpty();
        (await InSessionAsync(s => Fields.SharesAsync(CallerOf(user), Table, [], [Alpha], s, default))).Should().BeEmpty();

        // A share given to an owner team applies to its members.
        var ownerTeam = await CreateTeamAsync(fx, _root, 0, other);
        await ShareAsync(recordC, Beta, ownerTeam, isTeam: true, read: true, update: false);
        Cache(fx).Invalidate();
        var viaOwnerTeam = await InSessionAsync(s => Fields.SharesAsync(CallerOf(other), Table, [recordC], [Alpha, Beta], s, default));
        viaOwnerTeam[recordC].Read.Should().BeEquivalentTo([Alpha, Beta]);
        viaOwnerTeam[recordC].Update.Should().BeEquivalentTo([Alpha]);

        // Impersonation: a column is granted on a record only when both principals reach it there (by a share or a profile).
        var both = await InSessionAsync(s => Fields.SharesAsync(CallerOf(user, other), Table, [recordA, recordB, recordC], [Alpha, Beta], s, default));
        both.Should().BeEmpty("no record is shared with both");
        var profile = await NewProfileAsync();
        await PermitAsync(profile, Alpha, read: true);
        await Admin.ExecuteAsync(Associate(new EntityReference("systemuser", other), UserProfiles,
            new EntityReference(FieldSecurityTables.Profile, profile)));
        both = await InSessionAsync(s => Fields.SharesAsync(CallerOf(user, other), Table, [recordA, recordB, recordC], [Alpha, Beta], s, default));
        both.Keys.Should().BeEquivalentTo([recordA, recordB], "the impersonator reads fs_alpha everywhere through its profile");
        both[recordA].Read.Should().BeEquivalentTo([Alpha]);
        both[recordA].Update.Should().BeEmpty();
        both[recordB].Read.Should().BeEquivalentTo([Alpha]);

        // A column the caller's profiles already allow is not a share grant.
        var otherAlone = await InSessionAsync(s => Fields.SharesAsync(CallerOf(other), Table, [recordC], [Alpha, Beta], s, default));
        otherAlone[recordC].Read.Should().BeEquivalentTo([Beta]);
        otherAlone[recordC].Update.Should().BeEquivalentTo([Alpha]);
    }
}
