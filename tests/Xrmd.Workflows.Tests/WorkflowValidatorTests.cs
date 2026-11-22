using FluentAssertions;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Tests;

public class WorkflowValidatorTests
{
    private static WorkflowDefinition Fixture(string name, int category = 0) =>
        XamlWorkflowParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", name + ".xaml")), category);

    private static WorkflowRow Realtime(string entity = "account") => new()
    {
        Mode = 1,
        Category = 0,
        PrimaryEntity = entity,
        TriggerOnCreate = true,
        CreateStage = 40,
    };

    [Fact]
    public void Realtime_create_update_fixture_is_valid()
    {
        WorkflowValidator.Validate(Fixture("realtime-create-update"), Realtime()).Should().BeEmpty();
    }

    [Fact]
    public void Wait_in_realtime_is_invalid()
    {
        var errors = WorkflowValidator.Validate(Fixture("background-wait"), Realtime());

        errors.Should().HaveCount(2);
        errors[0].Should().Contain("wait").And.Contain("WaitStep9: wait for approval");
        errors[1].Should().Contain("WaitStep14");
        WorkflowValidator.Validate(Fixture("background-wait"), Realtime() with { Mode = 0 }).Should().BeEmpty();
    }

    [Fact]
    public void Update_trigger_without_columns_is_invalid()
    {
        var row = Realtime() with { TriggerOnCreate = false, TriggerOnUpdateAttributes = " , ", UpdateStage = 40 };

        WorkflowValidator.Validate(Fixture("realtime-create-update"), row)
            .Should().ContainSingle().Which.Should().Contain("update trigger");
        WorkflowValidator.Validate(Fixture("realtime-create-update"), row with { TriggerOnUpdateAttributes = "name,creditlimit" })
            .Should().BeEmpty();
    }

    [Fact]
    public void Delete_stage_40_is_invalid()
    {
        var row = Realtime() with { TriggerOnCreate = false, TriggerOnDelete = true, DeleteStage = 40 };

        WorkflowValidator.Validate(Fixture("realtime-create-update"), row)
            .Should().ContainSingle().Which.Should().Contain("delete").And.Contain("40");
        WorkflowValidator.Validate(Fixture("realtime-create-update"), row with { DeleteStage = 20 }).Should().BeEmpty();
    }

    [Fact]
    public void Realtime_stage_outside_20_and_40_is_invalid()
    {
        WorkflowValidator.Validate(Fixture("realtime-create-update"), Realtime() with { CreateStage = 10 })
            .Should().ContainSingle().Which.Should().Contain("10");
    }

    [Fact]
    public void Background_with_stage_set_is_valid()
    {
        var row = Realtime() with { Mode = 0, CreateStage = 20, TriggerOnDelete = true, DeleteStage = 40 };

        WorkflowValidator.Validate(Fixture("background-wait"), row).Should().BeEmpty();
    }

    [Fact]
    public void Action_without_message_name_is_invalid()
    {
        var row = new WorkflowRow { Category = 3, Mode = 1, PrimaryEntity = "none", UniqueName = null };

        WorkflowValidator.Validate(Fixture("action-echo", 3), row)
            .Should().ContainSingle().Which.Should().Contain("unique name");
        WorkflowValidator.Validate(Fixture("action-echo", 3), row with { UniqueName = "xrmv_Echo" }).Should().BeEmpty();
    }

    [Fact]
    public void Wait_in_action_or_business_rule_is_invalid()
    {
        var wait = Fixture("background-wait");

        WorkflowValidator.Validate(wait, new WorkflowRow { Category = 3, Mode = 0, PrimaryEntity = "none", UniqueName = "xrmv_Wait" })
            .Should().HaveCount(2).And.OnlyContain(e => e.Contains("an action"));
        WorkflowValidator.Validate(wait, new WorkflowRow { Category = 2, Mode = 1, PrimaryEntity = "account" })
            .Should().HaveCount(2).And.OnlyContain(e => e.Contains("a business rule"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public void Categories_that_do_not_run_are_invalid(int category)
    {
        WorkflowValidator.Validate(Fixture("realtime-create-update", category), Realtime() with { Category = category })
            .Should().ContainSingle().Which.Should().Contain($"category {category}");
    }

    [Fact]
    public void Business_rule_needs_a_primary_entity()
    {
        WorkflowValidator.Validate(Fixture("business-rule-entity", 2), new WorkflowRow { Category = 2, Mode = 1, PrimaryEntity = null })
            .Should().ContainSingle().Which.Should().Contain("primary entity");
    }

    [Fact]
    public void Unsupported_steps_alone_do_not_make_a_definition_invalid()
    {
        // W4: unsupported steps are recorded at activation and fail at run time; they are not validation errors.
        var d = Fixture("unsupported-expression");

        d.Unsupported.Should().NotBeEmpty();
        WorkflowValidator.Validate(d, Realtime() with { Mode = 0 }).Should().BeEmpty();
    }
}
