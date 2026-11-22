using FluentAssertions;
using Xrmd.Core.Errors;

namespace Xrmd.Security.Tests;

/// <summary>
/// The faults field-level security raises (plan 14): each factory carries the documented code and HTTP status, and names what
/// it is about in its message.
/// </summary>
public sealed class FieldSecurityFaultTests
{
    private static readonly Guid User = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Record = new("66666666-7777-8888-9999-000000000000");

    public static TheoryData<string, XrmdFaultException, uint, int, string?> Faults() => new()
    {
        { "FieldCreateDenied", XrmdFaultException.FieldCreateDenied("account", "new_salary", User), 0x8004F502, 403, "new_salary" },
        { "FieldUpdateDenied", XrmdFaultException.FieldUpdateDenied("account", "new_salary", User), 0x8004F507, 403, "new_salary" },
        { "FieldReadDenied", XrmdFaultException.FieldReadDenied("account", "new_salary", User), 0x8004F504, 403, "new_salary" },
        { "NotSecurable", XrmdFaultException.NotSecurable("account.name"), 0x8004F501, 400, "account.name" },
        { "AdminProfileReadOnly", XrmdFaultException.AdminProfileReadOnly(), 0x8004F50C, 403, null },
        { "PermissionOnUnsecuredColumn", XrmdFaultException.PermissionOnUnsecuredColumn("account", "new_note"), 0x8004F50E, 400, "new_note" },
        { "UnsecureWithoutPermissions", XrmdFaultException.UnsecureWithoutPermissions("account", "new_salary", User), 0x8004F508, 403, "new_salary" },
        { "ShareWithoutAccess", XrmdFaultException.ShareWithoutAccess(), 0x8004F509, 400, null },
        { "ShareUpdateWithoutAccess", XrmdFaultException.ShareUpdateWithoutAccess(), 0x8004F50A, 400, null },
        { "ShareDuplicate", XrmdFaultException.ShareDuplicate("new_salary", Record, User), 0x8004F50B, 400, "new_salary" },
        { "ShareWithoutUpdate", XrmdFaultException.ShareWithoutUpdate("new_salary", Record, User), 0x8004F503, 403, "new_salary" },
        { "ShareWithoutRead", XrmdFaultException.ShareWithoutRead("new_salary", Record, User), 0x8004F503, 403, "new_salary" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void Field_security_faults_carry_their_code_and_status(string name, XrmdFaultException fault, uint code, int status, string? names)
    {
        fault.ErrorCode.Should().Be(unchecked((int)code), name);
        fault.HttpStatus.Should().Be(status, name);
        fault.Message.Should().NotBeNullOrWhiteSpace();
        if (names is not null) fault.Message.Should().Contain(names, name);
    }

    [Fact]
    public void Error_code_constants_match_the_factories()
    {
        (ErrorCodes.FieldNotSecurable, ErrorCodes.FieldCreateDenied, ErrorCodes.ShareWithoutUpdate, ErrorCodes.FieldReadDenied)
            .Should().Be((unchecked((int)0x8004F501), unchecked((int)0x8004F502), unchecked((int)0x8004F503), unchecked((int)0x8004F504)));
        (ErrorCodes.FieldUpdateDenied, ErrorCodes.UnsecureWithoutPermissions, ErrorCodes.ShareWithoutAccess, ErrorCodes.ShareUpdateWithoutAccess)
            .Should().Be((unchecked((int)0x8004F507), unchecked((int)0x8004F508), unchecked((int)0x8004F509), unchecked((int)0x8004F50A)));
        (ErrorCodes.ShareDuplicate, ErrorCodes.AdminProfileReadOnly, ErrorCodes.PermissionOnUnsecuredColumn)
            .Should().Be((unchecked((int)0x8004F50B), unchecked((int)0x8004F50C), unchecked((int)0x8004F50E)));
    }
}
