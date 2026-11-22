using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Attributes every entity gets besides the caller's ones: primary key <c>{logicalname}id</c>, the system columns
/// (createdon/by, modifiedon/by, owninguser/team/businessunit, statecode, statuscode, versionnumber; an elastic table
/// has <c>partitionid</c> and <c>ttlinseconds</c> instead of the state columns),
/// <c>ownerid</c> for user/team-owned entities and the <c>_base</c> companion of each money attribute.
/// </summary>
public static class SystemAttributes
{
    private const int English = 1033;

    public static string PrimaryIdName(EntityMetadata e) => e.PrimaryIdAttribute ?? PrimaryIdName(e.LogicalName);

    /// <summary>
    /// The primary key name the server gives an entity: <c>{entityLogicalName}id</c>, except for the seeded tables whose
    /// Dataverse key is named otherwise (<c>duplicaterecord</c>: <c>duplicateid</c>, as its column <c>duplicaterecordid</c>
    /// is a lookup).
    /// </summary>
    public static string PrimaryIdName(string entityLogicalName) =>
        entityLogicalName == DuplicateDetectionSchema.Record ? DuplicateDetectionSchema.RecordId : entityLogicalName + "id";

    private static readonly HashSet<string> GeneratedNames = new(StringComparer.Ordinal)
    {
        "createdon", "createdby", "modifiedon", "modifiedby", "owninguser", "owningteam", "owningbusinessunit",
        "statecode", "statuscode", TypeMapping.VersionNumber, "ownerid", "ownerid" + TypeMapping.TypeColumnSuffix,
    };

    /// <summary>
    /// True for names of attributes/columns the server generates on every entity (primary key, system attributes,
    /// <c>ownerid</c> and its type column); callers cannot define attributes with these names.
    /// </summary>
    public static bool IsReservedName(string entityLogicalName, string? attributeLogicalName) =>
        attributeLogicalName is not null
        && (attributeLogicalName == PrimaryIdName(entityLogicalName) || GeneratedNames.Contains(attributeLogicalName));

    /// <summary>
    /// Returns the primary key first, then <paramref name="attrs"/> in order (each money attribute followed by its
    /// <c>_base</c> attribute), then the currency pair when there is a money attribute (<see cref="CurrencyLookup"/>,
    /// <see cref="ExchangeRate"/>), then the missing system attributes. Attributes the caller already supplied (matched
    /// by logical name) are kept and never duplicated; the inputs are not modified. Idempotent.
    /// </summary>
    public static IReadOnlyList<AttributeMetadata> Complete(EntityMetadata e, IEnumerable<AttributeMetadata> attrs)
    {
        var given = attrs.ToList();
        var names = given.Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        var primaryIdName = PrimaryIdName(e);
        var result = new List<AttributeMetadata>(given.Count + 16);

        void AddGenerated(AttributeMetadata a)
        {
            if (!names.Add(a.LogicalName)) return;
            SdkProperty.Set(a, nameof(AttributeMetadata.EntityLogicalName), e.LogicalName);
            result.Add(a);
        }

        var primaryId = given.FirstOrDefault(a => a.LogicalName == primaryIdName);
        if (primaryId is null) AddGenerated(PrimaryId(e.LogicalName, primaryIdName));
        else result.Add(primaryId);

        foreach (var a in given)
        {
            if (ReferenceEquals(a, primaryId)) continue;
            result.Add(a);
            if (a is MoneyAttributeMetadata { IsBaseCurrency: not true } money) AddGenerated(MoneyBase(money));
        }
        // A table with money has the currency pair, once (ruling X4); a definition that already holds it keeps it (pre-flight C6).
        if (given.Any(a => a is MoneyAttributeMetadata { IsBaseCurrency: not true }))
        {
            AddGenerated(CurrencyLookup());
            AddGenerated(ExchangeRate());
        }

        // The audit table has only auditid and createdon of the generic columns (Dataverse's audit table has no owner, state,
        // version or modified columns).
        // An elastic table has no state: its own system columns come in place of statecode and statuscode (plan 3.4).
        // postcomment has no modifiedon and no modifiedby: a comment is never updated (plan 3.5 UI-1g, as on Learn).
        var elastic = ElasticTables.IsElastic(e);
        foreach (var a in Standard().Where(a => e.LogicalName != AuditTable.Name || a.LogicalName == AuditTable.CreatedOn)
                     .Where(a => !elastic || a.LogicalName is not ("statecode" or "statuscode"))
                     .Where(a => e.LogicalName != PostSchema.Comment || a.LogicalName is not ("modifiedon" or "modifiedby"))) AddGenerated(a);
        if (elastic)
        {
            AddGenerated(PartitionId());
            AddGenerated(TtlInSeconds());
        }
        if (e.OwnershipType is OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned) AddGenerated(Owner());

        return result;
    }

