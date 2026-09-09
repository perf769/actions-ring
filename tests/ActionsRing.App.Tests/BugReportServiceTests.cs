using System.Runtime.InteropServices;
using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class BugReportServiceTests
{
    [TestMethod]
    public void IssueFormUsesOfficialRepositoryAndExpectedTemplate()
    {
        var uri = BugReportService.CreateIssueUri("2.3.0", "Microsoft Windows 10.0.26200", Architecture.X64);
        var query = ReadQuery(uri);

        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("github.com", uri.Host);
        Assert.AreEqual("/perf769/actions-ring/issues/new", uri.AbsolutePath);
        Assert.AreEqual(string.Empty, uri.Fragment);
        Assert.AreEqual(2, query.Count);
        Assert.AreEqual("bug_report.md", query["template"]);
        Assert.IsTrue(uri.AbsoluteUri.Length < 2048, "The default form should fit ordinary browser launch URL limits.");
    }

    [TestMethod]
    public void BodyContainsEditableSectionsAndOnlyRequestedSystemMetadata()
    {
        var body = ReadQuery(BugReportService.CreateIssueUri("2.3.0", "Windows 11", Architecture.Arm64))["body"];

        foreach (var heading in new[] { "Что произошло", "Как повторить", "Ожидаемое поведение", "Скриншоты", "Версия и система" })
        {
            StringAssert.Contains(body, "## " + heading);
        }
        StringAssert.Contains(body, "Actions Ring: 2.3.0");
        StringAssert.Contains(body, "ОС: Windows 11");
        StringAssert.Contains(body, "Архитектура: Arm64");
        StringAssert.Contains(body, "скрыв личные данные");
        Assert.AreEqual(3, body.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void QueryRoundTripsUnicodeAndReservedCharactersWithoutAddingParameters()
    {
        const string version = "2.3.0-preview+revision&extra=value#fragment";
        const string operatingSystem = "Windows тест + & ? # = %";
        var uri = BugReportService.CreateIssueUri(version, operatingSystem, Architecture.X64);
        var query = ReadQuery(uri);

        Assert.AreEqual(2, query.Count);
        Assert.AreEqual(string.Empty, uri.Fragment);
        StringAssert.Contains(query["body"], version);
        StringAssert.Contains(query["body"], operatingSystem);
        StringAssert.Contains(uri.Query, "%2B");
        StringAssert.Contains(uri.Query, "%26");
        StringAssert.Contains(uri.Query, "%23");
    }

    [TestMethod]
    public void CurrentMachineFormDoesNotIncludeIdentityPathsLogsOrConfiguration()
    {
        var body = ReadQuery(BugReportService.CreateIssueUri())["body"];
        var metadataOnlyBody = ReadQuery(BugReportService.CreateIssueUri(
            typeof(BugReportService).Assembly.GetName().Version!.ToString(3),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture))["body"];
        Assert.AreEqual(metadataOnlyBody, body, "The automatic form must use only the three explicitly allowed metadata fields.");
        var disallowed = new[]
        {
            @"\Users\",
            "settings.json",
            "AppData",
            "LaunchTarget",
            "AppUserModelId",
        };
        foreach (var value in disallowed.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            Assert.IsFalse(body.Contains(value, StringComparison.OrdinalIgnoreCase), "Unexpected local data in report body.");
        }
    }

    [TestMethod]
    public void MetadataCannotInjectAdditionalMarkdownLines()
    {
        var body = ReadQuery(BugReportService.CreateIssueUri("2.3.0\r\n# Extra", "Windows\nOther", Architecture.X64))["body"];

        StringAssert.Contains(body, "Actions Ring: 2.3.0  # Extra");
        StringAssert.Contains(body, "ОС: Windows Other");
        Assert.IsFalse(body.Contains("\n# Extra", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("\nOther", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> ReadQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
}
