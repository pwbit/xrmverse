using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Schema.Tests;

public class SchemaDiffTests
{
    private static SchemaDocument Sample() => SchemaYaml.Read(SchemaYamlTests.Sample);

    private static SchemaPlan Diff(SchemaDocument document, InMemoryOrg org) => SchemaDiff.Compute(document, org.Entities, org.OptionSets);

    /// <summary>An org where <paramref name="document"/> was applied.</summary>
    private static InMemoryOrg Applied(SchemaDocument document)
    {
        var org = new InMemoryOrg();
        var plan = Diff(document, org);
        plan.Rejections.Should().BeEmpty();
        org.Apply(plan.Changes);
        return org;
    }

    private static string Describe(OrganizationRequest r) => r switch
    {
        CreateOptionSetRequest x => $"CreateOptionSet {x.OptionSet.Name}",
        CreateEntityRequest x => $"CreateEntity {x.Entity.LogicalName} ({x.PrimaryAttribute.LogicalName})",
        CreateAttributeRequest x => $"CreateAttribute {x.EntityName}.{x.Attribute.LogicalName}",
        CreateOneToManyRequest x => $"CreateOneToMany {x.OneToManyRelationship.ReferencingEntity}.{x.Lookup.LogicalName} -> {x.OneToManyRelationship.ReferencedEntity}",
        CreateManyToManyRequest x => $"CreateManyToMany {x.ManyToManyRelationship.SchemaName}",
        _ => r.RequestName,
    };

    [Fact]
    public void Diff_of_empty_org_creates_in_dependency_order()
    {
        var plan = Diff(Sample(), new InMemoryOrg());

        plan.Rejections.Should().BeEmpty();
        plan.Extras.Should().BeEmpty();
        plan.Changes.Select(Describe).Should().Equal(
            "CreateOptionSet xv_priority",
            "CreateEntity xv_project (xv_name)",
            "CreateEntity xv_tag (xv_name)",
            "CreateAttribute xv_project.xv_budget",
            "CreateAttribute xv_project.xv_priority",
            "CreateAttribute xv_tag.xv_code",
            "CreateAttribute xv_tag.xv_notes",
            "CreateAttribute xv_tag.xv_rank",
            "CreateAttribute xv_tag.xv_weight",
            "CreateAttribute xv_tag.xv_active",
            "CreateAttribute xv_tag.xv_since",
            "CreateAttribute xv_tag.xv_color",
            "CreateAttribute xv_tag.xv_sizes",
            "CreateOneToMany xv_project.xv_parent -> xv_project",
            "CreateManyToMany xv_project_xv_tag");
        plan.Changes.Should().AllSatisfy(r => r.Parameters["SolutionUniqueName"].Should().Be("XrmverseFixture"));

        var project = (CreateEntityRequest)plan.Changes[1];
        (project.Entity.SchemaName, project.Entity.OwnershipType, SchemaColumnTypes.Text(project.Entity.DisplayCollectionName))
            .Should().Be(("xv_project", OwnershipTypes.UserOwned, "Projects"));
        project.Entity.DisplayName.LocalizedLabels.Should().ContainSingle().Which.LanguageCode.Should().Be(1033);
        ((StringAttributeMetadata)project.PrimaryAttribute).MaxLength.Should().Be(200);
        ((CreateEntityRequest)plan.Changes[2]).Entity.SchemaName.Should().Be("xv_Tag");
        ((CreateEntityRequest)plan.Changes[2]).Entity.OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
        var priority = (PicklistAttributeMetadata)((CreateAttributeRequest)plan.Changes[4]).Attribute;
        (priority.OptionSet.IsGlobal, priority.OptionSet.Name).Should().Be((true, "xv_priority"));
        var oneToMany = (CreateOneToManyRequest)plan.Changes[13];
        oneToMany.OneToManyRelationship.SchemaName.Should().Be("xv_project_xv_parent_xv_project");
        oneToMany.OneToManyRelationship.ReferencedAttribute.Should().Be("xv_projectid");
        var manyToMany = (CreateManyToManyRequest)plan.Changes[14];
        (manyToMany.ManyToManyRelationship.Entity1LogicalName, manyToMany.ManyToManyRelationship.Entity2LogicalName, manyToMany.IntersectEntitySchemaName)
            .Should().Be(("xv_project", "xv_tag", "xv_project_xv_tag"));

        // Every request passes the server's rules and builds the org the document describes.
        var org = new InMemoryOrg();
        org.Apply(plan.Changes);
        org.Attribute("xv_tag", "xv_code").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(20);
    }

