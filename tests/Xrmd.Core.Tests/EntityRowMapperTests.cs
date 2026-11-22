using System.Data;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;

namespace Xrmd.Core.Tests;

/// <summary>Plan 2.11 A7: an EntityName column is written as the object type code of the table it names.</summary>
public sealed class EntityRowMapperTests
{
    private static readonly IReadOnlyDictionary<string, AttributeMetadata> Attrs = new Dictionary<string, AttributeMetadata>
    {
        ["activitytypecode"] = new EntityNameAttributeMetadata("ActivityTypeCode") { LogicalName = "activitytypecode" },
    };

    private static EntityMetadata? Table(string name)
    {
        if (name != "email") return null;
        var e = new EntityMetadata { LogicalName = "email" };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.GetSetMethod(true)!.Invoke(e, [(int?)4202]);
        return e;
    }

    [Fact]
    public void Entityname_value_is_written_as_the_object_type_code()
    {
        var p = EntityRowMapper.ToParameters(new Entity("activitypointer") { ["activitytypecode"] = "email" }, Attrs, Table).Single();
        p.ParameterName.Should().Be("@activitytypecode");
        p.DbType.Should().Be(DbType.Int32);
        p.Value.Should().Be(4202);
    }

    [Fact]
    public void Entityname_value_naming_no_table_is_400()
    {
        var write = () => EntityRowMapper.ToParameters(new Entity("activitypointer") { ["activitytypecode"] = "nosuchtable" }, Attrs, Table);
        var fault = write.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("nosuchtable");
    }
}
