using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FluentAssertions;
using Xrmd.Abstractions.Sdk;

namespace Xrmd.Core.Tests.Sdk;

/// <summary>S.2a: the resolver of an SDK-free server refuses the SDK assemblies and names who asked for them.</summary>
public class SdkFreeResolverTests
{
    public SdkFreeResolverTests() => SdkFreeResolver.Reset();

    [Fact]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Refuses_an_sdk_assembly_and_names_the_requester()
    {
        var assembly = SdkFreeResolver.OnResolving(AssemblyLoadContext.Default, new AssemblyName("Microsoft.Xrm.Sdk, Version=9.0.0.0"));

        assembly.Should().BeNull();
        SdkFreeResolver.Refusals.Should().Equal(
            new SdkFreeRefusal("Microsoft.Xrm.Sdk", "Xrmd.Core.Tests.Sdk.SdkFreeResolverTests.Refuses_an_sdk_assembly_and_names_the_requester"));
    }

    [Fact]
    public void Ignores_other_assemblies()
    {
        SdkFreeResolver.OnResolving(AssemblyLoadContext.Default, new AssemblyName("Newtonsoft.Json")).Should().BeNull();

        SdkFreeResolver.Refusals.Should().BeEmpty();
    }

    [Fact]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Records_a_repeated_pair_once()
    {
        for (var i = 0; i < 3; i++)
            SdkFreeResolver.OnResolving(AssemblyLoadContext.Default, new AssemblyName("Microsoft.Xrm.Sdk.Workflow"));

        SdkFreeResolver.Refusals.Should().Equal(
            new SdkFreeRefusal("Microsoft.Xrm.Sdk.Workflow", "Xrmd.Core.Tests.Sdk.SdkFreeResolverTests.Records_a_repeated_pair_once"));
    }

    [Fact]
    public void Says_unknown_when_no_requester_is_found()
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            SdkFreeResolver.Refuse("Microsoft.Xrm.Sdk", null);
        }
        finally
        {
            Console.SetError(original);
        }

        captured.ToString().Trim().Should().Be("sdk-free: refused Microsoft.Xrm.Sdk requested by (unknown)");
        SdkFreeResolver.Refusals.Should().Equal(new SdkFreeRefusal("Microsoft.Xrm.Sdk", null));
    }

    [Theory]
    [InlineData("Microsoft.Xrm.Sdk")]
    [InlineData("Microsoft.Crm.Sdk.Proxy")]
    [InlineData("Microsoft.PowerPlatform.Dataverse.Client")]
    [InlineData("Microsoft.Xrm.Sdk.Workflow")]
    public void Names_the_refused_assembly_of_a_load_failure(string name)
    {
        SdkFreeResolver.RefusedAssembly(new InvalidOperationException("wrap",
            new FileNotFoundException("x", name + ", Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"))).Should().Be(name);
        SdkFreeResolver.RefusedAssembly(new FileLoadException("x", name)).Should().Be(name);
        SdkFreeResolver.RefusedAssembly(new TypeInitializationException("T",
            new TypeLoadException($"Could not load type 'Some.Type' from assembly '{name}, Version=9.0.0.0'."))).Should().Be(name);
    }

    [Fact]
    public void Other_failures_name_no_assembly()
    {
        SdkFreeResolver.RefusedAssembly(new FileNotFoundException("x", "Newtonsoft.Json, Version=13.0.0.0")).Should().BeNull();
        SdkFreeResolver.RefusedAssembly(new TypeLoadException("Could not load type 'A' from assembly 'Microsoft.Xrm.Sdk.Extra'.")).Should().BeNull();
        SdkFreeResolver.RefusedAssembly(new InvalidOperationException("boom")).Should().BeNull();
    }

    [Fact]
    public void Not_yet_message_names_the_requester_and_the_docs()
    {
        SdkFreeResolver.NotYetMessage("Xrmd.Host.XrmdHost.AddXrmd").Should().Be(
            "This build cannot run as an SDK-free server yet: Xrmd.Host.XrmdHost.AddXrmd still needs the Microsoft SDK " +
            "(SDK-free servers are experimental, see https://xrmverse.com/docs/microsoft-sdk#sdk-free-servers).");
    }
}
