using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// Golden files of the remote execution context as the SDK-based serializers write it today (the <c>asyncoperation.data</c>
/// XML and the webhook JSON body). They are a contract: S.2e2 must write the same bytes. The files live in
/// <c>tests/fixtures/remote-context-golden/</c>, were captured from the code of the S.2e1 branch and are compared byte for byte.
/// A missing file is written once and the test fails; an existing file is never rewritten.
/// </summary>
public class RemoteContextGoldenTests
{
    private static readonly Guid Id1 = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Id3 = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Id4 = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Id5 = new("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime When = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static Entity Target()
    {
        var inner = new Entity("contact", Id2) { ["fullname"] = "Inner", ["balance"] = new Money(1.5m) };
        var e = new Entity("account", Id1) { RowVersion = "12345", EntityState = EntityState.Changed };
        e["name"] = "Contoso";
        e["count"] = 7;
        e["big"] = 9007199254740993L;
        e["ratio"] = 0.25d;
        e["flag"] = true;
        e["when"] = When;
        e["guid"] = Id3;
        e["choice"] = new OptionSetValue(3);
        e["choices"] = new OptionSetValueCollection { new OptionSetValue(1), new OptionSetValue(2) };
        e["revenue"] = new Money(99.95m);
        e["owner"] = new EntityReference("systemuser", Id4) { Name = "Me" };
        e["nested"] = inner;
        e["page"] = new EntityCollection(new List<Entity> { inner }) { EntityName = "contact", MoreRecords = true, PagingCookie = "<cookie page=\"1\" />", TotalRecordCount = 17, TotalRecordCountLimitExceeded = false };
        e["parties"] = new EntityReferenceCollection { new EntityReference("contact", Id2), new EntityReference("account", Id1) };
        e["aliased"] = new AliasedValue("contact", "fullname", "Inner");
        e["bytes"] = new byte[] { 1, 2, 3 };
        e["amount"] = 12.5m;
        e["columns"] = new ColumnSet("name", "revenue");
        e["relationship"] = new Relationship("contact_customer_accounts") { PrimaryEntityRole = EntityRole.Referenced };
        e["keyedref"] = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-1" }, { "code", 42 } }) { Name = "Keyed", RowVersion = "77" };
        e["query"] = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = 5 };
        e["refs"] = new[] { new EntityReference("a", Id1), new EntityReference("b", Id2) };
        e["entities"] = new[] { inner };
        e["choicearray"] = new[] { new OptionSetValue(4), new OptionSetValue(5) };
        e["moneys"] = new[] { new Money(1m), new Money(2.25m) };
        e["aliasedrecord"] = new AliasedValue("contact", "parent", inner);
        e["aliasedpage"] = new AliasedValue("contact", "parents", new EntityCollection(new List<Entity> { inner }));
        e["aliasedmoney"] = new AliasedValue("c", "m", new Money(2m));
        e["nothing"] = null;
        e.FormattedValues["choice"] = "Three";
        e.KeyAttributes["accountnumber"] = "A-1";
        e.KeyAttributes["code"] = 42;
        e.RelatedEntities[new Relationship("contact_customer_accounts") { PrimaryEntityRole = EntityRole.Referenced }] =
            new EntityCollection(new List<Entity> { inner }) { EntityName = "contact" };
        e.RelatedEntities[new Relationship("other")] = new EntityCollection();
        return e;
    }

    internal static RemoteExecutionContext Build()
    {
        var target = Target();
        var context = new XrmdPluginExecutionContext
        {
            MessageName = "Update", PrimaryEntityName = "account", PrimaryEntityId = Id1, SecondaryEntityName = "none",
            Stage = 40, Mode = 1, Depth = 2,
            RequestId = Id5, UserId = Id4, InitiatingUserId = Id4, BusinessUnitId = Id3, OrganizationId = Id2,
            OrganizationName = "contoso", CorrelationId = Id5, OperationId = Id1, OperationCreatedOn = When,
            OwningExtension = new EntityReference("sdkmessageprocessingstep", Id3) { Name = "step" },
            ParentContext = new XrmdPluginExecutionContext { MessageName = "Create", PrimaryEntityName = "account", Stage = 30, Mode = 0, Depth = 1, OperationCreatedOn = When,
                UserId = Id4, OrganizationId = Id2, CorrelationId = Id5 },
        };
        ((XrmdPluginExecutionContext)context.ParentContext).SharedVariables["parentvar"] = "p";
        ((XrmdPluginExecutionContext)context.ParentContext).InputParameters["Target"] = new Entity("account", Id1) { ["name"] = "Before" };
        context.InputParameters["Target"] = target;
        context.OutputParameters["id"] = Id1;
        context.OutputParameters["count"] = 3;
        context.SharedVariables["counter"] = 42;
        context.SharedVariables["label"] = "x";
        context.PreEntityImages["pre"] = new Entity("account", Id1) { ["name"] = "Old", ["revenue"] = new Money(5m) };
        context.PostEntityImages["post"] = new Entity("account", Id1) { ["name"] = "Contoso", ["owner"] = new EntityReference("systemuser", Id4) };
        return AsyncContextSerializer.ToRemote(context);
    }

    private static string GoldenDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "fixtures", "remote-context-golden");
    }

    private static void AssertGolden(string file, byte[] actual)
    {
        var path = Path.Combine(GoldenDirectory(), file);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(actual);
            Assert.Fail($"golden file {file} written, review and commit");
        }
        Assert.Equal(File.ReadAllBytes(path), actual);
    }

    private static byte[] AsyncBytes() => new UTF8Encoding(false).GetBytes(AsyncContextSerializer.Serialize(Build()));

    [Fact] public void Async_context_is_deterministic() => Assert.Equal(AsyncBytes(), AsyncBytes());
    [Fact] public void Webhook_payload_is_deterministic() => Assert.Equal(WebhookPayload.Serialize(Build()), WebhookPayload.Serialize(Build()));
    [Fact] public void Async_context_matches_golden_file() => AssertGolden("async-context.xml", AsyncBytes());
    [Fact] public void Webhook_payload_matches_golden_file() => AssertGolden("webhook.json", WebhookPayload.Serialize(Build()));
}