    /// <summary>
    /// <c>partitionid</c> of an elastic table: String, 100 characters, not required; given on create, never changed.
    /// </summary>
    public static StringAttributeMetadata PartitionId() => new()
    {
        LogicalName = ElasticTables.PartitionId,
        SchemaName = "PartitionId",
        DisplayName = EnglishLabel("Partition Id"),
        MaxLength = ElasticTables.PartitionIdMaxLength,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        IsValidForCreate = true,
        IsValidForUpdate = false,
    };

    /// <summary><c>ttlinseconds</c> of an elastic table: the seconds a row lives after its last change, 1 to 2147483647, not required.</summary>
    public static IntegerAttributeMetadata TtlInSeconds() => new()
    {
        LogicalName = ElasticTables.TtlInSeconds,
        SchemaName = "TTLInSeconds",
        DisplayName = EnglishLabel("Time To Live (seconds)"),
        MinValue = 1,
        MaxValue = int.MaxValue,
        Format = IntegerFormat.None,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        IsValidForCreate = true,
        IsValidForUpdate = true,
    };

    /// <summary>Primary key attribute <c>{entityLogicalName}id</c>.</summary>
    public static UniqueIdentifierAttributeMetadata PrimaryId(string entityLogicalName, string? logicalName = null)
    {
        var a = new UniqueIdentifierAttributeMetadata(logicalName ?? entityLogicalName + "id")
        {
            LogicalName = logicalName ?? entityLogicalName + "id",
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
            IsValidForCreate = true,
            IsValidForUpdate = false,
        };
        SdkProperty.Set(a, nameof(AttributeMetadata.IsPrimaryId), true);
        SdkProperty.Set(a, nameof(AttributeMetadata.EntityLogicalName), entityLogicalName);
        return a;
    }

    /// <summary><c>ownerid</c>: polymorphic lookup (AttributeType Owner) to systemuser/team.</summary>
    public static LookupAttributeMetadata Owner()
    {
        var a = new LookupAttributeMetadata
        {
            LogicalName = "ownerid",
            SchemaName = "OwnerId",
            DisplayName = EnglishLabel("Owner"),
            Targets = ["systemuser", "team"],
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        };
        SdkProperty.Set(a, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Owner);
        SdkProperty.Set(a, nameof(AttributeMetadata.AttributeTypeName), AttributeTypeDisplayName.OwnerType);
        return a;
    }

    /// <summary>
    /// The <c>{name}_base</c> money attribute holding the value in base currency, with the Dataverse metadata of one (plan 2.10,
    /// observed): Precision 4, ±922337203685477, PrecisionSource 2, not valid for create or update, <c>IsBaseCurrency</c> true.
    /// </summary>
    public static MoneyAttributeMetadata MoneyBase(MoneyAttributeMetadata money)
    {
        var a = new MoneyAttributeMetadata(money.SchemaName is null ? null : money.SchemaName + "_Base")
        {
            LogicalName = money.LogicalName + TypeMapping.BaseColumnSuffix,
            DisplayName = money.DisplayName?.UserLocalizedLabel is { } label
                ? CreateLabel(label.Label + " (Base)", label.LanguageCode)
                : null,
            IsValidForCreate = false,
            IsValidForUpdate = false,
        };
        SetBaseMoneyMetadata(a);
        SdkProperty.Set(a, nameof(MoneyAttributeMetadata.IsBaseCurrency), true);
        return a;
    }

    /// <summary>
    /// Gives <paramref name="baseMoney"/> the fixed metadata of a <c>{money}_base</c> attribute (Precision, PrecisionSource, range);
    /// returns true when one of them changed. Used by <see cref="MoneyBase"/> and by the upgrade of a <c>_base</c> made before plan 2.10.
    /// </summary>
    public static bool SetBaseMoneyMetadata(MoneyAttributeMetadata baseMoney)
    {
        ArgumentNullException.ThrowIfNull(baseMoney);
        var changed = baseMoney.Precision != CurrencySchema.BaseMoneyPrecision
                      || baseMoney.PrecisionSource != CurrencySchema.BaseMoneyPrecisionSource
                      || baseMoney.MinValue != (double)CurrencySchema.MinBaseMoney
                      || baseMoney.MaxValue != (double)CurrencySchema.MaxBaseMoney;
        baseMoney.Precision = CurrencySchema.BaseMoneyPrecision;
        baseMoney.PrecisionSource = CurrencySchema.BaseMoneyPrecisionSource;
        baseMoney.MinValue = (double)CurrencySchema.MinBaseMoney;
        baseMoney.MaxValue = (double)CurrencySchema.MaxBaseMoney;
        return changed;
    }