    [Fact]
    public void Diff_without_solution_sets_none()
    {
        var document = Sample();
        document.Solution = null;

        Diff(document, new InMemoryOrg()).Changes.Should().AllSatisfy(r => r.Parameters.Should().NotContainKey("SolutionUniqueName"));
    }

    [Fact]
    public void Diff_second_time_is_empty()
    {
        var org = Applied(Sample());

        var plan = Diff(Sample(), org);

        plan.Changes.Should().BeEmpty();
        plan.Extras.Should().BeEmpty();
        plan.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Exported_org_reads_back_as_the_document_and_diffs_empty()
    {
        var org = Applied(Sample());

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");

        exported.Tables.Select(t => t.Name).Should().Equal("xv_project", "xv_tag");
        exported.Tables[0].Columns.Select(c => c.Name).Should().Equal("xv_budget", "xv_parent", "xv_priority");
        exported.Tables[1].Columns.Single(c => c.Name == "xv_code").SchemaName.Should().Be("xv_Code");
        exported.Tables[1].PrimaryName.MaxLength.Should().Be(100);
        exported.Tables[0].ManyToMany.Should().ContainSingle().Which.With.Should().Be("xv_tag");
        exported.Tables[1].ManyToMany.Should().BeEmpty();
        var plan = Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org);
        plan.Changes.Should().BeEmpty();
        plan.Extras.Should().BeEmpty();
        plan.Rejections.Should().BeEmpty();
    }

    private static SchemaDocument ElasticDocument(string tableType) => SchemaYaml.Read($$"""
        publisher: xv
        tables:
          - name: xv_sensor
            label: Sensor
            tableType: {{tableType}}
            primaryName: { name: xv_name }
            columns:
              - { name: xv_value, type: int }
        """);

    [Fact]
    public void Elastic_table_is_created_as_elastic_and_the_export_reads_it_back()
    {
        var document = ElasticDocument("Elastic");
        var org = new InMemoryOrg();
        var plan = Diff(document, org);
        plan.Changes.OfType<CreateEntityRequest>().Single().Entity.TableType.Should().Be("Elastic");
        org.Apply(plan.Changes);

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");

        exported.Tables.Single().TableType.Should().Be("Elastic");
        var again = Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org);
        again.Changes.Should().BeEmpty();
        again.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Changing_the_table_type_of_an_existing_table_is_a_rejection_naming_the_table()
    {
        var elastic = Applied(ElasticDocument("Elastic"));
        var standard = Applied(ElasticDocument("Standard"));

        Diff(ElasticDocument("Standard"), elastic).Rejections.Should().ContainSingle().Which
            .Should().Contain("xv_sensor").And.Contain("Elastic").And.Contain("Standard");
        Diff(ElasticDocument("Elastic"), standard).Rejections.Should().ContainSingle().Which.Should().Contain("xv_sensor");
    }

