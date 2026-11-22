using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <param name="OrgName">Organization name; also the name of the root business unit.</param>
/// <param name="RootBusinessUnitId">Id for the root business unit; ignored when a root business unit already exists.</param>
/// <param name="AdminObjectId">Entra object id (<c>oid</c>) of the administrator.</param>
/// <param name="AdminName">Full name of the administrator.</param>
public sealed record SeedOptions(string OrgName, Guid RootBusinessUnitId, Guid AdminObjectId, string AdminName)
{
    /// <summary>
    /// The base currency given by <c>Seed:BaseCurrency*</c> (ruling X1), or null when the settings are not given: an organization
    /// without a base currency then gets <see cref="BaseCurrencySeed.Default"/> (the euro). An organization that has one keeps it,
    /// and only a given setting that names another code is warned about.
    /// </summary>
    public BaseCurrencySeed? BaseCurrency { get; init; }

    /// <summary>Stable id derived from the organization name, so a restart without explicit configuration reuses it.</summary>
    public static Guid DeriveRootBusinessUnitId(string orgName) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:rootbusinessunit:" + orgName)));

    /// <summary>Stable id of the built-in SYSTEM user of the organization.</summary>
    public static Guid DeriveSystemUserId(string orgName) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:systemuser:" + orgName)));
}

/// <summary>
/// The base currency of a new organization (ruling X1): an ISO 4217 code (<see cref="IsoCurrencyCodes"/>), a name, a symbol and a
/// precision of 0 to 4 (decision D2).
/// </summary>
/// <param name="IsoCode">The ISO 4217 code (<c>Seed:BaseCurrency</c>).</param>
/// <param name="Name">The currency name (<c>Seed:BaseCurrencyName</c>, at most 100 characters).</param>
/// <param name="Symbol">The currency symbol (<c>Seed:BaseCurrencySymbol</c>, at most 5 characters: <c>organization.basecurrencysymbol</c> holds it).</param>
/// <param name="Precision">The currency precision (<c>Seed:BaseCurrencyPrecision</c>).</param>
public sealed record BaseCurrencySeed(string IsoCode, string Name, string Symbol, int Precision)
{
    /// <summary>The configuration key of the code; the others are this key followed by <c>Name</c>, <c>Symbol</c> and <c>Precision</c>.</summary>
    public const string SettingName = "Seed:BaseCurrency";

    /// <summary>The longest symbol: the length of <c>organization.basecurrencysymbol</c>, which copies it.</summary>
    public const int MaxSymbolLength = 5;

    /// <summary>The longest name: the length of <c>transactioncurrency.currencyname</c>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The euro, the default base currency.</summary>
    public static BaseCurrencySeed Default { get; } = new("EUR", "Euro", "€", 2);

    /// <summary>
    /// The base currency of the settings: no code means <see cref="Default"/>'s; the code is trimmed and upper-cased; a missing name
    /// or symbol is the euro's for <c>EUR</c>, else the code; a missing precision is 2. Checked with <see cref="Validate"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is invalid; the message names it.</exception>
    public static BaseCurrencySeed From(string? iso, string? name, string? symbol, int? precision)
    {
        var code = string.IsNullOrWhiteSpace(iso) ? Default.IsoCode : iso.Trim().ToUpperInvariant();
        var euro = code == Default.IsoCode;
        var seed = new BaseCurrencySeed(code,
            string.IsNullOrWhiteSpace(name) ? euro ? Default.Name : code : name.Trim(),
            string.IsNullOrWhiteSpace(symbol) ? euro ? Default.Symbol : code : symbol.Trim(),
            precision ?? Default.Precision);
        seed.Validate();
        return seed;
    }

    /// <summary>Checks the code against ISO 4217, the precision against 0 to 4, and the lengths of the name and symbol.</summary>
    /// <exception cref="InvalidOperationException">A value is invalid; the message names its setting.</exception>
    public void Validate()
    {
        if (!IsoCurrencyCodes.Contains(IsoCode))
            throw new InvalidOperationException(
                $"{SettingName} '{IsoCode}' is not an ISO 4217 currency code (for example EUR or USD); set a valid code and restart.");
        if (Precision is < CurrencySchema.MinPrecision or > CurrencySchema.MaxPrecision)
            throw new InvalidOperationException(
                $"{SettingName}Precision {Precision} is outside {CurrencySchema.MinPrecision}..{CurrencySchema.MaxPrecision}.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
            throw new InvalidOperationException($"{SettingName}Name must have 1 to {MaxNameLength} characters.");
        if (string.IsNullOrWhiteSpace(Symbol) || Symbol.Length > MaxSymbolLength)
            throw new InvalidOperationException($"{SettingName}Symbol must have 1 to {MaxSymbolLength} characters.");
    }
}

/// <summary>
/// Creates the system entities (organization, businessunit, systemuser) from the embedded seed definitions and
/// their initial rows: the organization, the root business unit and the administrator. Idempotent: every step first
/// checks for existing metadata/rows, so a restart changes nothing.
/// </summary>
public sealed class SystemSeed(IMetadataStore store, IDbConnectionFactory factory, ILogger<SystemSeed>? logger = null)
{
    /// <summary>The <c>meta.upgrade_step</c> name of the one-time <c>usersettings.paginglimit</c> backfill (#110; never renamed).</summary>
    public const string PagingLimitDefaultStep = "usersettings-paginglimit-default";

    /// <summary>The <c>meta.upgrade_step</c> name of the one-time color scheme default (UI-2a) of the settings rows made before the column.</summary>
    public const string ColorSchemeDefaultStep = "usersettings-colorscheme-default";

    /// <summary>
    /// The <c>meta.upgrade_step</c> name of the rebuild of a 2.7a PostgreSQL organization's text keys (plan 2.7b, ruling Q3, #299;
    /// never renamed): recorded once every unique key on a text column compares text keys (<see cref="TextKeys"/>).
    /// </summary>
    public const string TextKeysStep = "pg-text-keys-299";

    private const string ResourcePrefix = "Xrmd.Metadata.Seed.";

