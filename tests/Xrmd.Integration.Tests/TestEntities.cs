using Xrmd.Abstractions.Storage;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Integration.Tests;

/// <summary>Custom entities shared by the tests of the "xrmd" collection (one database per collection).</summary>
public static class TestEntities
{
    public const string Project = "pb_project";
    public const string ProjectSet = "pb_projects";
    public const string Pipe = "pb_pipe";
    public const string PipeSet = "pb_pipes";
    public const string PipeLog = "pb_pipelog";
    public const string PipeLog2 = "pb_pipe2log";
    public const string PipeParent = "pb_pipeparent";
    public const string Tag = "new_tag";
    public const string TagSet = "new_tags";

    /// <summary>The many-to-many relationship between <c>pb_project</c> (Entity1) and <c>new_tag</c>; also its intersect entity.</summary>
    public const string ProjectTag = "new_project_new_tag";

    /// <summary>The 1:N relationship derived from the <c>pb_parentid</c> lookup of <c>pb_pipe</c> to <c>pb_pipeparent</c>.</summary>
    public const string PipeParentRelationship = "pb_pipe_pb_parentid_pb_pipeparent";

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static Label Bilingual(string english, string italian) =>
        new(new LocalizedLabel(english, 1033), new[] { new LocalizedLabel(english, 1033), new LocalizedLabel(italian, 1040) });

