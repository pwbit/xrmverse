using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Solution;

namespace Xrmd.Solution.Tests;

public sealed class ImportJobLogTests
{
    [Fact]
    public void A_log_over_the_limit_is_truncated_with_a_marker()
    {
        var log = new ImportJobLog(DateTime.UtcNow);
        for (var i = 0; i < 400; i++) log.Warn(ImportJobLog.Entities, "xv_project", null, new string('w', 100) + i);

        var xml = log.ToXml(100, DateTime.UtcNow, maxLength: 8000);

        xml.Length.Should().BeLessThanOrEqualTo(8000);
        var root = XElement.Parse(xml);
        root.ToString().Should().MatchRegex(@"\[log truncated: \d+ entries omitted\]");
        root.Element("solutionManifests")!.Element("solutionManifest")!.Elements("result")
            .Should().Contain(r => (string?)r.Attribute("result") == "success", "the outcome is never cut");
    }

    [Fact]
    public void A_failure_log_over_the_limit_keeps_the_failure()
    {
        var log = new ImportJobLog(DateTime.UtcNow);
        for (var i = 0; i < 400; i++) log.Warn(ImportJobLog.Manifest, "s", null, new string('w', 100) + i);

        var xml = log.ToXml(0, DateTime.UtcNow, unchecked((int)0x80040203), "boom", maxLength: 8000);

        xml.Length.Should().BeLessThanOrEqualTo(8000);
        xml.Should().Contain("errortext=\"boom\"").And.Contain("[log truncated:");
    }

    [Fact]
    public void A_log_under_the_limit_is_not_truncated()
    {
        var log = new ImportJobLog(DateTime.UtcNow);
        log.Warn(ImportJobLog.Entities, "xv_project", null, "one");
        log.ToXml(100, DateTime.UtcNow).Should().NotContain("truncated");
    }
}
