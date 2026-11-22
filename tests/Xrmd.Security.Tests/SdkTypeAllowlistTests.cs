using System.Collections;
using System.Data;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.Security.Tests;

/// <summary>The types the deserialization of async payloads and SOAP requests accepts (<see cref="SdkTypeAllowlist"/>).</summary>
public sealed class SdkTypeAllowlistTests
{
    /// <summary>An early-bound entity of a plug-in or client assembly.</summary>
    [DataContract(Name = "Entity", Namespace = "http://schemas.microsoft.com/xrm/2011/Contracts")]
    private sealed class TestAccount : Entity
    {
        public TestAccount() : base("account") { }
    }

    public static TheoryData<Type> Allowed => new()
    {
        typeof(Entity), typeof(EntityReference[]), typeof(OptionSetValue), typeof(WhoAmIRequest), typeof(AccessRights),
        typeof(Guid), typeof(string), typeof(byte[]), typeof(TestAccount),
        typeof(bool), typeof(int), typeof(long), typeof(decimal), typeof(double), typeof(DateTime), typeof(Uri), typeof(Guid[][]),
    };

    public static TheoryData<Type> Refused => new()
    {
        typeof(DirectoryInfo), typeof(Hashtable), typeof(DataSet), typeof(Version[]), typeof(object), typeof(List<string>), typeof(DBNull),
        Type.GetType("System.UnitySerializationHolder", throwOnError: true)!,
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allowed_types(Type type) => SdkTypeAllowlist.IsAllowed(type).Should().BeTrue();

    [Theory]
    [MemberData(nameof(Refused))]
    public void Refused_types(Type type) => SdkTypeAllowlist.IsAllowed(type).Should().BeFalse();

    private static DataContractSerializer Serializer() =>
        new(typeof(ParameterCollection), new DataContractSerializerSettings { DataContractResolver = new SdkTypeAllowlistResolver() });

    private static string Write(ParameterCollection parameters, DataContractResolver resolver)
    {
        var text = new StringBuilder();
        var serializer = new DataContractSerializer(typeof(ParameterCollection),
            new DataContractSerializerSettings { DataContractResolver = resolver, KnownTypes = [typeof(Version)] });
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            serializer.WriteObject(writer, parameters);
        return text.ToString();
    }

    private static ParameterCollection Read(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        return (ParameterCollection)Serializer().ReadObject(reader)!;
    }

    [Fact]
    public void Resolver_round_trips_sdk_values()
    {
        var parameters = new ParameterCollection
        {
            ["Target"] = new Entity("account", Guid.NewGuid()) { ["name"] = "a", ["statuscode"] = new OptionSetValue(1) },
            ["Ids"] = new[] { Guid.NewGuid() },
            ["Rights"] = AccessRights.ReadAccess,
        };

        var read = Read(Write(parameters, new SdkTypeAllowlistResolver()));

        read["Target"].Should().BeOfType<Entity>().Which["name"].Should().Be("a");
        read["Ids"].Should().BeOfType<Guid[]>();
        read["Rights"].Should().Be(AccessRights.ReadAccess);
    }

    [Fact]
    public void Resolver_refuses_to_read_a_type_outside_the_allowlist()
    {
        // The SDK's resolver knows DataSet: a hostile payload can name it.
        var xml = Write(new ParameterCollection { ["v"] = new DataSet() }, new KnownTypesResolver());

        var act = () => Read(xml);

        act.Should().Throw<SerializationException>().WithMessage("Type 'System.Data.DataSet' is not allowed.");
    }

    [Fact]
    public void Resolver_refuses_to_write_a_type_outside_the_allowlist()
    {
        var act = () => Write(new ParameterCollection { ["v"] = new Version(1, 2) }, new SdkTypeAllowlistResolver());

        act.Should().Throw<SerializationException>().WithMessage("Type 'System.Version' is not allowed.");
    }
}
