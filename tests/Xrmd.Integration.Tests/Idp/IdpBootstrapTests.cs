using FluentAssertions;
using Xrmd.Host.Idp;
using static Xrmd.Integration.Tests.Idp.LocalIdpTestHost;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, Task 5: the one-shot initial administrator password file (<c>Idp:InitialAdminPasswordFile</c>).</summary>
[Collection("xrmd")]
public sealed class IdpBootstrapTests(XrmdFixture fx)
{
    private static string NewFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "xrmd-idp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    [Fact]
    public async Task The_file_creates_the_administrators_credential_and_is_deleted()
    {
        var file = NewFile("initial admin pw 1\n");
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", Audience);
            b.UseSetting("Idp:InitialAdminPasswordFile", file);
        });
        app.CreateClient().Dispose(); // starts the host

        File.Exists(file).Should().BeFalse();
        var result = await WithCredentialsAsync(app, c => c.SignInAsync(XrmdFixture.AdminName, "initial admin pw 1", null, default));
        result.Should().BeOfType<SignInResult.Succeeded>();
    }

    [Fact]
    public async Task An_existing_credential_is_never_overwritten_and_the_file_is_still_deleted()
    {
        await using var db = await IdpDatabase.CreateAsync(fx.ConnectionString);
        var first = NewFile("first password 111");
        var second = NewFile("another password 2");
        for (var start = 0; start < 2; start++)
        {
            await using var app = fx.App.WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:Org", db.ConnectionString);
                b.UseSetting("Auth:Mode", "Local");
                b.UseSetting("Auth:Audience", Audience);
                b.UseSetting("Idp:InitialAdminPasswordFile", start == 0 ? first : second);
            });
            app.CreateClient().Dispose();
            if (start == 1)
            {
                File.Exists(first).Should().BeFalse();
                File.Exists(second).Should().BeFalse();
                (await WithCredentialsAsync(app, c => c.SignInAsync(XrmdFixture.AdminName, "first password 111", null, default)))
                    .Should().BeOfType<SignInResult.Succeeded>();
                (await WithCredentialsAsync(app, c => c.SignInAsync(XrmdFixture.AdminName, "another password 2", null, default)))
                    .Should().Be(SignInResult.Failed);
            }
        }
    }

    /// <summary>A host started with <paramref name="file"/> as the password file; true when the administrator got a credential.</summary>
    private async Task<bool> AdminGetsCredentialAsync(string file)
    {
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", Audience);
            b.UseSetting("Idp:InitialAdminPasswordFile", file);
        });
        app.CreateClient().Dispose();
        return await WithCredentialsAsync(app, c => c.SignInAsync(XrmdFixture.AdminName, "planted password 9", null, default))
            is SignInResult.Succeeded;
    }

    [Fact]
    public async Task A_symlink_is_refused_and_left_in_place()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = NewFile("planted password 9");
        var link = Path.Combine(Path.GetTempPath(), "xrmd-idp-link-" + Guid.NewGuid().ToString("N"));
        File.CreateSymbolicLink(link, target);

        (await AdminGetsCredentialAsync(link)).Should().BeFalse();

        new FileInfo(link).LinkTarget.Should().NotBeNull("the file is left in place");
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public async Task A_hard_linked_file_is_refused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var original = NewFile("planted password 9");
        var linked = original + ".link";
        System.Diagnostics.Process.Start("ln", [original, linked])!.WaitForExit();
        File.Exists(linked).Should().BeTrue("the test needs a second link");

        (await AdminGetsCredentialAsync(original)).Should().BeFalse();

        File.Exists(original).Should().BeTrue();
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite)]
    public async Task A_file_wider_than_0640_is_refused(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        var file = NewFile("planted password 9");
        File.SetUnixFileMode(file, mode);

        (await AdminGetsCredentialAsync(file)).Should().BeFalse();

        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task A_0640_file_is_accepted()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = NewFile("planted password 9");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        (await AdminGetsCredentialAsync(file)).Should().BeTrue();
        File.Exists(file).Should().BeFalse();
    }
}
