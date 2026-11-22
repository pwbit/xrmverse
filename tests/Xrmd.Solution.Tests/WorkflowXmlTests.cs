using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Plan 2.14, task 3 (rulings W2 and W22): the <c>Workflows</c> of <c>customizations.xml</c>. A process of category 0, 2 or 3 is read
/// into a <see cref="PackageWorkflow"/> with its XAML file; any other category keeps today's kept-XML path.
/// </summary>
public sealed class WorkflowXmlTests
{
    private static readonly TestProcess Realtime = new("Create follow-up", "realtime-create-update", Mode: 1, CreateStage: 40,
        TriggerOnUpdate: "name,telephone1");

    private static readonly TestProcess Rule = new("Account rule", "business-rule-entity", Category: 2, TriggerOnCreate: false,
        Triggers: [new("change", "account", 2, "name"), new("load", "account", 1)]);

    private static readonly TestProcess Action = new("Echo action", "action-echo", Category: 3, PrimaryEntity: "none", StateCode: 0,
        TriggerOnCreate: false, UniqueName: "xw_Echo");

    private static SolutionPackage Read(params TestProcess[] processes) => Read(_ => { }, processes);

    private static SolutionPackage Read(Action<Dictionary<string, byte[]>> edit, params TestProcess[] processes)
    {
        var (solution, customizations, files) = WorkflowSolutionBuilder.Parts("xw_flows", false, processes);
        edit(files);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    [Fact]
    public void Reads_category_0_2_3_with_xaml_file()
    {
        var package = Read(Realtime, Rule, Action);

        package.Workflows.Should().HaveCount(3);
        package.Unsupported.Should().BeEmpty("every process is of a category xrmverse runs");

        var realtime = package.Workflows.Single(w => w.Id == Realtime.Id);
        (realtime.Name, realtime.Category, realtime.Mode, realtime.Scope, realtime.PrimaryEntity, realtime.StateCode)
            .Should().Be(("Create follow-up", 0, 1, 4, "account", 1));
        realtime.XamlPath.Should().Be("/" + Realtime.FilePath);
        realtime.Xaml.Should().Be(WorkflowSolutionBuilder.Xaml("realtime-create-update"));
        realtime.Fields["CreateStage"].Should().Be("40");
        realtime.Fields["TriggerOnCreate"].Should().Be("1");
        realtime.Fields["TriggerOnUpdateAttributeList"].Should().Be("name,telephone1");
        realtime.Fields["AsyncAutodelete"].Should().Be("0");
        realtime.Fields.Should().NotContainKeys("XamlFileName", "Category", "Mode", "Scope", "PrimaryEntity", "StateCode", "LocalizedNames");
        realtime.ProcessTriggers.Should().BeEmpty();
        realtime.Source.Name.LocalName.Should().Be("Workflow");

        var rule = package.Workflows.Single(w => w.Id == Rule.Id);
        rule.Category.Should().Be(2);
        rule.ProcessTriggers.Should().HaveCount(2);
        rule.ProcessTriggers[0].Element("event")!.Value.Should().Be("change");
        rule.ProcessTriggers[0].Element("controlname")!.Value.Should().Be("name");

        var action = package.Workflows.Single(w => w.Id == Action.Id);
        (action.Category, action.PrimaryEntity, action.StateCode, action.Fields["UniqueName"]).Should().Be((3, "none", 0, "xw_Echo"));

        // The XAML files are read, not kept as unread entries; each process's source carries its file (a layer payload, ruling M5).
        package.KeptFiles.Should().NotContainKeys(Realtime.FilePath, Rule.FilePath, Action.FilePath);
        var source = package.Sources![(ComponentType.Workflow, Realtime.Id.ToString("D"))];
        source.Files.Should().ContainKey(Realtime.FilePath);
        ComponentType.TableOf(ComponentType.Workflow).Should().Be("workflow");
    }

    [OpenProject365Fact]
    public void Keeps_category_5_as_unsupported_xml()
    {
        // A built modern flow next to a classic one: only the classic one is read.
        var flow = new TestProcess("Modern flow", null, Category: 5, PrimaryEntity: "none", TriggerOnCreate: false);
        var package = Read(Realtime, flow);
        package.Workflows.Should().ContainSingle().Which.Id.Should().Be(Realtime.Id);
        var kept = package.Unsupported.Should().ContainSingle().Subject;
        kept.Name.LocalName.Should().Be("Workflows");
        kept.Elements("Workflow").Should().ContainSingle().Which.Attribute("Name")!.Value.Should().Be("Modern flow");
        package.KeptFiles.Should().ContainKey(flow.FilePath);

        // OpenProject365's nine modern flows: the Workflows node is kept as it is today, their JSON files are kept entries.
        var (solution, customizations, files) = SolutionZip.Read(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip")));
        var real = CustomizationsReader.Read(solution, customizations, files);
        real.Workflows.Should().BeEmpty();
        var workflows = real.Unsupported.Single(u => u.Name.LocalName == "Workflows");
        XNode.DeepEquals(workflows, customizations.Root!.Element("Workflows")).Should().BeTrue("the node is kept unchanged");
        workflows.Elements("Workflow").Should().HaveCount(9).And.OnlyContain(w => w.Element("Category")!.Value == "5");
        real.KeptFiles!.Keys.Count(k => k.StartsWith("Workflows/", StringComparison.Ordinal)).Should().Be(9);
    }

    [Fact]
    public void Writes_same_elements_and_xaml_bytes()
    {
        var (_, customizations, files) = WorkflowSolutionBuilder.Parts("xw_flows", false, Realtime, Rule, Action);
        var package = Read(Realtime, Rule, Action);

        var (_, written) = CustomizationsWriter.Write(package);
        var writtenFiles = CustomizationsWriter.Files(package);

        var original = customizations.Root!.Element("Workflows")!.Elements("Workflow").ToDictionary(w => (string)w.Attribute("WorkflowId")!);
        var output = written.Root!.Element("Workflows")!.Elements("Workflow").ToList();
        output.Should().HaveCount(3);
        foreach (var element in output)
        {
            var source = original[(string)element.Attribute("WorkflowId")!];
            element.Attributes().Select(a => (a.Name.LocalName, a.Value)).Should().Equal(source.Attributes().Select(a => (a.Name.LocalName, a.Value)));
            element.Elements().Select(e => e.Name.LocalName).Should().Equal(source.Elements().Select(e => e.Name.LocalName));
            XNode.DeepEquals(element, source).Should().BeTrue("process {0} is written as it was read", source.Attribute("Name")!.Value);
        }
        foreach (var process in new[] { Realtime, Rule, Action })
            writtenFiles.Should().ContainKey(process.FilePath).WhoseValue.Should().Equal(files[process.FilePath], "the XAML of {0} is written byte for byte", process.Name);

        // Read again: the same processes.
        var again = CustomizationsReader.Read(CustomizationsWriter.Write(package).Solution, written, writtenFiles);
        again.Workflows.Select(w => (w.Id, w.Xaml, w.Fields.Count, w.ProcessTriggers.Count))
            .Should().BeEquivalentTo(package.Workflows.Select(w => (w.Id, w.Xaml, w.Fields.Count, w.ProcessTriggers.Count)));
    }

    [Fact]
    public void Writes_PrimaryEntity_as_the_file_has_it()
    {
        // The import stores the logical name in lower case; the export keeps the file's spelling when only the case differs.
        var package = Read(Realtime with { PrimaryEntity = "Account" });
        var lowered = package with { Workflows = [package.Workflows.Single() with { PrimaryEntity = "account" }] };

        var (_, written) = CustomizationsWriter.Write(lowered);

        written.Root!.Element("Workflows")!.Element("Workflow")!.Element("PrimaryEntity")!.Value.Should().Be("Account");
    }

    [Fact]
    public void Missing_xaml_file_is_invalid_package()
    {
        var act = () => Read(files => files.Remove(Rule.FilePath), Realtime, Rule);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(Rule.FilePath).And.Contain("Account rule");
    }

    private static byte[] XrmdWfTest() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip"));

    /// <summary>An element without the whitespace between elements, to compare element for element.</summary>
    private static XElement Normalized(XElement element) => XElement.Parse(element.ToString(SaveOptions.DisableFormatting), LoadOptions.None);

    [Fact]
    public void Reads_the_five_processes_of_the_real_export()
    {
        var (solution, customizations, files) = SolutionZip.Read(XrmdWfTest());

        var package = CustomizationsReader.Read(solution, customizations, files);

        package.Unsupported.Should().NotContain(u => u.Name.LocalName == "Workflows");
        package.Workflows.Select(w => (w.Name, w.Category, w.Mode, w.StateCode, w.PrimaryEntity)).Should().BeEquivalentTo(new[]
        {
            ("Child", 0, 1, 0, (string?)"xrmv_order"), ("OnCreate", 0, 1, 0, "xrmv_order"), ("OnUpdate", 0, 0, 0, "xrmv_order"),
            ("New business rule", 2, 1, 0, "xrmv_order"), ("Recalculate", 3, 0, 0, "xrmv_order"),
        });
        var rule = package.Workflows.Single(w => w.Category == 2);
        rule.ProcessTriggers.Should().BeEmpty("the rule has no ProcessTriggers element: its scope is ProcessTriggerScope");
        rule.Fields["ProcessTriggerScope"].Should().Be("2");
        var action = package.Workflows.Single(w => w.Category == 3);
        (action.Fields["UniqueName"], action.Fields["SdkMessageId"], action.Fields["FormId"])
            .Should().Be(("Recalculate", "{9ef696a1-e6de-f111-84e8-002248872b66}", "{00000000-0000-0000-0000-000000000000}"));
        package.Workflows.Single(w => w.Name == "OnUpdate").Fields["TriggerOnUpdateAttributeList"].Should().Be("xrmv_status");
        foreach (var workflow in package.Workflows)
            System.Text.Encoding.UTF8.GetBytes(workflow.Xaml).Should().Equal(files[workflow.XamlPath.TrimStart('/')]);
    }

    [Fact]
    public void Writes_the_real_export_processes_element_for_element_and_their_xaml_byte_for_byte()
    {
        var (solution, customizations, files) = SolutionZip.Read(XrmdWfTest());
        var package = CustomizationsReader.Read(solution, customizations, files);

        var (_, written) = CustomizationsWriter.Write(package);
        var writtenFiles = CustomizationsWriter.Files(package);

        var original = customizations.Root!.Element("Workflows")!.Elements("Workflow").ToList();
        var output = written.Root!.Element("Workflows")!.Elements("Workflow").ToDictionary(w => (string)w.Attribute("WorkflowId")!);
        output.Should().HaveCount(5);
        foreach (var source in original)
        {
            var element = output[(string)source.Attribute("WorkflowId")!];
            element.Elements().Select(e => e.Name.LocalName).Should().Equal(source.Elements().Select(e => e.Name.LocalName));
            XNode.DeepEquals(Normalized(element), Normalized(source)).Should().BeTrue("process {0} is written as it was read", (string)source.Attribute("Name")!);
            var path = source.Element("XamlFileName")!.Value.TrimStart('/');
            writtenFiles.Should().ContainKey(path).WhoseValue.Should().Equal(files[path], "the XAML of {0} is written byte for byte", path);
        }
    }

    [Fact]
    public void Process_trigger_scope_that_is_not_a_number_is_invalid_package()
    {
        var (solution, customizations, files) = SolutionZip.Read(XrmdWfTest());
        customizations.Root!.Element("Workflows")!.Elements("Workflow").Single(w => w.Element("Category")!.Value == "2")
            .Element("ProcessTriggerScope")!.Value = "Entity";

        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("ProcessTriggerScope");
    }
}