    /// <summary>
    /// Creates <c>pb_project</c> (user-owned; pb_name String(200) primary name, pb_budget Money, pb_customerid Lookup→systemuser,
    /// pb_startdate DateTime, pb_count Integer, pb_ratio Double, pb_priority Picklist 1 Low/2 Medium/3 High, with Italian labels Bassa/Media for 1 and 2) unless it exists.
    /// </summary>
    public static async Task EnsureProjectAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Project) is not null) return;
            var entity = new EntityMetadata { LogicalName = Project, SchemaName = "pb_Project", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new MoneyAttributeMetadata { LogicalName = "pb_budget", SchemaName = "pb_Budget" },
                new LookupAttributeMetadata { LogicalName = "pb_customerid", SchemaName = "pb_CustomerId", Targets = ["systemuser"] },
                new DateTimeAttributeMetadata { LogicalName = "pb_startdate", SchemaName = "pb_StartDate" },
                new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" },
                new DoubleAttributeMetadata { LogicalName = "pb_ratio", SchemaName = "pb_Ratio" },
                new PicklistAttributeMetadata
                {
                    LogicalName = "pb_priority",
                    SchemaName = "pb_Priority",
                    OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                    {
                        // Italian labels for 1 and 2 only (#109: the user's language, falling back to 1033).
                        new OptionMetadata(Bilingual("Low", "Bassa"), 1),
                        new OptionMetadata(Bilingual("Medium", "Media"), 2),
                        new OptionMetadata(SystemAttributes.CreateLabel("High", 1033), 3),
                    }) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
                },
            ]);
            var name = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 };
            await store.CreateEntityAsync(entity, name);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Creates the plug-in pipeline test entities unless they exist: <c>pb_pipe</c> (user-owned; pb_name String(200)
    /// primary name, pb_count Integer, pb_data Memo), <c>pb_pipelog</c> and <c>pb_pipe2log</c> (pb_name String(200), pb_data Memo).
    /// </summary>
    public static async Task EnsurePipeAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Pipe) is null)
            {
                var pipe = new EntityMetadata { LogicalName = Pipe, SchemaName = "pb_Pipe", OwnershipType = OwnershipTypes.UserOwned };
                pipe.SetAttributes(
                [
                    new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" },
                    new MemoAttributeMetadata { LogicalName = "pb_data", SchemaName = "pb_Data", MaxLength = 100000 },
                ]);
                await store.CreateEntityAsync(pipe, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 });
            }

            if (store.GetEntity(PipeLog) is null)
            {
                var log = new EntityMetadata { LogicalName = PipeLog, SchemaName = "pb_PipeLog", OwnershipType = OwnershipTypes.UserOwned };
                log.SetAttributes([new MemoAttributeMetadata { LogicalName = "pb_data", SchemaName = "pb_Data", MaxLength = 100000 }]);
                await store.CreateEntityAsync(log, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 });
            }

            if (store.GetEntity(PipeLog2) is null)
            {
                var log2 = new EntityMetadata { LogicalName = PipeLog2, SchemaName = "pb_Pipe2Log", OwnershipType = OwnershipTypes.UserOwned };
                log2.SetAttributes([new MemoAttributeMetadata { LogicalName = "pb_data", SchemaName = "pb_Data", MaxLength = 100000 }]);
                await store.CreateEntityAsync(log2, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 });
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// <see cref="EnsurePipeAsync"/>, then <c>pb_pipeparent</c> (pb_name String(200) primary name) and the <c>pb_parentid</c>
    /// lookup of <c>pb_pipe</c> to it, unless they exist.
    /// </summary>
    public static async Task EnsurePipeParentAsync(IMetadataStore store)
    {
        await EnsurePipeAsync(store);
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(PipeParent) is null)
            {
                var parent = new EntityMetadata { LogicalName = PipeParent, SchemaName = "pb_PipeParent", OwnershipType = OwnershipTypes.UserOwned };
                parent.SetAttributes([]);
                await store.CreateEntityAsync(parent, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 });
            }

            if (store.GetEntity(Pipe)!.Attributes.All(a => a.LogicalName != "pb_parentid"))
                await store.AddAttributeAsync(Pipe,
                    new LookupAttributeMetadata { LogicalName = "pb_parentid", SchemaName = "pb_ParentId", Targets = [PipeParent] });
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// <see cref="EnsureProjectAsync"/>, then <c>new_tag</c> (user-owned; new_name String(100) primary name) and the
    /// many-to-many relationship <c>new_project_new_tag</c> (<c>pb_project</c> ↔ <c>new_tag</c>), unless they exist.
    /// </summary>
    public static async Task EnsureProjectTagAsync(IMetadataStore store, string connectionString)
    {
        await EnsureProjectAsync(store);
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Tag) is null)
            {
                var tag = new EntityMetadata { LogicalName = Tag, SchemaName = Tag, OwnershipType = OwnershipTypes.UserOwned };
                tag.SetAttributes([]);
                await store.CreateEntityAsync(tag, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
            }

            if (store.GetRelationship(ProjectTag) is null)
                await InSessionAsync(connectionString, s => store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
                {
                    SchemaName = ProjectTag,
                    Entity1LogicalName = Project,
                    Entity2LogicalName = Tag,
                    IntersectEntityName = ProjectTag,
                }, default));
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The table of the classic workflow parity export (plan 2.14, <c>tests/fixtures/workflows/real</c>).</summary>
    public const string Order = "xrmv_order";

    /// <summary>
    /// Creates <c>xrmv_order</c> as the parity export declares it, unless it exists: user-owned, activities enabled (a task's
    /// regarding), xrmv_name String(100) primary name, xrmv_amount Decimal, xrmv_status Picklist (128830000, 128830001) and
    /// xrmv_accountid Lookup→account.
    /// </summary>
    public static async Task EnsureOrderAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Order) is not null) return;
            var entity = new EntityMetadata
            {
                LogicalName = Order, SchemaName = "xrmv_Order", OwnershipType = OwnershipTypes.UserOwned, HasActivities = true,
            };
            entity.SetAttributes(
            [
                new DecimalAttributeMetadata { LogicalName = "xrmv_amount", SchemaName = "xrmv_Amount", Precision = 2 },
                new LookupAttributeMetadata { LogicalName = "xrmv_accountid", SchemaName = "xrmv_AccountId", Targets = ["account"] },
                new PicklistAttributeMetadata
                {
                    LogicalName = "xrmv_status",
                    SchemaName = "xrmv_Status",
                    OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                    {
                        new OptionMetadata(SystemAttributes.CreateLabel("New", 1033), 128830000),
                        new OptionMetadata(SystemAttributes.CreateLabel("New Action", 1033), 128830001),
                    }) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
                },
            ]);
            await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_Name", MaxLength = 100 });
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Runs <paramref name="write"/> in a session of its own, committed when it succeeds and rolled back otherwise.</summary>
    public static async Task InSessionAsync(string connectionString, Func<DataSession, Task> write)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session);
        }
        catch
        {
            await tx.RollbackAsync();
            session.RolledBack();
            throw;
        }
        await tx.CommitAsync();
        session.Committed();
    }
}