    // Referenced entities first, though lookup targets are not validated.
    private static readonly string[] EntityOrder =
    [
        // The currencies before organization (basecurrencyid) and usersettings (transactioncurrencyid, keyed by systemuserid).
        "businessunit", "systemuser", TimeZoneSchema.Table, CurrencySchema.Table, CurrencySchema.UserSettings, AuditTable.Name, "organization",
        // The processes (plan 2.14) after sdkmessage (an action's message), asyncoperation after workflow (Workflow_AsyncOperations).
        "pluginassembly", "plugintype", "sdkmessage", WorkflowSchema.Table, WorkflowSchema.TriggerTable, "sdkmessagefilter", ServiceEndpointSchema.Table,
        "sdkmessageprocessingstep",
        "sdkmessageprocessingstepimage", CustomApiTables.Api, CustomApiTables.RequestParameter, CustomApiTables.ResponseProperty,
        "asyncoperation", DuplicateDetectionSchema.Rule, DuplicateDetectionSchema.Condition, DuplicateDetectionSchema.Record, "plugintracelog",
        AccessTeamSchema.TeamTemplate, "team", "role", "privilege", "privilegeobjecttypecodes", "roleprivileges", "systemuserroles", "teamroles",
        FieldSecurityTables.Profile, FieldSecurityTables.Permission, FieldSecurityTables.UserProfiles, FieldSecurityTables.TeamProfiles,
        FieldSecurityTables.Share,
        "teammembership", "principalobjectaccess",
        "fileattachment", "publisher", "solution", "solutioncomponent", "importjob", "savedquery", "systemform", "webresource",
        // The system charts (plan 3.5 UI-1f) after the views, and after the web resources their webresourceid names.
        ChartSchema.Table,
        "sitemap", "appmodule",
        AppModuleSchema.Component, AppModuleSchema.RolesIntersect, AppModuleSchema.UserQuery, AppModuleSchema.UserEntityUiSettings,
        // The app settings (#129): the definitions before the values that name them.
        AppModuleSchema.SettingDefinition, AppModuleSchema.AppSetting, AppModuleSchema.OrganizationSetting,
        // The table and column maps (#143), the entity map before its attribute maps.
        AttributeMapSchema.EntityMap, AttributeMapSchema.AttributeMap,
        // The environment variables (#167), the definition before its value.
        EnvironmentVariableSchema.Definition, EnvironmentVariableSchema.Value,
        // The custom controls (plan 3.5 UI-1d).
        CustomControlSchema.Table,
        // The knowledge tables (D3), parents first.
        KnowledgeSchema.Source, KnowledgeSchema.SourceEntity, KnowledgeSchema.SourceAttribute, KnowledgeSchema.Synonyms, KnowledgeSchema.GlossaryTerm,
        // The business tables: account and contact point at each other, which the seed path does not check.
        "account", "contact",
        // The activity model (plan 2.11): the pointer before the party rows that name it, then the four activities and notes.
        ActivitySchema.Pointer, ActivitySchema.Party, ActivitySchema.Email, ActivitySchema.Task, ActivitySchema.PhoneCall,
        ActivitySchema.Appointment, ActivitySchema.Annotation,
        // The timeline posts (plan 3.5 UI-1g): postregarding before the posts that name it, the posts before their comments.
        PostSchema.Regarding, PostSchema.Post, PostSchema.Comment,
        // The queues (plan 4.1a): the queue before the items and memberships that name it.
        QueueSchema.Queue, QueueSchema.QueueItem, QueueSchema.QueueMembership,
    ];

    /// <summary>The seeded tables a custom table must not take the name of (an object type code of 10000 or more).</summary>
    private static readonly string[] BusinessTables =
    [
        "account", "contact", ActivitySchema.Pointer, ActivitySchema.Party, ActivitySchema.Email, ActivitySchema.Task,
        ActivitySchema.PhoneCall, ActivitySchema.Appointment, ActivitySchema.Annotation,
        PostSchema.Regarding, PostSchema.Post, PostSchema.Comment,
        QueueSchema.Queue, QueueSchema.QueueItem, QueueSchema.QueueMembership,
    ];

    /// <summary>The id of the system solution <c>Default</c>, which holds every component (Dataverse's well-known id).</summary>
    public static readonly Guid DefaultSolutionId = new("FD140AAF-4DF4-11DD-BD17-0019B9312238");

    /// <summary>The id of the system solution <c>Active</c> (Dataverse's well-known id).</summary>
    public static readonly Guid ActiveSolutionId = new("FD140AAE-4DF4-11DD-BD17-0019B9312238");

    /// <summary>The id of the default publisher <c>DefaultPublisher&lt;org&gt;</c> (Dataverse's well-known id).</summary>
    public static readonly Guid DefaultPublisherId = new("D21AAB71-79E7-11DD-8874-00188B01E34F");

    /// <summary>The unique names of the system solutions, which cannot be deleted.</summary>
    public const string DefaultSolutionName = "Default", ActiveSolutionName = "Active";

    /// <summary>
    /// The default of <c>organization.maxdepthforhierarchicalsecuritymodel</c>, written for a new organization and for one made
    /// before the column (from memory: the plan 2.5 checklist verifies it on a real environment).
    /// </summary>
    public const int DefaultHierarchyDepth = 3;

