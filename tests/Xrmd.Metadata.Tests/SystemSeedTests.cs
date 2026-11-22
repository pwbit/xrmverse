using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 2.15, task 1: the embedded seed definitions of the duplicate detection tables, the organization settings and the
/// schema constants (no database). Plan 2.14, task 2: the process tables.
/// </summary>
public class SystemSeedTests
{
    private static EntityMetadata Seed(string logicalName)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream($"Xrmd.Metadata.Seed.{logicalName}.json")
                           ?? throw new InvalidOperationException($"No seed resource for {logicalName}.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    private static AttributeMetadata Column(EntityMetadata e, string name) => e.Attributes.Single(a => a.LogicalName == name);

    [Fact]
    public void Duplicate_tables_are_seeded_with_their_type_codes()
    {
        (Seed("duplicaterule").ObjectTypeCode, Seed("duplicaterecord").ObjectTypeCode, Seed("duplicaterulecondition").ObjectTypeCode)
            .Should().Be((DuplicateDetectionSchema.RuleObjectTypeCode, DuplicateDetectionSchema.RecordObjectTypeCode, DuplicateDetectionSchema.ConditionObjectTypeCode));
        (DuplicateDetectionSchema.RuleObjectTypeCode, DuplicateDetectionSchema.RecordObjectTypeCode, DuplicateDetectionSchema.ConditionObjectTypeCode)
            .Should().Be((4414, 4415, 4416));
        Seed("duplicaterule").OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        Seed("duplicaterulecondition").OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
        Seed("duplicaterecord").OwnershipType.Should().Be(OwnershipTypes.UserOwned);

        var rule = Seed("duplicaterule");
        foreach (var column in DuplicateDetectionSchema.ServerFilledRuleColumns)
            (Column(rule, column).IsValidForCreate, Column(rule, column).IsValidForUpdate).Should().Be((false, false), column);
        ((StringAttributeMetadata)Column(rule, "name")).MaxLength.Should().Be(160);
        ((StringAttributeMetadata)Column(rule, "uniquename")).MaxLength.Should().Be(100);
        ((MemoAttributeMetadata)Column(rule, "description")).MaxLength.Should().Be(2000);
        ((IntegerAttributeMetadata)Column(Seed("duplicaterulecondition"), "operatorparam")).MinValue.Should().Be(1);
    }

    [Fact]
    public void Duplicate_record_key_is_duplicateid()
    {
        SystemAttributes.PrimaryIdName("duplicaterecord").Should().Be("duplicateid");
        SystemAttributes.IsReservedName("duplicaterecord", "duplicaterecordid").Should().BeFalse("it is a lookup column");
        SystemAttributes.PrimaryIdName("duplicaterule").Should().Be("duplicateruleid");
        var lookup = Column(Seed("duplicaterecord"), "baserecordid");
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.EntityLogicalName))!.GetSetMethod(nonPublic: true)!.Invoke(lookup, ["duplicaterecord"]);
        DuplicateDetectionSchema.IsRecordLookup(lookup).Should().BeTrue();
        TypeMapping.ToColumns(lookup).Select(c => c.Name).Should().Equal("baserecordid", "baserecordidtype");
    }

    [Fact]
    public void Organization_and_job_seed_carry_the_duplicate_detection_values()
    {
        var organization = Seed("organization");
        foreach (var column in DuplicateDetectionSchema.OrganizationColumns)
        {
            var flag = Column(organization, column).Should().BeOfType<BooleanAttributeMetadata>().Subject;
            flag.DefaultValue.Should().BeFalse(column);
            flag.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired, column);
        }
        ((PicklistAttributeMetadata)Column(Seed("asyncoperation"), "operationtype")).OptionSet.Options.Select(o => o.Value!.Value)
            .Should().Contain([7, 8]);
    }

    [Fact]
    public void Schema_constants_are_the_plan_values()
    {
        (DuplicateDetectionSchema.MaxPublishedPerBaseTable, DuplicateDetectionSchema.MaxBulkRecords, DuplicateDetectionSchema.MaxPageSize)
            .Should().Be((5, 5000, 5000));
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        DuplicateDetectionSchema.MatchCodeTableName(id).Should().Be("matchcode_0f8fad5bd9cb469fa16570867728950e");
        ((int)DuplicateDetectionSchema.DuplicateOperator.PickListValue).Should().Be(6);
        (DuplicateDetectionSchema.RuleStatus.Unpublished, DuplicateDetectionSchema.RuleStatus.Publishing, DuplicateDetectionSchema.RuleStatus.Published)
            .Should().Be((0, 1, 2));
        DuplicateDetectionSchema.CanBeEnabled(new EntityMetadata { LogicalName = "new_thing" }).Should().BeTrue();
        DuplicateDetectionSchema.CanBeEnabled(Seed("account")).Should().BeTrue();
        DuplicateDetectionSchema.CanBeEnabled(Seed("contact")).Should().BeTrue();
        DuplicateDetectionSchema.CanBeEnabled(Seed("systemuser")).Should().BeFalse();
        DuplicateDetectionSchema.CanBeEnabled(Seed("duplicaterule")).Should().BeFalse();
    }

    [Fact]
    public void Process_tables_are_seeded_with_the_plan_values()
    {
        var workflow = Seed(WorkflowSchema.Table);
        var trigger = Seed(WorkflowSchema.TriggerTable);
        (workflow.ObjectTypeCode, workflow.EntitySetName, workflow.OwnershipType, workflow.PrimaryNameAttribute)
            .Should().Be((WorkflowSchema.ObjectTypeCode, "workflows", OwnershipTypes.UserOwned, "name"));
        (trigger.ObjectTypeCode, trigger.EntitySetName, trigger.OwnershipType).Should().Be((WorkflowSchema.TriggerObjectTypeCode, "processtriggers", OwnershipTypes.OrganizationOwned));
        (WorkflowSchema.ObjectTypeCode, WorkflowSchema.TriggerObjectTypeCode).Should().Be((4703, 4712));
        ((StringAttributeMetadata)Column(workflow, "name")).MaxLength.Should().Be(100);
        ((StringAttributeMetadata)Column(workflow, "uniquename")).MaxLength.Should().Be(256);
        ((MemoAttributeMetadata)Column(workflow, "xaml")).MaxLength.Should().Be(1073741823);
        Column(workflow, WorkflowSchema.UnsupportedStepsColumn).Should().BeOfType<MemoAttributeMetadata>();
        foreach (var flag in new[] { "ondemand", "subprocess", "triggeroncreate", "triggerondelete", "asyncautodelete", "syncworkflowlogonfailure", "istransacted" })
            Column(workflow, flag).Should().BeOfType<BooleanAttributeMetadata>(flag);
        foreach (var lookup in new[] { "parentworkflowid", "activeworkflowid" })
            ((LookupAttributeMetadata)Column(workflow, lookup)).Targets.Should().Equal("workflow");
        ((LookupAttributeMetadata)Column(workflow, "sdkmessageid")).Targets.Should().Equal("sdkmessage");
        ((LookupAttributeMetadata)Column(trigger, "processid")).Targets.Should().Equal("workflow");
        ((LookupAttributeMetadata)Column(trigger, "formid")).Targets.Should().Equal("systemform");

        int[] Options(EntityMetadata e, string column) => ((EnumAttributeMetadata)Column(e, column)).OptionSet.Options.Select(o => o.Value!.Value).ToArray();
        Options(workflow, "category").Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
        Options(workflow, "type").Should().Equal((int)WorkflowSchema.Type.Definition, (int)WorkflowSchema.Type.Activation, 3);
        Options(workflow, "mode").Should().Equal((int)WorkflowSchema.Mode.Background, (int)WorkflowSchema.Mode.Realtime);
        Options(workflow, "runas").Should().Equal((int)WorkflowSchema.RunAs.Owner, (int)WorkflowSchema.RunAs.CallingUser);
        Options(workflow, "createstage").Should().Equal((int)WorkflowSchema.Stage.Pre, (int)WorkflowSchema.Stage.Post);
        Options(workflow, "statuscode").Should().Equal(WorkflowSchema.DraftStatus, WorkflowSchema.ActivatedStatus);
        Options(trigger, "scope").Should().Equal(1, WorkflowSchema.EntityTriggerScope);
        // Plan 2.14, Task 3b: the rule's own scope (<ProcessTriggerScope> of the parity export; Learn has 1 Form and 2 Entity).
        Options(workflow, WorkflowSchema.ProcessTriggerScope).Should().Equal(1, WorkflowSchema.EntityTriggerScope, 3);
        ((int)WorkflowSchema.Category.Workflow, (int)WorkflowSchema.Category.BusinessRule, (int)WorkflowSchema.Category.Action).Should().Be((0, 2, 3));

        var job = Seed("asyncoperation");
        Options(job, "operationtype").Should().Contain(WorkflowSchema.WorkflowOperationType);
        ((LookupAttributeMetadata)Column(job, WorkflowSchema.WorkflowActivationId)).Targets.Should().Equal("workflow");
        job.ManyToOneRelationships.Should().ContainSingle(r => r.SchemaName == WorkflowSchema.AsyncOperationsRelationship)
            .Which.CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
        workflow.ManyToOneRelationships.Single(r => r.SchemaName == WorkflowSchema.ParentRelationship).CascadeConfiguration.Delete
            .Should().Be(CascadeType.Cascade);
        trigger.ManyToOneRelationships.Single(r => r.SchemaName == WorkflowSchema.TriggersRelationship).CascadeConfiguration.Delete
            .Should().Be(CascadeType.Cascade);
    }

    /// <summary>#122: the Dataverse column OpenProject365's TimeEntryValidate plug-in reads.</summary>
    [Fact]
    public void Systemuser_has_applicationid()
    {
        var column = Column(Seed("systemuser"), "applicationid");
        column.Should().BeOfType<UniqueIdentifierAttributeMetadata>();
        column.SchemaName.Should().Be("ApplicationId");
        column.IsValidForCreate.Should().NotBe(false);
        column.IsValidForUpdate.Should().NotBe(false);
        column.IsValidForRead.Should().NotBe(false);
    }
}