    /// <summary>
    /// The currency pair a money column brings (plan 2.10, X4) is generated by the server: a diff neither creates, compares nor lists
    /// it, and an export does not describe it (nor the money's <c>_base</c>).
    /// </summary>
    [Fact]
    public void Diff_ignores_generated_currency_columns()
    {
        var org = Applied(Sample());
        org.Entity("xv_project").Attributes.Select(a => a.LogicalName).Should().Contain(["transactioncurrencyid", "exchangerate", "xv_budget_base"]);
        org.Entity("xv_tag").Attributes.Select(a => a.LogicalName).Should().NotContain("transactioncurrencyid");

        var plan = Diff(Sample(), org);
        plan.Changes.Should().BeEmpty();
        plan.Extras.Should().BeEmpty();
        plan.Rejections.Should().BeEmpty();

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");
        exported.Tables.SelectMany(t => t.Columns).Select(c => c.Name).Should().NotContain(["transactioncurrencyid", "exchangerate", "xv_budget_base"]);

        var project = org.Entity("xv_project");
        SchemaColumnTypes.IsGenerated(project, project.Attributes.Single(a => a.LogicalName == "transactioncurrencyid")).Should().BeTrue();
        SchemaColumnTypes.IsGenerated(project, project.Attributes.Single(a => a.LogicalName == "xv_budget_base")).Should().BeTrue();
        SchemaColumnTypes.IsGenerated(project, project.Attributes.Single(a => a.LogicalName == "xv_budget")).Should().BeFalse();
    }

    [Fact]
    public void Diff_lists_extras_and_never_deletes()
    {
        var org = Applied(Sample());
        var smaller = Sample();
        smaller.Choices.Clear();
        smaller.Choices.Add(new SchemaChoice { Name = "xv_priority", Label = "Priority", Options = [new() { Value = 100000000, Label = "Low" }] });
        smaller.Tables.RemoveAt(1); // xv_tag
        smaller.Tables[0].Columns.RemoveAll(c => c.Name == "xv_budget");
        smaller.Tables[0].ManyToMany.Clear();

        var plan = Diff(smaller, org);

        plan.Changes.Should().BeEmpty();
        plan.Rejections.Should().BeEmpty();
        plan.Extras.Should().BeEquivalentTo(
            "option xv_priority.100000001",
            "table xv_tag",
            "column xv_project.xv_budget",
            "relationship xv_project_xv_tag");
    }

    [Fact]
    public void Extras_ignore_other_publishers_and_system_components()
    {
        var org = Applied(Sample());
        org.Apply(new CreateAttributeRequest { EntityName = "xv_project", Attribute = new IntegerAttributeMetadata { LogicalName = "new_other", SchemaName = "new_other" } });

        Diff(Sample(), org).Extras.Should().BeEmpty();
    }

    [Fact]
    public void Type_change_is_a_rejection_naming_the_column()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[0].Columns[0] = new SchemaColumn { Name = "xv_budget", Type = "string", Label = "Budget" };

        var plan = Diff(document, org);