    public async Task EnsureAsync(SeedOptions o, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (string.IsNullOrWhiteSpace(o.OrgName)) throw new ArgumentException("OrgName is required.", nameof(o));
        if (o.AdminObjectId == Guid.Empty) throw new ArgumentException("AdminObjectId is required.", nameof(o));
        // An invalid Seed:BaseCurrency stops the startup before anything is touched (ruling X1).
        o.BaseCurrency?.Validate();

        DemandNoCustomBusinessTables(o.OrgName);
        foreach (var name in EntityOrder)
            await EnsureEntityAsync(name, ct);
        // The many-to-many relationship of the seeded intersect table queuemembership (plan 4.1a; name from Learn). Only the server's
        // store has the seed path: another one would leave the intersect without its relationship, so it fails instead.
        if (store is not MetadataStore queueStore)
            throw new NotSupportedException("The system seed needs the metadata store of the server to write the relationship 'queuemembership_association'.");
        await queueStore.EnsureSeedManyToManyAsync(QueueSchema.MembershipRelationship(), ct);

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        ISqlDialect dialect = SqlDialects.For(conn);
        var now = DateTime.UtcNow;

        await KeyLockAsync(conn, tx, "businessunit", "root", ct);
        var rootId = await FindIdAsync(conn, tx,
            dialect.Limit(
                $"SELECT businessunitid FROM data.businessunit{dialect.LockHint(RowLock.UpdateRange)} WHERE parentbusinessunitid IS NULL ORDER BY createdon, businessunitid", "1")
            + dialect.LockClause(RowLock.UpdateRange), ct);
        await KeyLockAsync(conn, tx, "systemuser", o.AdminObjectId, ct);
        var adminId = await FindIdAsync(conn, tx,
            dialect.Limit(
                $"SELECT systemuserid FROM data.systemuser{dialect.LockHint(RowLock.UpdateRange)} WHERE azureactivedirectoryobjectid = @oid", "1")
            + dialect.LockClause(RowLock.UpdateRange), ct,
            ("@oid", o.AdminObjectId));
        var newAdminId = adminId ?? Guid.NewGuid();
        rootId ??= await InsertRootBusinessUnitAsync(conn, tx, o, newAdminId, now, ct);

        if (adminId is null)
        {
            await ExecuteAsync(conn, tx, """
                INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid,
                    owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES (@id, @name, @name, @oid, @bu, @bu, @now, @id, @now, @id, 0, 1)
                """, ct, ("@id", newAdminId), ("@name", o.AdminName), ("@oid", o.AdminObjectId), ("@bu", rootId.Value), ("@now", now));
        }

        await KeyLockAsync(conn, tx, "organization", "organization", ct);
        if (await FindIdAsync(conn, tx,
                dialect.Limit($"SELECT organizationid FROM data.organization{dialect.LockHint(RowLock.UpdateRange)}", "1") + dialect.LockClause(RowLock.UpdateRange), ct) is null)
        {
            var creator = adminId ?? newAdminId;
            await ExecuteAsync(conn, tx, $"""
                INSERT INTO data.organization (organizationid, name, plugintracelogsetting, fullnameconventioncode,
                    isauditenabled, auditretentionperiodv2, isuseraccessauditenabled, useraccessauditinginterval,
                    ishierarchicalsecuritymodelenabled, maxdepthforhierarchicalsecuritymodel, usepositionhierarchy, expirechangetrackingindays,
                    {ActivitySchema.MaxUploadFileSize}, {AppModuleSchema.AutoSaveEnabled}, owningbusinessunit, createdon, createdby, modifiedon,
                    modifiedby, statecode, statuscode)
                VALUES (@id, @name, 1, 0, {dialect.BooleanLiteral(false)}, 30, {dialect.BooleanLiteral(false)}, 4, {dialect.BooleanLiteral(false)}, @depth,
                    {dialect.BooleanLiteral(false)}, @expire, @maxupload, {dialect.BooleanLiteral(true)}, @bu, @now, @by, @now, @by, 0, 1)
                """, ct, ("@id", Guid.NewGuid()), ("@name", o.OrgName), ("@depth", DefaultHierarchyDepth),
                ("@expire", ChangeTrackingSchema.DefaultExpireDays), ("@maxupload", ActivitySchema.DefaultMaxUploadFileSize), ("@bu", rootId.Value),
                ("@now", now), ("@by", creator));
        }

        var systemUserId = SeedOptions.DeriveSystemUserId(o.OrgName);
        await KeyLockAsync(conn, tx, "systemuser", systemUserId, ct);
        if (await FindIdAsync(conn, tx,
                $"SELECT systemuserid FROM data.systemuser{dialect.LockHint(RowLock.UpdateRange)} WHERE systemuserid = @id{dialect.LockClause(RowLock.UpdateRange)}", ct, ("@id", systemUserId)) is null)
        {
            await ExecuteAsync(conn, tx, """
                INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid,
                    owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES (@id, N'SYSTEM', N'SYSTEM', NULL, @bu, @bu, @now, @id, @now, @id, 0, 1)
                """, ct, ("@id", systemUserId), ("@bu", rootId.Value), ("@now", now));
        }

        await EnsureBaseCurrencyAsync(conn, tx, o.BaseCurrency, rootId.Value, adminId ?? newAdminId, now, ct);
        await EnsureUserSettingsAsync(conn, tx, systemUserId, now, ct);
        // The rows per page of the list views (plan 3.5 UI-1a): the default of 50 for a settings row made before the column,
        // once (#110, meta.upgrade_step), so a value an administrator clears afterwards is not set again at every startup.
        await UpgradeOnceAsync(conn, tx, PagingLimitDefaultStep, now,
            $"UPDATE data.{CurrencySchema.UserSettings} SET {AppModuleSchema.PagingLimit} = @limit WHERE {AppModuleSchema.PagingLimit} IS NULL", ct,
            ("@limit", AppModuleSchema.DefaultPagingLimit));
        // The time zone and formats of every user (#131, ruling Z2): the defaults for a settings row made before the columns, once
        // (meta.upgrade_step), so a value an administrator clears afterwards is not set again at every startup.
        await UpgradeOnceAsync(conn, tx, TimeZoneSchema.DefaultsStep, now,
            $"UPDATE data.{CurrencySchema.UserSettings} SET {string.Join(", ", TimeZoneSchema.UserSettingsDefaults.Select((d, i) => $"{d.Column} = {dialect.IsNull(d.Column, $"@d{i}")}"))}",
            ct, [.. DefaultParameters()]);
        // The color scheme (UI-2a): System for a settings row made before the column, once; the renderer stays null (follow the app).
        await UpgradeOnceAsync(conn, tx, ColorSchemeDefaultStep, now,
            $"UPDATE data.{CurrencySchema.UserSettings} SET {AppModuleSchema.UserColorScheme} = {dialect.IsNull(AppModuleSchema.UserColorScheme, "@scheme")}", ct,
            ("@scheme", AppModuleSchema.DefaultColorScheme));
        await EnsureTimeZonesAsync(conn, tx, now, ct);

        // The form defaults (plan 3.5 UI-1b, Learn): not the default form, active, for a form made before the two columns.
        await ExecuteAsync(conn, tx,
            $"UPDATE data.systemform SET isdefault = {dialect.IsNull("isdefault", dialect.BooleanLiteral(false))}, formactivationstate = {dialect.IsNull("formactivationstate", "1")} " +
            "WHERE isdefault IS NULL OR formactivationstate IS NULL", ct);

        await EnsureSystemSolutionsAsync(conn, tx, o, rootId.Value, adminId ?? newAdminId, now, ct);
        await EnsureSystemAdministratorProfileAsync(conn, tx, rootId.Value, adminId ?? newAdminId, now, ct);
        await EnsureSettingDefinitionsAsync(conn, tx, rootId.Value, adminId ?? newAdminId, now, ct);
        await EnsureQueueViewsAsync(conn, tx, rootId.Value, adminId ?? newAdminId, now, ct);

        // Organizations of an older database get the default only where the new column is still empty.
        await ExecuteAsync(conn, tx, "UPDATE data.organization SET plugintracelogsetting = 1 WHERE plugintracelogsetting IS NULL", ct);
        // The full name order of an organization made before contacts were seeded is the default, "Last Name, First Name".
        await ExecuteAsync(conn, tx, "UPDATE data.organization SET fullnameconventioncode = 0 WHERE fullnameconventioncode IS NULL", ct);

        // The audit settings: auditing off, thirty days of retention, no user access auditing (which is not supported), 4 hours.
        await ExecuteAsync(conn, tx, $"""
            UPDATE data.organization SET
                isauditenabled = {dialect.IsNull("isauditenabled", dialect.BooleanLiteral(false))},
                auditretentionperiodv2 = {dialect.IsNull("auditretentionperiodv2", "30")},
                isuseraccessauditenabled = {dialect.IsNull("isuseraccessauditenabled", dialect.BooleanLiteral(false))},
                useraccessauditinginterval = {dialect.IsNull("useraccessauditinginterval", "4")}
            WHERE isauditenabled IS NULL OR auditretentionperiodv2 IS NULL OR isuseraccessauditenabled IS NULL
                OR useraccessauditinginterval IS NULL
            """, ct);

        // The change tracking retention (plan 2.13, ruling C1): the Dataverse default of 7 days.
        await ExecuteAsync(conn, tx,
            $"UPDATE data.organization SET {ChangeTrackingSchema.ExpireDaysColumn} = @expire WHERE {ChangeTrackingSchema.ExpireDaysColumn} IS NULL", ct,
            ("@expire", ChangeTrackingSchema.DefaultExpireDays));

        // The note body limit (plan 2.11, pre-flight C3): 5 MB for an organization made before the column.
        await ExecuteAsync(conn, tx,
            $"UPDATE data.organization SET {ActivitySchema.MaxUploadFileSize} = @maxupload WHERE {ActivitySchema.MaxUploadFileSize} IS NULL", ct,
            ("@maxupload", ActivitySchema.DefaultMaxUploadFileSize));
        // Auto-save of the model-driven forms (#117): on, as in Dataverse, for an organization made before the column.
        await ExecuteAsync(conn, tx,
            $"UPDATE data.organization SET {AppModuleSchema.AutoSaveEnabled} = {dialect.BooleanLiteral(true)} WHERE {AppModuleSchema.AutoSaveEnabled} IS NULL", ct);
        // The duplicate detection settings (plan 2.15): all four off (Learn: default false).
        await ExecuteAsync(conn, tx,
            "UPDATE data.organization SET " + string.Join(", ", DuplicateDetectionSchema.OrganizationColumns.Select(c => $"{c} = {dialect.IsNull(c, dialect.BooleanLiteral(false))}")) +
            " WHERE " + string.Join(" OR ", DuplicateDetectionSchema.OrganizationColumns.Select(c => $"{c} IS NULL")), ct);

        // The hierarchy security settings (plan 2.5): the model off, a depth of 3, no position hierarchy (which is not supported).
        await ExecuteAsync(conn, tx, $"""
            UPDATE data.organization SET
                ishierarchicalsecuritymodelenabled = {dialect.IsNull("ishierarchicalsecuritymodelenabled", dialect.BooleanLiteral(false))},
                maxdepthforhierarchicalsecuritymodel = {dialect.IsNull("maxdepthforhierarchicalsecuritymodel", "@depth")},
                usepositionhierarchy = {dialect.IsNull("usepositionhierarchy", dialect.BooleanLiteral(false))}
            WHERE ishierarchicalsecuritymodelenabled IS NULL OR maxdepthforhierarchicalsecuritymodel IS NULL OR usepositionhierarchy IS NULL
            """, ct, ("@depth", DefaultHierarchyDepth));

        // The row components (plan 2.6, ruling M14): rows made before the ismanaged column are unmanaged.
        foreach (var table in EntityOrder.Where(t => t != "solution" && Defines(t, "ismanaged")))
            await ExecuteAsync(conn, tx, $"UPDATE data.{dialect.Quote(table)} SET ismanaged = {dialect.BooleanLiteral(false)} WHERE ismanaged IS NULL", ct);

        // The audit lookups: a record's history and its deletion (objectid, type, newest first), and the retention purge and
        // DeleteAuditData (createdon). Created for a new organization and for one made before them.
        // No command timeout: on an organization that already holds many audit rows the build can outlast the default 30 s.
        // Plan 2.7a, Task 4: the guarded index statements are the dialect's (ISqlDialect.CreateIndexIfMissing).
        await ExecuteAsync(conn, tx,
            dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, "audit", "IX_audit_objectid", "objectid, objecttypecode, createdon DESC") + "\n" +
            dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, "audit", "IX_audit_createdon", "createdon"), ct, timeoutSeconds: 0);

