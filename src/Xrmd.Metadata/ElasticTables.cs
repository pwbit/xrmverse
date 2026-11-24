using Xrmd.Model.Metadata;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata;

/// <summary>
/// The table type of a table (<c>Standard</c> or <c>Elastic</c>, plan 3.4) and what an elastic table is: its system columns'
/// names, its read-only alternate key over the primary id and the partition id, the partition id value rule and the features
/// it refuses (ruling E9). Every check throws 400 <see cref="ErrorCodes.InvalidArgument"/>.
/// </summary>
public static partial class ElasticTables
{
    public const string TableTypeStandard = "Standard";
    public const string TableTypeElastic = "Elastic";

    /// <summary>The partition id column of an elastic table.</summary>
    public const string PartitionId = "partitionid";

    /// <summary>The time to live column of an elastic table, in seconds.</summary>
    public const string TtlInSeconds = "ttlinseconds";

    /// <summary>Logical name of the key every elastic table reports over its primary id and <see cref="PartitionId"/>.</summary>
    public const string KeyLogicalName = "keyfornosqlentitywithpkpartitionid";

    private const string KeySchemaName = "KeyForNoSqlEntityWithPKPartitionId";

    /// <summary>The longest partition id value, and the <see cref="PartitionId"/> column's <c>MaxLength</c>.</summary>
    public const int PartitionIdMaxLength = 100;

    private const string ForbiddenPartitionCharacters = "/<>*%&:\\?+";

