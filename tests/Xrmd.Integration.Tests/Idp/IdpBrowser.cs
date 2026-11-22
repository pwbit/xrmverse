using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Identity.Client;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>What a browser does at the pages of the local identity provider: submit a page's form, and approve a device code.</summary>
internal static partial class IdpBrowser
{
    /// <summary>Posts the page's form: its hidden fields (antiforgery token included) plus <paramref name="fields"/>.</summary>
    public static Task<HttpResponseMessage> SubmitAsync(HttpClient browser, string html, Dictionary<string, string> fields)
    {
        var form = FormRegex().Match(html);
        form.Success.Should().BeTrue("the page has a form: " + html);
        var values = HiddenRegex().Matches(form.Value).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        values.Should().ContainKey("__RequestVerificationToken");
        foreach (var (name, value) in fields) values[name] = value;
        return browser.PostAsync(WebUtility.HtmlDecode(form.Groups[1].Value), Form(values));
    }

    public static FormUrlEncodedContent Form(Dictionary<string, string> values) => new(values);

    /// <summary>
    /// The user's side of the device-code flow: opens the verification URL in a fresh browser (cookies on, no redirects), signs in
    /// with a password, enters the user code and approves it.
    /// </summary>
    public static async Task ApproveDeviceAsync(Func<HttpClient> newBrowser, DeviceCodeResult device, string userName, string password)
    {
        var browser = newBrowser();
        var start = await browser.GetAsync(device.VerificationUrl);
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var login = await browser.GetAsync(start.Headers.Location);
        var signedIn = await SubmitAsync(browser, await login.Content.ReadAsStringAsync(),
            new() { ["username"] = userName, ["password"] = password });
        var entry = await browser.GetAsync(signedIn.Headers.Location);
        entry.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirm = await SubmitAsync(browser, await entry.Content.ReadAsStringAsync(), new() { ["user_code"] = device.UserCode });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = await SubmitAsync(browser, await confirm.Content.ReadAsStringAsync(), new() { ["action"] = "approve" });
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect, await approved.Content.ReadAsStringAsync());
        var done = await browser.GetAsync(approved.Headers.Location);
        done.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [GeneratedRegex("<form method=\"post\" action=\"([^\"]*)\">.*?</form>", RegexOptions.Singleline)]
    private static partial Regex FormRegex();

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")]
    private static partial Regex HiddenRegex();
}
