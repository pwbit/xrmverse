using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Security;

namespace Xrmd.Security.Tests;

/// <summary>Plan 2.11 A12: the shared privilege stems of the activity tables and of notes.</summary>
public class PrivilegeNamesTests
{
    private static EntityMetadata Table(string name, string schema, bool? isActivity = null)
    {
        var e = new EntityMetadata { LogicalName = name, SchemaName = schema };
        if (isActivity is { } flag) typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.IsActivity))!.GetSetMethod(true)!.Invoke(e, [flag]);
        return e;
    }

    [Fact]
    public void Stem_is_activity_for_activity_tables()
    {
        PrivilegeNames.Stem(Table("email", "Email", true)).Should().Be("Activity");
        PrivilegeNames.Stem(Table("xv_sms", "xv_Sms", true)).Should().Be("Activity");
        PrivilegeNames.Stem(Table("activitypointer", "ActivityPointer", false)).Should().Be("Activity");
        PrivilegeNames.Stem(Table("annotation", "Annotation")).Should().Be("Note");
        PrivilegeNames.Stem(Table("account", "Account")).Should().Be("Account");
        PrivilegeNames.For(AccessRights.ReadAccess, Table("email", "Email", true)).Should().Be("prvReadActivity");
        PrivilegeNames.For(AccessRights.ShareAccess, Table("xv_sms", "xv_Sms", true)).Should().Be("prvShareActivity");
        PrivilegeNames.For(AccessRights.WriteAccess, Table("annotation", "Annotation")).Should().Be("prvWriteNote");
        PrivilegeNames.For(AccessRights.ReadAccess, Table("account", "Account")).Should().Be("prvReadAccount");
    }
}
