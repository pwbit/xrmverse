using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Pipeline.Bulk;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// S.2e3c3a Task 2: the model half of <see cref="BulkTargets"/> reads, checks and names the targets of a model bulk request exactly as
/// the SDK half does for the twin request (same faults, texts and ids).
/// </summary>
public class BulkTargetsModelTests
{
    private static readonly string[] Messages =
        [BulkTargets.CreateMultiple, BulkTargets.UpdateMultiple, BulkTargets.UpsertMultiple, BulkTargets.DeleteMultiple];

    private static readonly TableDefinition Account = new() { LogicalName = "account", PrimaryIdAttribute = "accountid" };

    private static Request Bulk(string message, object? targets, bool withTargets = true)
    {
        var request = new Request { Name = message };
        if (withTargets) request.Parameters[BulkTargets.TargetsParameter] = targets;
        return request;
    }

    private static object Targets(string message, params object?[] items) => message == BulkTargets.DeleteMultiple
        ? AsReferences(items)
        : new RecordCollection { EntityName = "account", Records = [.. items.Cast<Record>()] };

    private static RecordReferenceCollection AsReferences(object?[] items)
    {
        var references = new RecordReferenceCollection();
        references.AddRange(items.Cast<RecordReference>());
        return references;
    }

    private static Exception Caught(Action action)
    {
        var caught = Xunit.Record.Exception(action);
        caught.Should().NotBeNull();
        return caught!;
    }

    private static void ShouldMatch(Exception model, Exception sdk)
    {
        model.GetType().Should().Be(sdk.GetType());
        model.Message.Should().Be(sdk.Message);
        if (sdk is XrmdFaultException fault)
        {
            var modelFault = (XrmdFaultException)model;
            modelFault.ErrorCode.Should().Be(fault.ErrorCode);
            modelFault.HttpStatus.Should().Be(fault.HttpStatus);
        }
    }

    [Fact]
    public void Read_faults_match_the_sdk_read()
    {
        foreach (var message in Messages)
        {
            object wrongType = message == BulkTargets.DeleteMultiple
                ? new RecordCollection { EntityName = "account", Records = [new Record("account", Guid.NewGuid())] }
                : AsReferences([new RecordReference { LogicalName = "account", Id = Guid.NewGuid() }]);
            var cases = new[]
            {
                Bulk(message, null, withTargets: false),
                Bulk(message, null),
                Bulk(message, wrongType),
                Bulk(message, message == BulkTargets.DeleteMultiple ? new RecordReferenceCollection() : new RecordCollection()),
            };
            foreach (var request in cases)
            {
                var sdk = new SdkMirror().Out(request)!;
                var model = Caught(() => BulkTargets.Read(request));
                model.Should().BeOfType<XrmdFaultException>();
                ShouldMatch(model, Caught(() => BulkTargets.Read(sdk)));
            }
        }

        var notBulk = new Request { Name = "Create", Parameters = { ["Target"] = new Record("account") } };
        var notBulkSdk = new SdkMirror().Out(notBulk)!;
        var refused = Caught(() => BulkTargets.Read(notBulk));
        refused.Should().BeOfType<ArgumentException>().Which.Message.Should().Be("'Create' is not a bulk message. (Parameter 'request')");
        ShouldMatch(refused, Caught(() => BulkTargets.Read(notBulkSdk)));
    }

    [Fact]
    public void Read_builds_model_single_requests()
    {
        var singles = new Dictionary<string, string>
        {
            [BulkTargets.CreateMultiple] = "Create",
            [BulkTargets.UpdateMultiple] = "Update",
            [BulkTargets.UpsertMultiple] = "Upsert",
            [BulkTargets.DeleteMultiple] = "Delete",
        };
        foreach (var (message, single) in singles)
        {
            object?[] items = message == BulkTargets.DeleteMultiple
                ? [new RecordReference { LogicalName = "account", Id = Guid.NewGuid() }, new RecordReference { LogicalName = "account", Id = Guid.NewGuid() }]
                : [new Record("account", Guid.NewGuid()), new Record("account", Guid.NewGuid())];
            var targets = BulkTargets.Read(Bulk(message, Targets(message, items)));

            targets.Message.Should().Be(message);
            targets.Count.Should().Be(2);
            targets.Names.Should().Equal("account", "account");
            targets.CollectionName.Should().Be(message == BulkTargets.DeleteMultiple ? null : "account");
            for (var i = 0; i < targets.Count; i++)
            {
                targets.Items[i].Should().BeSameAs(items[i]);
                var request = targets.Single(i);
                request.Should().BeOfType<Request>();
                request.Name.Should().Be(single);
                request.Parameters.Keys.Should().Equal("Target");
                request.Parameters["Target"].Should().BeSameAs(targets.Items[i]);
            }
        }
    }

