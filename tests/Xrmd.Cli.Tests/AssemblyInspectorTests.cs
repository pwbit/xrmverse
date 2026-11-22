using FluentAssertions;
using Xrmd.Cli.Plugins;

namespace Xrmd.Cli.Tests;

public sealed class AssemblyInspectorTests
{
    private static string TestPluginsPath => Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll");

    [Fact]
    public void Inspect_finds_IPlugin_types_without_loading()
    {
        var before = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "Xrmd.TestPlugins");

        var info = AssemblyInspector.Inspect(TestPluginsPath);

        info.Name.Should().Be("Xrmd.TestPlugins");
        info.Version.Should().Be(new Version(1, 0, 0, 0));
        info.Culture.Should().Be("neutral");
        info.PublicKeyToken.Should().MatchRegex("^[0-9a-f]{16}$");
        info.PluginTypes.Should().Contain(["Xrmd.TestPlugins.SetAttributePlugin", "Xrmd.TestPlugins.ThrowPlugin"]);
        info.PluginTypes.Should().NotContain("Xrmd.TestPlugins.NotAPlugin");
        AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "Xrmd.TestPlugins").Should().Be(before, "the inspector must not load into the default context");
    }

    [Fact]
    public void Inspect_of_a_file_that_is_not_an_assembly_is_a_usage_error()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(path, "not an assembly");
        try
        {
            var act = () => AssemblyInspector.Inspect(path);
            act.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Inspect_finds_a_plugin_derived_from_an_abstract_IPlugin_base_and_skips_the_abstract_base(bool withSdk)
    {
        var info = AssemblyInspector.Inspect(TestPluginsPath, withSdk);

        info.PluginTypes.Should().Contain("Xrmd.TestPlugins.DerivedPlugin");
        info.PluginTypes.Should().Contain("Xrmd.TestPlugins.SetAttributePlugin");
        info.PluginTypes.Should().NotContain("Xrmd.TestPlugins.PluginBase");
        info.PluginTypes.Should().NotContain("Xrmd.TestPlugins.NotAPlugin");
    }

    [Fact]
    public void InspectByMetadata_matches_Inspect()
    {
        var expected = AssemblyInspector.Inspect(TestPluginsPath);

        var info = AssemblyInspector.InspectByMetadata(TestPluginsPath);

        info.Should().BeEquivalentTo(expected);
    }
}