    /// <summary>True when <paramref name="e"/> is an elastic table.</summary>
    public static bool IsElastic(TableDefinition e) =>
        e is not null && string.Equals(e.TableType, TableTypeElastic, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table type as stored and shown: <c>Standard</c> for null, empty or any case of it, <c>Elastic</c> for any case of it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming any other value.</exception>
    public static string NormalizeTableType(string? value)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, TableTypeStandard, StringComparison.OrdinalIgnoreCase)) return TableTypeStandard;
        if (string.Equals(value, TableTypeElastic, StringComparison.OrdinalIgnoreCase)) return TableTypeElastic;
        throw XrmdFaultException.InvalidArgument($"TableType '{value}' is not supported by xrmverse (only Standard and Elastic).");
    }

    /// <summary>
    /// The alternate key metadata of an elastic table: primary id plus <see cref="PartitionId"/>. Read only and computed on
    /// every read, never stored.
    /// </summary>
    public static TableKey NoSqlKey(TableDefinition e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new TableKey
        {
            LogicalName = KeyLogicalName,
            SchemaName = KeySchemaName,
            KeyAttributes = [SystemAttributes.PrimaryIdName(e), PartitionId],
            EntityLogicalName = e.LogicalName,
            // The key is always usable, so Active.
            EntityKeyIndexStatus = KeyIndexState.Active,
            IsCustomizable = ModelFactory.Flag(false),
        };
    }

    /// <summary>The partition column a lookup to an elastic table gets on its referencing table: <c>{lookup}pid</c>.</summary>
    public static string PartitionColumnOf(string lookupLogicalName) => lookupLogicalName + "pid";

    /// <summary>A partition id value is at most <see cref="PartitionIdMaxLength"/> characters and holds none of <c>/ &lt; &gt; * % &amp; : \ ? +</c>; null passes.</summary>
    /// <exception cref="XrmdFaultException">400 <c>Invalid partitionid value: ...</c>.</exception>
    public static void DemandValidPartitionId(string? value)
    {
        if (value is null) return;
        if (value.Length > PartitionIdMaxLength)
            throw XrmdFaultException.InvalidArgument($"Invalid partitionid value: it has {value.Length} characters, at most {PartitionIdMaxLength} are allowed.");
        if (value.IndexOfAny(ForbiddenPartitionCharacters.ToCharArray()) is var at and >= 0)
            throw XrmdFaultException.InvalidArgument($"Invalid partitionid value: the character '{value[at]}' is not allowed.");
    }

    /// <summary>
    /// A create or update of a table definition: the type must be known and, for an elastic table, the settings it cannot have
    /// are refused (ownership by team, activity, access teams, duplicate detection).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the table and the setting.</exception>
    public static void DemandValidEntity(TableDefinition e)
    {
        ArgumentNullException.ThrowIfNull(e);
        NormalizeTableType(e.TableType);
        if (!IsElastic(e)) return;
        var name = $"The elastic table '{e.LogicalName}'";
        if (e.OwnershipType is not (null or TableOwnership.UserOwned or TableOwnership.OrganizationOwned))
            throw XrmdFaultException.InvalidArgument($"{name} cannot be {e.OwnershipType}: only user and organization ownership are possible.");
        if (e.IsActivity == true) throw XrmdFaultException.InvalidArgument($"{name} cannot be an activity table.");
        if (e.AutoCreateAccessTeams == true) throw XrmdFaultException.InvalidArgument($"{name} does not support access teams.");
        if (e.IsDuplicateDetectionEnabled?.Value == true)
            throw XrmdFaultException.InvalidArgument($"{name} does not support duplicate detection.");
    }

    /// <summary>
    /// A column defined on an elastic table (not a generated one): refused are Money, multi-select choice, state, status, image,
    /// calculated or rollup columns, an Integer with a format, a Customer lookup and the names of the system columns.
    /// Anything else passes (a String or Memo with <c>FormatName.Json</c> too).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the table and the column.</exception>
    public static void DemandValidAttribute(TableDefinition e, ColumnDefinition a)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(a);
        if (!IsElastic(e)) return;
        var where = $"Column '{a.LogicalName}' of elastic table '{e.LogicalName}'";
        if (a.LogicalName is PartitionId or TtlInSeconds)
            throw XrmdFaultException.InvalidArgument($"{where}: the name belongs to a system column of every elastic table.");
        var refused = a switch
        {
            MoneyColumn => "a Money column",
            MultiChoiceColumn => "a multi-select choice column",
            StateColumn => "a state column",
            StatusColumn => "a status column",
            ImageColumn => "an image column",
            _ when CustomerColumns.IsCustomer(a) => "a Customer lookup",
            _ when a.SourceType is > 0 => "a calculated, rollup or formula column",
            IntegerColumn { Format: not (null or IntegerDisplayFormat.None) } => "an Integer column with a format",
            _ => null,
        };
        if (refused is not null) throw XrmdFaultException.InvalidArgument($"{where} cannot be {refused}: elastic tables do not support it.");
    }

    /// <summary>
    /// A one-to-many relationship with an elastic side cascades nothing: every action of its <c>CascadeSettings</c> is
    /// <c>NoCascade</c> (or not given).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the relationship and the action.</exception>
    public static void DemandValidRelationship(OneToManyRelationship r, TableDefinition referenced, TableDefinition referencing)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentNullException.ThrowIfNull(referencing);
        if (!IsElastic(referenced) && !IsElastic(referencing)) return;
        if (r.CascadeConfiguration is not { } cascade) return;
        var actions = new (string Name, CascadeAction? Value)[]
        {
            ("Assign", cascade.Assign), ("Delete", cascade.Delete), ("Merge", cascade.Merge), ("Reparent", cascade.Reparent),
            ("Share", cascade.Share), ("Unshare", cascade.Unshare), ("Archive", cascade.Archive), ("RollupView", cascade.RollupView),
        };
        if (actions.FirstOrDefault(x => x.Value is not (null or CascadeAction.NoCascade)) is { Value: { } value } action)
            throw XrmdFaultException.InvalidArgument(
                $"Relationship '{r.SchemaName}': CascadeConfiguration.{action.Name} = {value} is not possible with the elastic table '{(IsElastic(referenced) ? referenced : referencing).LogicalName}' (use NoCascade).");
    }

    /// <summary>
    /// The columns an Upsert that replaces a row of <paramref name="e"/> sets to null (ruling E6): each column a caller can update
    /// that <paramref name="isSet"/> says the request does not carry, among the custom columns and <see cref="TtlInSeconds"/>. The primary id, the
    /// partition id, the owner, generated columns, files and images are kept. The core's replace and the security guard's field
    /// checks use this one list.
    /// </summary>
    public static IReadOnlyList<string> UpsertClearedColumns(TableDefinition e, Func<string, bool> isSet)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(isSet);
        var pk = SystemAttributes.PrimaryIdName(e);
        return (e.Attributes ?? [])
            .Where(a => a.LogicalName != pk && a.LogicalName != PartitionId && !isSet(a.LogicalName!)
                        && a.AttributeOf is null && a is not (FileColumn or ImageColumn)
                        && !TypeMapping.IsStoredByOtherAttribute(a) && a.IsValidForUpdate != false
                        && (a.LogicalName == TtlInSeconds || MetadataStore.IsCustomAttribute(e, a)))
            .Select(a => a.LogicalName!)
            .ToList();
    }

    /// <summary>A feature an elastic table does not have.</summary>
    /// <param name="feature">What is refused, in the plural or as a noun phrase ("many-to-many relationships").</param>
    /// <exception cref="XrmdFaultException">400 naming the feature and the table.</exception>
    public static void DemandNotElastic(TableDefinition e, string feature)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (IsElastic(e)) throw XrmdFaultException.InvalidArgument($"{feature} are not supported by the elastic table '{e.LogicalName}'.");
    }
}