        // The activity lookups (plan 2.11): the activities of a regarding record, and the party rows of an activity and of a party.
        await ExecuteAsync(conn, tx,
            dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, "activitypointer", "IX_activitypointer_regarding", "regardingobjectid, regardingobjectidtype") + "\n" +
            dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, "activityparty", "IX_activityparty_activityid", "activityid") + "\n" +
            dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, "activityparty", "IX_activityparty_partyid", "partyid"), ct, timeoutSeconds: 0);

        // Plan 2.7b, ruling Q3 (#299): the unique keys on text columns of a 2.7a PostgreSQL organization compare text keys.
        await RebuildTextKeysAsync(conn, tx, now, ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The base currency (ruling X1): the unique index on <c>isocurrencycode</c>; for an organization without a base currency (a
    /// new one, or one made before currencies) the currency row of <paramref name="configured"/>, or of the euro when it is null
    /// (rate 1, System, active), and the organization's currency columns. An existing row with that code is reused: made active at
    /// rate 1, or refused when it is a Custom currency. For an organization that has a base currency, nothing but a warning when
    /// <paramref name="configured"/> is given and names another code (the base currency is fixed once set). Missing organization
    /// defaults (pricing precision 2, display option 0, format code 0) are filled either way.
    /// </summary>
    /// <exception cref="InvalidOperationException">The row to reuse is a Custom currency.</exception>
    private async Task EnsureBaseCurrencyAsync(
        DbConnection conn, DbTransaction tx, BaseCurrencySeed? configured, Guid rootId, Guid creator, DateTime now, CancellationToken ct)
    {
        var wanted = configured ?? BaseCurrencySeed.Default;
        ISqlDialect dialect = SqlDialects.For(conn);
        await ExecuteAsync(conn, tx,
            dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, CurrencySchema.Table, UniqueIsoCodeIndex, dialect.TextKey(CurrencySchema.IsoCode), unique: true), ct);

        await using (var cmd = conn.Command(dialect.Limit($"""
            SELECT c.{CurrencySchema.IsoCode}
            FROM data.organization o{dialect.LockHint(RowLock.UpdateRange)}
            JOIN data.{CurrencySchema.Table} c ON c.{CurrencySchema.CurrencyId} = o.{CurrencySchema.BaseCurrencyId}
            """, "1") + dialect.LockClause(RowLock.UpdateRange, "o"), tx))
        {
            if (await cmd.ExecuteScalarAsync(ct) is string current)
            {
                if (configured is not null && !string.Equals(current, configured.IsoCode, StringComparison.Ordinal))
                    logger?.LogWarning(
                        "{Setting} is {Wanted}, but the organization's base currency is {Current}: a base currency cannot change once set, so the setting is ignored",
                        BaseCurrencySeed.SettingName, configured.IsoCode, current);
                await FillCurrencyDefaultsAsync(conn, tx, ct);
                return;
            }
        }

        Guid? currencyId = null;
        await KeyLockAsync(conn, tx, CurrencySchema.Table, wanted.IsoCode, ct);
        await using (var find = conn.Command(
            $"SELECT {CurrencySchema.CurrencyId}, {CurrencySchema.CurrencyType} FROM data.{CurrencySchema.Table}{dialect.LockHint(RowLock.UpdateRange)} WHERE {CurrencySchema.IsoCode} = @iso{dialect.LockClause(RowLock.UpdateRange)}", tx))
        {
            find.AddParameter("@iso", DbType.String, wanted.IsoCode);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                currencyId = reader.GetGuid(0);
                if (!reader.IsDBNull(1) && reader.GetInt32(1) != 0)
                    throw new InvalidOperationException(
                        $"The organization has no base currency, and its currency '{wanted.IsoCode}' ({currencyId}) is a custom currency, which " +
                        $"cannot become the base currency. Set {BaseCurrencySeed.SettingName} to another code, or make that currency a System " +
                        "currency, then restart.");
            }
        }
        if (currencyId is not null)
        {
            // The reused row becomes the base currency: active, at rate 1 (ruling X2).
            await ExecuteAsync(conn, tx, $"""
                UPDATE data.{CurrencySchema.Table} SET {CurrencySchema.ExchangeRate} = 1, statecode = 0, statuscode = 1, modifiedon = @now, modifiedby = @by
                WHERE {CurrencySchema.CurrencyId} = @id AND ({CurrencySchema.ExchangeRate} IS NULL OR {CurrencySchema.ExchangeRate} <> 1 OR statecode <> 0 OR statuscode <> 1)
                """, ct, ("@id", currencyId.Value), ("@now", now), ("@by", creator));
        }
        else
        {
            currencyId = Guid.NewGuid();
            await ExecuteAsync(conn, tx, $"""
                INSERT INTO data.{CurrencySchema.Table} ({CurrencySchema.CurrencyId}, currencyname, {CurrencySchema.IsoCode}, {CurrencySchema.Symbol},
                    {CurrencySchema.Precision}, {CurrencySchema.ExchangeRate}, {CurrencySchema.CurrencyType},
                    owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES (@id, @name, @iso, @symbol, @precision, 1, 0, @bu, @now, @by, @now, @by, 0, 1)
                """, ct, ("@id", currencyId.Value), ("@name", wanted.Name), ("@iso", wanted.IsoCode), ("@symbol", wanted.Symbol),
                ("@precision", wanted.Precision), ("@bu", rootId), ("@now", now), ("@by", creator));
        }
        // Plan 2.7a, Task 5 fix round 1: scalar subqueries instead of T-SQL's UPDATE-alias-FROM form (the row exists: found or inserted above).
        await ExecuteAsync(conn, tx, $"""
            UPDATE data.organization SET {CurrencySchema.BaseCurrencyId} = @id,
                {CurrencySchema.BaseSymbol} = (SELECT c.{CurrencySchema.Symbol} FROM data.{CurrencySchema.Table} c WHERE c.{CurrencySchema.CurrencyId} = @id),
                {CurrencySchema.BasePrecision} = (SELECT c.{CurrencySchema.Precision} FROM data.{CurrencySchema.Table} c WHERE c.{CurrencySchema.CurrencyId} = @id)
            """, ct, ("@id", currencyId.Value));
        await FillCurrencyDefaultsAsync(conn, tx, ct);
    }

    private static Task FillCurrencyDefaultsAsync(DbConnection conn, DbTransaction tx, CancellationToken ct)
    {
        ISqlDialect dialect = SqlDialects.For(conn);
        return ExecuteAsync(conn, tx, $"""
            UPDATE data.organization SET
                {CurrencySchema.PricingPrecision} = {dialect.IsNull(CurrencySchema.PricingPrecision, "@pricing")},
                {CurrencySchema.DisplayOption} = {dialect.IsNull(CurrencySchema.DisplayOption, "0")},
                {CurrencySchema.FormatCode} = {dialect.IsNull(CurrencySchema.FormatCode, "0")}
            WHERE {CurrencySchema.PricingPrecision} IS NULL OR {CurrencySchema.DisplayOption} IS NULL OR {CurrencySchema.FormatCode} IS NULL
            """, ct, ("@pricing", CurrencySchema.DefaultPricingPrecision));
    }

    /// <summary>
    /// One <c>usersettings</c> row (keyed by <c>systemuserid</c>, owned by the user, in the user's business unit, no currency of its
    /// own) for every <c>systemuser</c> without one: the seeded users, and any user an earlier version or a SQL script added. The
    /// only backfill of the table (pre-flight C7); the Core adds the row of a user it creates.
    /// </summary>
    private static async Task EnsureUserSettingsAsync(DbConnection conn, DbTransaction tx, Guid systemUserId, DateTime now, CancellationToken ct)
    {
        ISqlDialect dialect = SqlDialects.For(conn);
        await KeyLockAsync(conn, tx, CurrencySchema.UserSettings, "seed", ct);
        await ExecuteAsync(conn, tx, $"""
            INSERT INTO data.{CurrencySchema.UserSettings} (systemuserid, ownerid, owneridtype, owninguser, owningbusinessunit,
                createdon, createdby, modifiedon, modifiedby, statecode, statuscode, {AppModuleSchema.PagingLimit}, {AppModuleSchema.UserColorScheme},
                {string.Join(", ", TimeZoneSchema.UserSettingsDefaults.Select(d => d.Column))})
            SELECT u.systemuserid, u.systemuserid, 8, u.systemuserid, u.businessunitid, @now, @by, @now, @by, 0, 1, {AppModuleSchema.DefaultPagingLimit},
                {AppModuleSchema.DefaultColorScheme},
                {string.Join(", ", TimeZoneSchema.UserSettingsDefaults.Select((_, i) => $"@d{i}"))}
            FROM data.systemuser u
            WHERE NOT EXISTS (SELECT 1 FROM data.{CurrencySchema.UserSettings} s{dialect.LockHint(RowLock.UpdateRange)} WHERE s.systemuserid = u.systemuserid)
            """, ct, [("@now", now), ("@by", systemUserId), .. DefaultParameters()]);
    }

    /// <summary>The parameters <c>@d0</c>, <c>@d1</c>... of <see cref="TimeZoneSchema.UserSettingsDefaults"/>, in order.</summary>
    private static IEnumerable<(string Name, object Value)> DefaultParameters() =>
        TimeZoneSchema.UserSettingsDefaults.Select((d, i) => ($"@d{i}", d.Value));

    /// <summary>
    /// One <c>timezonedefinition</c> row per <see cref="TimeZoneCatalog"/> code that has none (#131, ruling Z3), with a stable id.
    /// <c>userinterfacename</c> is the host's display name of the zone (.NET <see cref="TimeZoneInfo.DisplayName"/>), not
    /// Dataverse's label; a code the host cannot resolve gets its Windows id instead, and a warning.
    /// </summary>
    private async Task EnsureTimeZonesAsync(DbConnection conn, DbTransaction tx, DateTime now, CancellationToken ct)
    {
        var rows = TimeZoneCatalog.All.Select(z => new
        {
            id = TimeZoneSchema.RowId(z.Code), code = z.Code, standard = z.StandardName, bias = z.Bias, retired = z.RetiredOrder,
            name = Truncate(DisplayName(z), TimeZoneSchema.MaxNameLength),
        });
        ISqlDialect dialect = SqlDialects.For(conn);
        var json = dialect.JsonRecords("@json", [
            new JsonColumn("id", SqlColumnKind.Guid), new JsonColumn("code", SqlColumnKind.Int32), new JsonColumn("standard", SqlColumnKind.String, 100),
            new JsonColumn("name", SqlColumnKind.String, 100), new JsonColumn("bias", SqlColumnKind.Int32), new JsonColumn("retired", SqlColumnKind.Int32)]);
        // A check then insert of many codes: the key lock serializes the seeds where the hint does not lock the missing rows.
        await KeyLockAsync(conn, tx, TimeZoneSchema.Table, "seed", ct);
        await ExecuteAsync(conn, tx, $"""
            INSERT INTO data.{TimeZoneSchema.Table} (timezonedefinitionid, {TimeZoneSchema.Code}, standardname, userinterfacename, bias,
                retiredorder, createdon, modifiedon, statecode, statuscode)
            SELECT j.id, j.code, j.standard, j.name, j.bias, j.retired, @now, @now, 0, 1
            FROM {json} j
            WHERE NOT EXISTS (SELECT 1 FROM data.{TimeZoneSchema.Table} t{dialect.LockHint(RowLock.UpdateRange)} WHERE t.{TimeZoneSchema.Code} = j.code)
            """, ct, ("@json", System.Text.Json.JsonSerializer.Serialize(rows)), ("@now", now));
    }

    private string DisplayName(TimeZoneEntry zone)
    {
        try
        {
            return TimeZoneCatalog.Resolve(zone.Code).DisplayName;
        }
        catch (Xrmd.Core.Errors.XrmdFaultException e)
        {
            logger?.LogWarning("Time zone code {Code}: {Reason}", zone.Code, e.Message);
            return zone.StandardName;
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>The unique index on <c>transactioncurrency.isocurrencycode</c> (ruling X3), created by the seed.</summary>
    public const string UniqueIsoCodeIndex = "UX_transactioncurrency_isocurrencycode";

    /// <summary>
    /// The default publisher (<c>DefaultPublisher&lt;org&gt;</c>, prefix <c>new</c>, option value prefix 10000) and the system
    /// solutions <c>Default</c> and <c>Active</c>, each inserted only when its row is missing.
    /// </summary>
    private static async Task EnsureSystemSolutionsAsync(
        DbConnection conn, DbTransaction tx, SeedOptions o, Guid rootId, Guid creator, DateTime now, CancellationToken ct)
    {
        // Plan 2.7a, Task 5 fix round 1: each IF NOT EXISTS of the T-SQL batch is a key lock, a locking read and an insert.
        var dialect = SqlDialects.For(conn);
        (string Name, object Value)[] common = [("@bu", rootId), ("@now", now), ("@by", creator)];
        if (await MissingAsync(conn, tx, "publisher", "publisherid", DefaultPublisherId, ct))
            await ExecuteAsync(conn, tx, """
                INSERT INTO data.publisher (publisherid, uniquename, friendlyname, customizationprefix, customizationoptionvalueprefix,
                    description, owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES (@publisher, @publishername, @publisherlabel, N'new', 10000, @publisherdescription, @bu, @now, @by, @now, @by, 0, 1);
                """, ct, [("@publisher", DefaultPublisherId), ("@publishername", "DefaultPublisher" + o.OrgName),
                ("@publisherlabel", "Default Publisher for " + o.OrgName), ("@publisherdescription", "Default publisher for this organization"), .. common]);
        foreach (var (id, name, label) in new[] { (DefaultSolutionId, DefaultSolutionName, "Default Solution"), (ActiveSolutionId, ActiveSolutionName, "Active Solution") })
            if (await MissingAsync(conn, tx, "solution", "solutionid", id, ct))
                await ExecuteAsync(conn, tx, $"""
                    INSERT INTO data.solution (solutionid, uniquename, friendlyname, version, ismanaged, publisherid, description, installedon,
                        owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                    VALUES (@id, @name, @label, N'1.0', {dialect.BooleanLiteral(false)}, @publisher, @label, @now,
                        @bu, @now, @by, @now, @by, 0, 1);
                    """, ct, [("@id", id), ("@name", name), ("@label", label), ("@publisher", DefaultPublisherId), .. common]);
    }

    /// <summary>
    /// True when <paramref name="table"/> has no row whose <paramref name="key"/> is <paramref name="id"/>, after taking the key
    /// lock and an update lock on that row (plan 2.7a, Task 5 fix round 1: the <c>IF NOT EXISTS (… WITH (UPDLOCK, HOLDLOCK))</c>
    /// of the seed batches). The caller inserts the row in the same transaction.
    /// </summary>
    private static async Task<bool> MissingAsync(DbConnection conn, DbTransaction tx, string table, string key, Guid id, CancellationToken ct)
    {
        var dialect = SqlDialects.For(conn);
        await KeyLockAsync(conn, tx, table, id, ct);
        return await FindIdAsync(conn, tx,
            $"SELECT {key} FROM data.{table}{dialect.LockHint(RowLock.UpdateRange)} WHERE {key} = @id{dialect.LockClause(RowLock.UpdateRange)}", ct, ("@id", id)) is null;
    }

    /// <summary>The system views of the queue tables (<see cref="QueueSchema.SystemViews"/>), each inserted only when its id is missing.</summary>
    private async Task EnsureQueueViewsAsync(DbConnection conn, DbTransaction tx, Guid rootId, Guid creator, DateTime now, CancellationToken ct)
    {
        var dialect = SqlDialects.For(conn);
        foreach (var view in QueueSchema.SystemViews)
        {
            if (!await MissingAsync(conn, tx, "savedquery", "savedqueryid", view.Id, ct)) continue;
            var code = store.GetEntity(view.Table)?.ObjectTypeCode ?? 0;
            await ExecuteAsync(conn, tx, $"""
                INSERT INTO data.savedquery (savedqueryid, name, returnedtypecode, fetchxml, layoutxml, querytype, isdefault, ismanaged,
                    owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES (@id, @name, @table, @fetch, @layout, 0, {dialect.BooleanLiteral(view.IsDefault)}, {dialect.BooleanLiteral(false)},
                    @bu, @now, @by, @now, @by, 0, 1);
                """, ct, ("@id", view.Id), ("@name", view.Name), ("@table", view.Table), ("@fetch", view.FetchXml),
                ("@layout", view.LayoutXml(code)), ("@bu", rootId), ("@now", now), ("@by", creator));
        }
    }

    /// <summary>
    /// The System Administrator field security profile (fixed id, no permission rows), inserted only when its row is missing.
    /// The table rules keep it read-only.
    /// </summary>
    private static async Task EnsureSystemAdministratorProfileAsync(
        DbConnection conn, DbTransaction tx, Guid rootId, Guid creator, DateTime now, CancellationToken ct)
    {
        if (!await MissingAsync(conn, tx, "fieldsecurityprofile", "fieldsecurityprofileid", FieldSecurityTables.SystemAdministratorProfileId, ct)) return;
        await ExecuteAsync(conn, tx, """
            INSERT INTO data.fieldsecurityprofile (fieldsecurityprofileid, name, description,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES (@id, @name, @description, @bu, @now, @by, @now, @by, 0, 1);
            """, ct,
            ("@id", FieldSecurityTables.SystemAdministratorProfileId), ("@name", FieldSecurityTables.SystemAdministratorProfileName),
            ("@description", "Read-only profile of the System Administrator role, which reads and writes every secured column."),
            ("@bu", rootId), ("@now", now), ("@by", creator));
    }

    /// <summary>
    /// The app setting definitions the model-driven forms read (#129): <see cref="AppModuleSchema.AsyncOnLoad"/> and
    /// <see cref="AppModuleSchema.AsyncOnSave"/>, plus the timeline options <see cref="AppModuleSchema.TimelineGroupByDate"/>,
    /// <see cref="AppModuleSchema.TimelineRememberFilters"/> (UI-1g) and <see cref="AppModuleSchema.TimelinePosts"/> (UI-1g, posts),
    /// with their well-known ids and the default <c>false</c>, and the presentation settings <see cref="AppModuleSchema.Renderer"/>
    /// (default <c>classic</c>) and <see cref="AppModuleSchema.Presentation"/> (default empty; UI-2a) and <see cref="AppModuleSchema.LookupOpenMode"/> (default <c>sidepane</c>) and <see cref="AppModuleSchema.AppOnLoad"/> (default empty; X.10), each inserted only when its row is missing (an administrator's change
    /// to a row stays).
    /// </summary>
    private static async Task EnsureSettingDefinitionsAsync(
        DbConnection conn, DbTransaction tx, Guid rootId, Guid creator, DateTime now, CancellationToken ct)
    {
        ISqlDialect dialect = SqlDialects.For(conn);
        await KeyLockAsync(conn, tx, AppModuleSchema.SettingDefinition, "seed", ct);
        await ExecuteAsync(conn, tx, $"""
            INSERT INTO data.{AppModuleSchema.SettingDefinition} ({AppModuleSchema.SettingDefinition}id, uniquename, displayname, defaultvalue,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            SELECT j.id, j.name, j.name, j.defaultvalue, @bu, @now, @by, @now, @by, 0, 1
            FROM (VALUES (@onload, @onloadname, @false), (@onsave, @onsavename, @false), (@groupby, @groupbyname, @false),
                (@remember, @remembername, @false), (@posts, @postsname, @false),
                (@renderer, @renderername, @rendererdefault), (@presentation, @presentationname, @presentationdefault),
                (@lookupopen, @lookupopenname, @lookupopendefault),
                (@apponload, @apponloadname, @apponloaddefault))
                AS j (id, name, defaultvalue)
            WHERE NOT EXISTS (SELECT 1 FROM data.{AppModuleSchema.SettingDefinition} d{dialect.LockHint(RowLock.UpdateRange)}
                WHERE d.{AppModuleSchema.SettingDefinition}id = j.id OR d.uniquename = j.name);
            """, ct,
            ("@onload", AppModuleSchema.AsyncOnLoadId), ("@onloadname", AppModuleSchema.AsyncOnLoad),
            ("@onsave", AppModuleSchema.AsyncOnSaveId), ("@onsavename", AppModuleSchema.AsyncOnSave),
            ("@groupby", AppModuleSchema.TimelineGroupByDateId), ("@groupbyname", AppModuleSchema.TimelineGroupByDate),
            ("@remember", AppModuleSchema.TimelineRememberFiltersId), ("@remembername", AppModuleSchema.TimelineRememberFilters),
            ("@posts", AppModuleSchema.TimelinePostsId), ("@postsname", AppModuleSchema.TimelinePosts),
            ("@renderer", AppModuleSchema.RendererId), ("@renderername", AppModuleSchema.Renderer),
            ("@rendererdefault", AppModuleSchema.RendererDefault),
            ("@presentation", AppModuleSchema.PresentationId), ("@presentationname", AppModuleSchema.Presentation),
            ("@presentationdefault", AppModuleSchema.PresentationDefault),
            ("@lookupopen", AppModuleSchema.LookupOpenModeId), ("@lookupopenname", AppModuleSchema.LookupOpenMode),
            ("@lookupopendefault", AppModuleSchema.LookupOpenModeDefault),
            ("@apponload", AppModuleSchema.AppOnLoadId), ("@apponloadname", AppModuleSchema.AppOnLoad),
            ("@apponloaddefault", AppModuleSchema.AppOnLoadDefault),
            // A parameter, not N'false': on PostgreSQL a VALUES column cannot mix a character literal with citext parameters.
            ("@false", "false"),
            ("@bu", rootId), ("@now", now), ("@by", creator));
    }

    private static async Task<Guid> InsertRootBusinessUnitAsync(
        DbConnection conn, DbTransaction tx, SeedOptions o, Guid adminId, DateTime now, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx, """
            INSERT INTO data.businessunit (businessunitid, name, parentbusinessunitid, owningbusinessunit,
                createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES (@id, @name, NULL, @id, @now, @by, @now, @by, 0, 1)
            """, ct, ("@id", o.RootBusinessUnitId), ("@name", o.OrgName), ("@now", now), ("@by", adminId));
        return o.RootBusinessUnitId;
    }

    /// <summary>
    /// A table named <c>account</c> or <c>contact</c> that is not the seeded one (object type code 10000 or more, made before
    /// these tables were seeded) stops the startup: nothing is renamed, merged or touched.
    /// </summary>
    /// <exception cref="InvalidOperationException">Naming the table and the organization.</exception>
    private void DemandNoCustomBusinessTables(string orgName)
    {
        foreach (var name in BusinessTables)
            if (store.GetEntity(name) is { } existing && !MetadataStore.IsSystemEntity(existing))
                throw new InvalidOperationException(
                    $"Organization '{orgName}' already has a custom table named '{name}' (object type code {existing.ObjectTypeCode}), " +
                    $"but xrmverse seeds its own '{name}' table. Rename or delete the custom table, then restart the server.");
    }

    private async Task EnsureEntityAsync(string logicalName, CancellationToken ct)
    {
        var entity = LoadDefinition(logicalName);
        // A seed definition may declare saved relationships (a Customer lookup has one per target); they are not part of the
        // stored table definition.
        var relationships = (entity.ManyToOneRelationships ?? []).ToList();
        entity.SetRelationships([], []);
        await EnsureGlobalOptionSetsAsync(entity, ct);
        var existing = store.GetEntity(logicalName);
        if (existing is null)
        {
            // Intersect (and other) seed entities may have no primary name.
            var primaryName = entity.PrimaryNameAttribute is null
                ? null
                : (entity.Attributes ?? []).Single(a => a.LogicalName == entity.PrimaryNameAttribute);
            entity.SetAttributes((entity.Attributes ?? []).Where(a => !ReferenceEquals(a, primaryName)));
            // Seeded entities may define their own statecode/statuscode, which the public API rejects as reserved names.
            if (store is MetadataStore concrete) await concrete.CreateSeedEntityAsync(entity, primaryName, ct);
            else await store.CreateEntityAsync(entity, primaryName!, ct);
            await EnsureRelationshipsAsync(relationships, ct);
            return;
        }

        // An earlier seed named some entity sets differently from Dataverse; the public API cannot change a set name.
        if (entity.EntitySetName is { } wanted && existing.EntitySetName != wanted && store is MetadataStore seedStore)
        {
            await seedStore.RenameSeedEntitySetAsync(logicalName, wanted, ct);
            existing = store.GetEntity(logicalName)!;
        }

        // An earlier seed gave appmoduleroles a type code and a key name Dataverse does not have (#105).
        if (store is MetadataStore correctStore && entity.ObjectTypeCode is { } code
            && (existing.ObjectTypeCode != code || (entity.PrimaryIdAttribute is { } key && SystemAttributes.PrimaryIdName(existing) != key)))
        {
            await correctStore.CorrectSeedEntityAsync(logicalName, code, entity.PrimaryIdAttribute ?? SystemAttributes.PrimaryIdName(existing), ct);
            existing = store.GetEntity(logicalName)!;
        }

        // Entities created by an earlier version miss the attributes added since.
        var present = (existing.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in entity.Attributes ?? [])
            if (!present.Contains(attribute.LogicalName) && !SystemAttributes.IsReservedName(logicalName, attribute.LogicalName))
            {
                // A Customer column is refused by the public API (CreateCustomerRelationships only): the seed has its own way in.
                if (CustomerColumns.IsCustomer(attribute) && store is MetadataStore customerStore)
                    await customerStore.AddSeedAttributeAsync(logicalName, attribute, ct);
                else await store.AddAttributeAsync(logicalName, attribute, ct);
            }
        // Options added to a seeded local choice since (asyncoperation.operationtype 7 and 8, plan 2.15).
        if (store is MetadataStore optionStore)
            foreach (var picklist in (entity.Attributes ?? []).OfType<PicklistAttributeMetadata>().Where(a => a.OptionSet is { IsGlobal: not true }))
                await optionStore.EnsureSeedOptionsAsync(logicalName, picklist, ct);
        // A lookup that gained targets since (sdkmessageprocessingstep.eventhandler: serviceendpoint, plan 3.2).
        if (store is MetadataStore targetStore)
            foreach (var lookup in (entity.Attributes ?? []).OfType<LookupAttributeMetadata>())
                if (existing.Attributes?.FirstOrDefault(a => a.LogicalName == lookup.LogicalName) is LookupAttributeMetadata stored
                    && (lookup.Targets ?? []).Except(stored.Targets ?? []).Any())
                    await targetStore.SetSeedLookupTargetsAsync(logicalName, lookup.LogicalName, lookup.Targets!, ct);
        await EnsureRelationshipsAsync(relationships, ct);
    }

    /// <summary>
    /// A seed column bound to a global choice (<c>OptionSet.IsGlobal</c> with a name) carries the choice's options in the seed
    /// file; the choice is created first, once (a choice that exists is left as it is, so an administrator's labels stay).
    /// </summary>
    private async Task EnsureGlobalOptionSetsAsync(EntityMetadata entity, CancellationToken ct)
    {
        var choices = (entity.Attributes ?? []).OfType<EnumAttributeMetadata>()
            .Select(a => a.OptionSet)
            .Where(o => o is { IsGlobal: true, Name.Length: > 0 } && o.Options.Count > 0)
            .Cast<OptionSetMetadata>().DistinctBy(o => o.Name).ToList();
        if (choices.Count == 0) return;
        if (store is not MetadataStore concrete)
            throw new InvalidOperationException("Seed global choices need the metadata store of the server.");
        await concrete.EnsureSeedOptionSetsAsync(choices, ct);
    }

    private async Task EnsureRelationshipsAsync(List<Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata> relationships, CancellationToken ct)
    {
        if (relationships.Count == 0) return;
        if (store is not MetadataStore concrete)
            throw new InvalidOperationException("Seed relationships need the metadata store of the server.");
        await concrete.EnsureSeedRelationshipsAsync(relationships, ct);
    }

    /// <summary>The attribute names of every seed definition, by entity.</summary>
    private static readonly Lazy<Dictionary<string, HashSet<string>>> SeedAttributes = new(() =>
        EntityOrder.ToDictionary(name => name,
            name => (LoadDefinition(name).Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal));

    /// <summary>True when the seed definition of the system entity <paramref name="entityLogicalName"/> has the attribute.</summary>
    internal static bool Defines(string entityLogicalName, string attributeLogicalName) =>
        SeedAttributes.Value.TryGetValue(entityLogicalName, out var names) && names.Contains(attributeLogicalName);

    private static EntityMetadata LoadDefinition(string logicalName)
    {
        var resource = ResourcePrefix + logicalName + ".json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Missing seed resource {resource}");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    /// <summary>
    /// Runs the one-time data upgrade <paramref name="update"/> and records <paramref name="step"/> in <c>meta.upgrade_step</c>,
    /// unless it is recorded already (#110). The step's key lock and the locking read serialize two startups (plan 2.7a, Task 4:
    /// the <c>IF NOT EXISTS … BEGIN … END</c> batch it replaces was T-SQL only).
    /// </summary>
    private static Task UpgradeOnceAsync(DbConnection conn, DbTransaction tx, string step, DateTime now, string update,
        CancellationToken ct, params (string Name, object Value)[] parameters) =>
        UpgradeOnceAsync(conn, tx, step, now, async () =>
        {
            await ExecuteAsync(conn, tx, update, ct, parameters);
            return true;
        }, ct);

    /// <summary>
    /// <see cref="UpgradeOnceAsync(DbConnection, DbTransaction, string, DateTime, string, CancellationToken, ValueTuple{string, object}[])"/>
    /// with the upgrade <paramref name="work"/>, which returns true when it is done: the step is recorded only then, so that a
    /// later start runs it again (plan 2.7b, ruling Q3: a key whose rows must be fixed first).
    /// </summary>
    private static async Task UpgradeOnceAsync(DbConnection conn, DbTransaction tx, string step, DateTime now, Func<Task<bool>> work,
        CancellationToken ct)
    {
        var dialect = SqlDialects.For(conn);
        await KeyLockAsync(conn, tx, "meta.upgrade_step", step, ct);
        await using (var cmd = conn.Command(
                         $"SELECT COUNT(*) FROM meta.upgrade_step{dialect.LockHint(RowLock.UpdateRange)} WHERE name = @step", tx))
        {
            cmd.AddParameter("@step", DbType.String, step);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0) return;
        }
        if (!await work()) return;
        await ExecuteAsync(conn, tx, "INSERT INTO meta.upgrade_step (name, applied_on) VALUES (@step, @now)", ct, ("@step", step), ("@now", now));
    }

    /// <summary>
    /// The step <see cref="TextKeysStep"/> (plan 2.7b, ruling Q3, #299): each unique index of the <c>data</c> schema on a text column
    /// that compares the stored text (<see cref="TextKeys.ReadPlainAsync"/>) is built again on the text keys of its text columns,
    /// under the same name. When its rows already collide under the new rule the index stays as it was (the rebuild is rolled back
    /// to a savepoint), the key is logged, and the step stays unrecorded, so that the next start tries again; the health report and
    /// the doctor name the key until then. Nothing on SQL Server, whose keys compare like this already.
    /// </summary>
    private async Task RebuildTextKeysAsync(DbConnection conn, DbTransaction tx, DateTime now, CancellationToken ct)
    {
        var dialect = SqlDialects.For(conn);
        if (dialect.PlainTextKeyIndexes is null) return;
        await UpgradeOnceAsync(conn, tx, TextKeysStep, now, async () =>
        {
            var session = new DataSession(conn, tx);
            var done = true;
            foreach (var key in await TextKeys.ReadPlainAsync(conn, tx, ct))
            {
                var rebuild = dialect.DropIndexIfExists(DdlGenerator.DataSchema, key.Table, key.Index) + "\n" +
                              dialect.CreateIndexIfMissing(DdlGenerator.DataSchema, key.Table, key.Index,
                                  DdlGenerator.KeyColumns(dialect, key.Columns), unique: true, filter: key.Filter);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await session.RecoverableAsync(async () =>
                    {
                        await ExecuteAsync(conn, tx, rebuild, ct, timeoutSeconds: 0);
                        return true;
                    }, ct);
                }
                catch (DbException ex) when (dialect.Classify(ex) == DbErrorKind.UniqueViolation)
                {
                    // Fix round 1: the failed build is retried at every start until the rows are fixed, so its cost is logged.
                    logger?.LogWarning("{Message} (the rebuild attempt took {Elapsed} ms)", TextKeys.ConflictMessage(key.Table, key.Index),
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    done = false;
                }
            }
            return done;
        }, ct);
    }

    /// <summary>The key lock of <paramref name="key"/> in <paramref name="table"/> (plan 2.7a, Task 3 fix round 1; <see cref="DataSessionSql.KeyLockAsync"/>).</summary>
    private static Task KeyLockAsync(DbConnection conn, DbTransaction tx, string table, object? key, CancellationToken ct) =>
        new DataSession(conn, tx).KeyLockAsync(table, key, ct);

    private static async Task<Guid?> FindIdAsync(
        DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.Command(sql, tx);
        foreach (var (n, v) in parameters) cmd.AddParameter(n, DbValueTypes.Of(v), v);
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    private static Task ExecuteAsync(
        DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
        => ExecuteAsync(conn, tx, sql, ct, timeoutSeconds: null, parameters);

    private static async Task ExecuteAsync(
        DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, int? timeoutSeconds,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.Command(sql, tx);
        if (timeoutSeconds is { } t) cmd.CommandTimeout = t;
        foreach (var (n, v) in parameters) cmd.AddParameter(n, DbValueTypes.Of(v), v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