    /// <summary>
    /// <c>transactioncurrencyid</c>: the lookup of a record to its currency (valid for create and update, not required). With
    /// <see cref="ExchangeRate"/> it is the currency pair of a table with money columns; account and contact have it in their seed
    /// files (decision D1), which hold this same definition (pre-flight C6, checked by a test).
    /// </summary>
    public static LookupAttributeMetadata CurrencyLookup() => new()
    {
        LogicalName = CurrencySchema.CurrencyId,
        SchemaName = "TransactionCurrencyId",
        DisplayName = EnglishLabel("Currency"),
        Targets = [CurrencySchema.Table],
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        IsValidForCreate = true,
        IsValidForUpdate = true,
    };

    /// <summary>
    /// <c>exchangerate</c>: the rate of the record's currency when the record was written, computed by the server (Precision 10,
    /// 1E-10 to 1E11, not valid for create or update). The other half of the currency pair (<see cref="CurrencyLookup"/>).
    /// </summary>
    public static DecimalAttributeMetadata ExchangeRate() => new()
    {
        LogicalName = CurrencySchema.ExchangeRate,
        SchemaName = "ExchangeRate",
        DisplayName = EnglishLabel("Exchange Rate"),
        Precision = CurrencySchema.RecordRateDecimals,
        MinValue = CurrencySchema.MinRate,
        MaxValue = CurrencySchema.MaxRate,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        IsValidForCreate = false,
        IsValidForUpdate = false,
    };

    private static IEnumerable<AttributeMetadata> Standard()
    {
        yield return ReadOnly(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime, "CreatedOn") { LogicalName = "createdon" }, "Created On");
        yield return ReadOnly(Lookup("createdby", "CreatedBy", "systemuser"), "Created By");
        yield return ReadOnly(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime, "ModifiedOn") { LogicalName = "modifiedon" }, "Modified On");
        yield return ReadOnly(Lookup("modifiedby", "ModifiedBy", "systemuser"), "Modified By");
        yield return ReadOnly(Lookup("owninguser", "OwningUser", "systemuser"), "Owning User");
        yield return ReadOnly(Lookup("owningteam", "OwningTeam", "team"), "Owning Team");
        yield return ReadOnly(Lookup("owningbusinessunit", "OwningBusinessUnit", "businessunit"), "Owning Business Unit");
        yield return StateCode();
        yield return StatusCode();
        yield return ReadOnly(new BigIntAttributeMetadata("VersionNumber") { LogicalName = TypeMapping.VersionNumber }, "Version Number");
    }

    /// <summary>A single-language label with <c>UserLocalizedLabel</c> set, as the Web API returns it.</summary>
    public static Label CreateLabel(string text, int languageCode) =>
        new(new LocalizedLabel(text, languageCode), new[] { new LocalizedLabel(text, languageCode) });

    private static Label EnglishLabel(string text) => CreateLabel(text, English);

    private static LookupAttributeMetadata Lookup(string logicalName, string schemaName, string target) =>
        new() { LogicalName = logicalName, SchemaName = schemaName, Targets = [target] };

    private static AttributeMetadata ReadOnly(AttributeMetadata a, string displayName)
    {
        a.DisplayName = EnglishLabel(displayName);
        a.IsValidForCreate = false;
        a.IsValidForUpdate = false;
        return a;
    }

    private static StateAttributeMetadata StateCode()
    {
        var options = new OptionSetMetadata(new OptionMetadataCollection
        {
            new StateOptionMetadata { Value = 0, Label = EnglishLabel("Active"), DefaultStatus = 1 },
            new StateOptionMetadata { Value = 1, Label = EnglishLabel("Inactive"), DefaultStatus = 2 },
        }) { IsGlobal = false, OptionSetType = OptionSetType.State };
        return new StateAttributeMetadata
        {
            LogicalName = "statecode",
            SchemaName = "StateCode",
            DisplayName = EnglishLabel("Status"),
            OptionSet = options,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        };
    }

    private static StatusAttributeMetadata StatusCode()
    {
        var options = new OptionSetMetadata(new OptionMetadataCollection
        {
            new StatusOptionMetadata(1, 0) { Label = EnglishLabel("Active") },
            new StatusOptionMetadata(2, 1) { Label = EnglishLabel("Inactive") },
        }) { IsGlobal = false, OptionSetType = OptionSetType.Status };
        return new StatusAttributeMetadata
        {
            LogicalName = "statuscode",
            SchemaName = "StatusCode",
            DisplayName = EnglishLabel("Status Reason"),
            OptionSet = options,
        };
    }
}
