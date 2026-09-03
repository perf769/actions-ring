using ActionsRing.Core.Configuration;
using ActionsRing.Core.Profiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.Core.Tests;

[TestClass]
public sealed class ProfileResolverTests
{
    [TestMethod]
    public void Resolve_SelectsHighestPriorityMatchingProfile()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.ApplicationProfiles =
        [
            Profile("browser-low", priority: 1, "chrome"),
            Profile("browser-high", priority: 20, "CHROME.EXE"),
            Profile("editor", priority: 100, "photoshop"),
        ];

        var selection = ProfileResolver.Resolve(
            configuration,
            new ForegroundApplication("chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe", "Docs"));

        Assert.IsTrue(selection.IsApplicationSpecific);
        Assert.AreEqual("browser-high", selection.Id);
    }

    [TestMethod]
    public void Resolve_SupportsWildcardPathAndAllRuleMatching()
    {
        var profile = Profile("design", 1, "photoshop");
        profile.MatchAllRules = true;
        profile.MatchRules.Add(new ApplicationMatchRule
        {
            Kind = ApplicationMatchKind.ExecutablePath,
            Mode = TextMatchMode.Wildcard,
            Pattern = @"*\Adobe\*\Photoshop.exe",
        });

        var matches = ProfileResolver.Matches(
            profile,
            new ForegroundApplication(
                "Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe",
                "Artwork.psd"));

        Assert.IsTrue(matches);
    }

    [TestMethod]
    public void Resolve_FallsBackToGlobalWhenNoProfileMatches()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.ApplicationProfiles = [Profile("browser", 1, "chrome")];

        var selection = ProfileResolver.Resolve(
            configuration,
            new ForegroundApplication("notepad", null, "Notes"));

        Assert.IsFalse(selection.IsApplicationSpecific);
        Assert.AreEqual(configuration.GlobalProfile.Id, selection.Id);
    }

    [TestMethod]
    public void Resolve_UsesOnlyTheSelectedUserProfile()
    {
        var configuration = ConfigurationDefaults.Create();
        var work = ConfigurationDefaults.CreateDefaultUserProfile();
        work.Id = "work";
        work.Name = "Работа";
        work.ApplicationProfiles = [Profile("work-browser", 10, "chrome")];
        configuration.UserProfiles.Add(work);
        configuration.ActiveUserProfileId = work.Id;

        var selection = ProfileResolver.Resolve(
            configuration,
            new ForegroundApplication("chrome", null, "Browser"));

        Assert.AreEqual("work-browser", selection.Id);
        configuration.ActiveUserProfileId = "user-default";
        Assert.IsFalse(ProfileResolver.Resolve(
            configuration,
            new ForegroundApplication("chrome", null, "Browser")).IsApplicationSpecific);
    }

    [DataTestMethod]
    [DataRow("Документ — Браузер", "*браузер", true, true)]
    [DataRow("report-final.docx", "report-?????.docx", true, true)]
    [DataRow("report.docx", "report-?????.docx", true, false)]
    [DataRow("alphabet", "a**ha*et", false, true)]
    [DataRow("alphabet", "a*z", false, false)]
    public void WildcardRulesMatchWholeTextWithoutCaseOrBacktrackingSurprises(
        string title,
        string pattern,
        bool ignoreCase,
        bool expected)
    {
        var rule = new ApplicationMatchRule
        {
            Kind = ApplicationMatchKind.WindowTitle,
            Mode = TextMatchMode.Wildcard,
            Pattern = pattern,
            IgnoreCase = ignoreCase,
        };

        var actual = ProfileResolver.Matches(rule, new ForegroundApplication(null, null, title));

        Assert.AreEqual(expected, actual);
    }

    private static ApplicationProfile Profile(string id, int priority, string processName) => new()
    {
        Id = id,
        Name = id,
        Priority = priority,
        MatchRules =
        [
            new ApplicationMatchRule
            {
                Kind = ApplicationMatchKind.ProcessName,
                Mode = TextMatchMode.Equals,
                Pattern = processName,
            },
        ],
        RootRing = ConfigurationDefaults.CreateDefaultRing(),
    };
}