        plan.Rejections.Should().ContainSingle().Which.Should().Contain("xv_project.xv_budget").And.Contain("money").And.Contain("string");
        plan.Changes.Should().BeEmpty();
    }

    [Fact]
    public void Lookup_target_change_is_a_rejection_naming_the_column()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[0].Columns[2].Target = "xv_tag";

        Diff(document, org).Rejections.Should().ContainSingle().Which.Should().Contain("xv_project.xv_parent").And.Contain("xv_tag");
    }

    [Fact]
    public void A_lookup_without_targets_is_no_column_and_a_rejection_in_a_diff_not_a_crash()
    {
        var org = Applied(Sample());
        var untargeted = (LookupAttributeMetadata)org.Attribute("xv_project", "xv_parent");
        untargeted.Targets = [];
        SchemaColumnTypes.ToColumn(untargeted).Should().BeNull();

        var plan = Diff(Sample(), org);

        plan.Rejections.Should().ContainSingle().Which.Should().Contain("xv_project.xv_parent").And.Contain("cannot change"); // explicit, not a crash on Targets[0]
    }

    [Fact]
    public void Other_unapplicable_changes_are_rejections()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[1].Columns[3].Precision = 4; // xv_weight
        document.Tables[1].Ownership = SchemaOwnership.User;
        document.Tables[0].PrimaryName.Name = "xv_title";
        document.Tables[0].Columns[1].Choice = null; // xv_priority: global → own options
        document.Tables[0].Columns[1].Options = [new() { Value = 1, Label = "A" }];
        document.Tables[0].Columns.Add(new SchemaColumn { Name = "xv_kind", Type = "choice", Choice = "xv_missing" });
        document.Tables[0].Columns.Add(new SchemaColumn { Name = "xv_link", Type = "lookup", Target = "xv_nowhere" });

        var plan = Diff(document, org);

        plan.Rejections.Should().HaveCount(6);
        plan.Rejections.Should().Contain(r => r.Contains("xv_tag.xv_weight") && r.Contains("precision"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_tag") && r.Contains("ownership"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_project") && r.Contains("xv_title"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_project.xv_priority"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_project.xv_kind") && r.Contains("xv_missing"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_project.xv_link") && r.Contains("xv_nowhere"));
    }

    [Fact]
    public void MaxLength_decrease_is_a_rejection()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[1].Columns[0].MaxLength = 10;      // xv_code 20 → 10
        document.Tables[0].PrimaryName.MaxLength = 150;    // xv_name 200 → 150

        var plan = Diff(document, org);

        plan.Rejections.Should().HaveCount(2);
        plan.Rejections.Should().Contain(r => r.Contains("xv_tag.xv_code") && r.Contains("20") && r.Contains("10"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_project.xv_name") && r.Contains("150"));
    }

    [Fact]
    public void MaxLength_increase_becomes_UpdateAttribute()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[1].Columns[0].MaxLength = 40;

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.Should().ContainSingle().Which.Should().BeOfType<UpdateAttributeRequest>().Subject;
        (update.EntityName, update.Attribute.LogicalName, ((StringAttributeMetadata)update.Attribute).MaxLength).Should().Be(("xv_tag", "xv_code", 40));
        org.Apply(plan.Changes);
        Diff(document, org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void Added_option_becomes_InsertOptionValue()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Choices[0].Options.Add(new SchemaOption { Value = 100000002, Label = "Urgent" });
        document.Tables[1].Columns[6].Options!.Add(new SchemaOption { Value = 3, Label = "Blue" }); // xv_color

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        plan.Changes.Should().HaveCount(2);
        var global = plan.Changes[0].Should().BeOfType<InsertOptionValueRequest>().Subject;
        (global.OptionSetName, global.Value, SchemaColumnTypes.Text(global.Label)).Should().Be(("xv_priority", 100000002, "Urgent"));
        var local = plan.Changes[1].Should().BeOfType<InsertOptionValueRequest>().Subject;
        (local.EntityLogicalName, local.AttributeLogicalName, local.Value, SchemaColumnTypes.Text(local.Label))
            .Should().Be(("xv_tag", "xv_color", 3, "Blue"));
        local.OptionSetName.Should().BeNull();
        org.Apply(plan.Changes);
        Diff(document, org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void Label_change_becomes_UpdateAttribute()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[0].Columns[0].Label = "Total budget";
        document.Tables[0].Columns[0].Description = "In the org currency";

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.Should().ContainSingle().Which.Should().BeOfType<UpdateAttributeRequest>().Subject;
        update.EntityName.Should().Be("xv_project");
        update.Attribute.Should().BeOfType<MoneyAttributeMetadata>();
        (update.Attribute.LogicalName, SchemaColumnTypes.Text(update.Attribute.DisplayName), SchemaColumnTypes.Text(update.Attribute.Description))
            .Should().Be(("xv_budget", "Total budget", "In the org currency"));
        update.Parameters["SolutionUniqueName"].Should().Be("XrmverseFixture");
        org.Apply(plan.Changes);
        Diff(document, org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void Other_label_changes_become_updates()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[0].Label = "Programme";
        document.Tables[0].PrimaryName.Label = "Title";
        document.Choices[0].Label = "Importance";
        document.Choices[0].Options[1].Label = "Very high";
        document.Tables[1].Columns[6].Options![0].Label = "Crimson";

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        plan.Changes.Select(c => c.GetType().Name).Should().Equal(
            nameof(UpdateOptionSetRequest), nameof(UpdateEntityRequest), nameof(UpdateAttributeRequest), nameof(UpdateAttributeRequest));
        org.Apply(plan.Changes);
        Diff(document, org).Changes.Should().BeEmpty();
        SchemaColumnTypes.Text(org.Entity("xv_project").DisplayName).Should().Be("Programme");
        SchemaColumnTypes.Text(((EnumAttributeMetadata)org.Attribute("xv_project", "xv_priority")).OptionSet.Options[1].Label).Should().Be("Very high");
    }

    [Fact]
    public void New_components_in_an_existing_org_are_created()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[1].Columns.Add(new SchemaColumn { Name = "xv_owner", Type = "lookup", Target = "systemuser" });
        org.Entities.Add(new EntityMetadata { LogicalName = "systemuser", SchemaName = "SystemUser" });

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        plan.Changes.Should().ContainSingle().Which.Should().BeOfType<CreateOneToManyRequest>()
            .Which.OneToManyRelationship.ReferencedEntity.Should().Be("systemuser");
    }

    [Fact]
    public void Export_refuses_a_column_the_yaml_cannot_describe()
    {
        var org = Applied(Sample());
        org.Apply(new CreateAttributeRequest { EntityName = "xv_tag", Attribute = new DoubleAttributeMetadata { LogicalName = "xv_ratio", SchemaName = "xv_ratio" } });

        var error = Assert.Throws<NotSupportedException>(() => SchemaExporter.Export(org.Entities, org.OptionSets, "xv"));

        error.Message.Should().Contain("xv_tag.xv_ratio");
        Diff(Sample(), org).Extras.Should().Equal("column xv_tag.xv_ratio");
    }

    private const string CustomerYaml = """
        publisher: xv
        tables:
          - name: xv_deal
            label: Deal
            primaryName: { name: xv_name, label: Name }
            columns:
              - { name: xv_customerid, type: customer, label: Customer }
        """;

    [Fact]
    public void Customer_column_plans_one_message_with_both_relationships_and_exports_as_customer()
    {
        var org = new InMemoryOrg();
        var plan = Diff(SchemaYaml.Read(CustomerYaml), org);

        plan.Rejections.Should().BeEmpty();
        var request = plan.Changes.OfType<CreateCustomerRelationshipsRequest>().Should().ContainSingle().Subject;
        request.OneToManyRelationships.Select(r => (r.SchemaName, r.ReferencedEntity, r.ReferencingEntity)).Should().Equal(
            ("xv_deal_xv_customerid_account", "account", "xv_deal"), ("xv_deal_xv_customerid_contact", "contact", "xv_deal"));
        plan.Changes.OfType<CreateAttributeRequest>().Select(r => r.Attribute.LogicalName).Should().NotContain("xv_customerid");
        plan.Changes.OfType<CreateOneToManyRequest>().Should().BeEmpty();

        org.Apply(plan.Changes);
        org.Attribute("xv_deal", "xv_customerid").Should().BeOfType<LookupAttributeMetadata>().Which.AttributeType.Should().Be(AttributeTypeCode.Customer);
        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");
        var column = exported.Tables.Single().Columns.Single();
        (column.Name, column.Type, column.Target, column.Label).Should().Be(("xv_customerid", "customer", null, "Customer"));
        SchemaYaml.Write(exported).Should().Contain("{name: xv_customerid, type: customer, label: Customer}");
        var again = Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org);
        again.Changes.Should().BeEmpty();
        again.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Customer_column_with_a_target_is_a_format_error_and_a_lookup_is_not_a_customer()
    {
        var withTarget = CustomerYaml.Replace("type: customer,", "type: customer, target: account,");
        Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(withTarget)).Message.Should().Contain("no target");

        var org = Applied(SchemaYaml.Read(CustomerYaml));
        var asLookup = CustomerYaml.Replace("type: customer", "type: lookup, target: account");
        Diff(SchemaYaml.Read(asLookup), org).Rejections.Should().ContainSingle().Which.Should().Contain("xv_deal.xv_customerid").And.Contain("customer");
    }

    [Fact]
    public void Export_still_refuses_other_polymorphic_lookups()
    {
        var org = Applied(Sample());
        org.Apply(new CreateAttributeRequest
        {
            EntityName = "xv_tag", Attribute = new LookupAttributeMetadata { LogicalName = "xv_regarding", SchemaName = "xv_regarding", Targets = ["xv_tag", "xv_project"] },
        });

        Assert.Throws<NotSupportedException>(() => SchemaExporter.Export(org.Entities, org.OptionSets, "xv")).Message.Should().Contain("xv_tag.xv_regarding");
    }

    private static SchemaDocument ActivitySample() => SchemaYaml.Read(SchemaYamlTests.ActivitySample);

    [Fact]
    public void Activity_table_and_flags_are_created_and_export_back_unchanged()
    {
        var plan = Diff(ActivitySample(), new InMemoryOrg());

        plan.Rejections.Should().BeEmpty();
        var sms = (CreateEntityRequest)plan.Changes[0];
        sms.Entity.IsActivity.Should().BeTrue();
        // The primary column of a custom activity: logical name 'subject', SchemaName 'Subject'.
        (sms.PrimaryAttribute.LogicalName, sms.PrimaryAttribute.SchemaName).Should().Be(("subject", "Subject"));
        (sms.HasNotes, sms.HasActivities).Should().Be((true, false));
        var site = (CreateEntityRequest)plan.Changes[1];
        site.Entity.IsActivity.Should().NotBe(true);
        (site.HasNotes, site.HasActivities, site.Entity.HasNotes, site.Entity.HasActivities).Should().Be((true, true, true, true));
        var plain = (CreateEntityRequest)plan.Changes[2];
        (plain.HasNotes, plain.HasActivities).Should().Be((false, false));

        var org = new InMemoryOrg();
        org.Apply(plan.Changes);
        org.Entity("xv_sms").PrimaryIdAttribute.Should().Be("activityid");
        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");
        var exportedSms = exported.Tables.Single(t => t.Name == "xv_sms");
        (exportedSms.IsActivity, exportedSms.HasNotes, exportedSms.HasActivities, exportedSms.PrimaryName.Name, exportedSms.PrimaryName.SchemaName)
            .Should().Be((true, true, (bool?)null, "subject", (string?)null));
        exported.Tables.Single(t => t.Name == "xv_site").HasActivities.Should().BeTrue();
        exported.Tables.Single(t => t.Name == "xv_plain").HasNotes.Should().BeNull();
        var again = Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org);
        again.Changes.Should().BeEmpty();
        again.Rejections.Should().BeEmpty();
        Diff(ActivitySample(), org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void Diff_switches_flags_on_with_UpdateEntity()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[0].HasNotes = true;
        document.Tables[0].HasActivities = true;

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.Should().ContainSingle().Which.Should().BeOfType<UpdateEntityRequest>().Subject;
        (update.Entity.LogicalName, update.Entity.HasNotes, update.Entity.HasActivities).Should().Be(("xv_project", true, true));
        org.Apply(plan.Changes);
        Diff(document, org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void Diff_switches_isValidForQueue_on_one_way_and_exports_it()
    {
        var org = Applied(Sample());
        var document = Sample();
        document.Tables[0].IsValidForQueue = true;

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.Should().ContainSingle().Which.Should().BeOfType<UpdateEntityRequest>().Subject;
        (update.Entity.LogicalName, update.Entity.IsValidForQueue?.Value).Should().Be(("xv_project", true));
        org.Apply(plan.Changes);
        Diff(document, org).Changes.Should().BeEmpty();
        SchemaExporter.Export(org.Entities, org.OptionSets, "xv").Tables.Single(t => t.Name == "xv_project").IsValidForQueue.Should().BeTrue();

        document.Tables[0].IsValidForQueue = false;
        Diff(document, org).Rejections.Should().ContainSingle().Which.Should().Contain("isValidForQueue").And.Contain("cannot be turned off");
    }

    [Fact]
    public void Activity_without_queues_exports_false_and_applies_elsewhere_without_queues()
    {
        var source = new InMemoryOrg();
        var document = SchemaYaml.Read(SchemaYamlTests.ActivitySample);
        document.Tables[0].IsValidForQueue = false; // xv_sms, created without queues
        source.Apply(Diff(document, new InMemoryOrg()).Changes);
        source.Entity("xv_sms").IsValidForQueue!.Value.Should().BeFalse();

        var exported = SchemaExporter.Export(source.Entities, source.OptionSets, "xv");
        exported.Tables.Single(t => t.Name == "xv_sms").IsValidForQueue.Should().BeFalse("absent would mean on for an activity");
        exported.Tables.Single(t => t.Name == "xv_plain").IsValidForQueue.Should().BeNull("absent means off for an ordinary table");
        var yaml = SchemaYaml.Write(exported);
        yaml.Should().Contain("isValidForQueue: false");

        // Applied to another organization, the activity stays without queues; applied back to the source, nothing changes.
        var target = new InMemoryOrg();
        var plan = Diff(SchemaYaml.Read(yaml), target);
        plan.Rejections.Should().BeEmpty();
        ((CreateEntityRequest)plan.Changes.First(c => c is CreateEntityRequest r && r.Entity.LogicalName == "xv_sms")).Entity.IsValidForQueue!.Value
            .Should().BeFalse();
        target.Apply(plan.Changes);
        target.Entity("xv_sms").IsValidForQueue!.Value.Should().BeFalse();
        Diff(SchemaYaml.Read(yaml), source).Changes.Should().BeEmpty();
    }

    [Fact]
    public void Diff_refuses_turning_flags_off()
    {
        var org = Applied(ActivitySample());
        var document = ActivitySample();
        document.Tables[1].HasNotes = false; // xv_site
        document.Tables[1].HasActivities = false;
        document.Tables[0].HasNotes = false; // xv_sms: an activity always has notes

        var plan = Diff(document, org);

        plan.Changes.Should().BeEmpty();
        plan.Rejections.Should().HaveCount(3);
        plan.Rejections.Should().Contain(r => r.Contains("xv_site") && r.Contains("hasNotes") && r.Contains("cannot be turned off"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_site") && r.Contains("hasActivities") && r.Contains("cannot be turned off"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_sms") && r.Contains("hasNotes"));

        // Not stating a flag leaves it as it is.
        document = ActivitySample();
        document.Tables[1].HasNotes = null;
        document.Tables[1].HasActivities = null;
        Diff(document, org).Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Diff_refuses_changing_isActivity()
    {
        var org = Applied(ActivitySample());
        var document = ActivitySample();
        document.Tables[0].IsActivity = false; // xv_sms
        document.Tables[0].PrimaryName.Name = "xv_name";
        document.Tables[2].IsActivity = true; // xv_plain
        document.Tables[2].HasNotes = null;
        document.Tables[2].HasActivities = null;

        var plan = Diff(document, org);

        plan.Rejections.Should().Contain(r => r.Contains("xv_sms") && r.Contains("isActivity") && r.Contains("cannot change"));
        plan.Rejections.Should().Contain(r => r.Contains("xv_plain") && r.Contains("isActivity") && r.Contains("cannot change"));
    }
}