    [Fact]
    public void DemandOneTable_text_matches()
    {
        var cases = new (object?[] Items, int Index, string Name)[]
        {
            ([new Record("account"), new Record("contact")], 1, "contact"),
            ([new Record("account"), new Record { LogicalName = null! }], 1, "(none)"),
            ([new Record("account"), null], 1, "(none)"),
        };
        foreach (var (items, index, name) in cases)
        {
            var request = Bulk(BulkTargets.UpdateMultiple, new RecordCollection { Records = [.. items.Cast<Record>()] });
            var sdk = new SdkMirror().Out(request)!;
            var model = BulkTargets.Read(request);
            model.Names[index].Should().Be(name == "(none)" ? null : name, "a null target or name is kept as null");

            var fault = Caught(() => BulkTargets.DemandOneTable(model, "account"));
            fault.Message.Should().Be(
                $"Target {index} of request 'UpdateMultiple' is a '{name}' record: every target must be a record of 'account'.");
            ShouldMatch(fault, Caught(() => BulkTargets.DemandOneTable(BulkTargets.Read(sdk), "account")));
        }

        // DeleteMultiple: a reference without a name and a null reference print "(none)" too.
        var references = new (RecordReference? Item, string Name)[]
        {
            (new RecordReference { LogicalName = null!, Id = Guid.NewGuid() }, "(none)"),
            (null, "(none)"),
        };
        foreach (var (item, name) in references)
        {
            var request = Bulk(BulkTargets.DeleteMultiple, AsReferences([new RecordReference { LogicalName = "account", Id = Guid.NewGuid() }, item]));
            var sdk = new SdkMirror().Out(request)!;
            var model = BulkTargets.Read(request);
            model.Names[1].Should().BeNull("a null reference or name is kept as null");

            var fault = Caught(() => BulkTargets.DemandOneTable(model, "account"));
            fault.Message.Should().Be(
                $"Target 1 of request 'DeleteMultiple' is a '{name}' record: every target must be a record of 'account'.");
            ShouldMatch(fault, Caught(() => BulkTargets.DemandOneTable(BulkTargets.Read(sdk), "account")));
        }
    }

    [Fact]
    public void IdOf_matches_the_sdk_IdOf()
    {
        var sdkTable = SdkMetadataBridge.ToSdk(Account);
        var id = Guid.NewGuid();
        var cases = new (object Target, Guid Expected)[]
        {
            (new Record("account", id), id),
            (new Record("account") { KeyAttributes = { ["accountid"] = id } }, id),
            (new Record("account") { KeyAttributes = { ["accountid"] = id.ToString() } }, id),
            (new Record("account") { Attributes = { ["accountid"] = id, ["name"] = "a" } }, id),
            (new Record("account") { Attributes = { ["name"] = "a" } }, Guid.Empty),
            (new RecordReference { LogicalName = "account", Id = id }, id),
            (new RecordReference { LogicalName = "account", KeyAttributes = { ["accountid"] = id } }, id),
            (new RecordReference { LogicalName = "account", KeyAttributes = { ["accountid"] = id.ToString() } }, id),
            (new Record("account") { KeyAttributes = { ["accountid"] = "not a guid" } }, Guid.Empty),
            (new RecordReference { LogicalName = "account", KeyAttributes = { ["accountid"] = "not a guid" } }, Guid.Empty),
        };
        foreach (var (target, expected) in cases)
        {
            var twin = new SdkMirror().Out(target);
            twin.Should().BeAssignableTo(target is Record ? typeof(Entity) : typeof(EntityReference));
            BulkTargets.IdOf(Account, target).Should().Be(expected);
            BulkTargets.IdOf(Account, target).Should().Be(BulkTargets.IdOf(sdkTable, twin));
        }
        BulkTargets.IdOf(Account, null).Should().Be(Guid.Empty);
    }

    [Fact]
    public void TargetIds_reads_created_ids_first()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var request = Bulk(BulkTargets.CreateMultiple, Targets(BulkTargets.CreateMultiple,
            new Record("account", first), new Record("account") { KeyAttributes = { ["accountid"] = second } }, new Record("account")));
        Guid[] created = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        BulkTargets.TargetIds(request, Account, created).Should().BeSameAs(created);
        BulkTargets.TargetIds(request, Account, null).Should().Equal(first, second, Guid.Empty);
        BulkTargets.TargetIds(Bulk(BulkTargets.CreateMultiple, null, withTargets: false), Account, null).Should().BeEmpty();
    }
}
