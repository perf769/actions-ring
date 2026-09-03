using ActionsRing.Core.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.Core.Tests;

[TestClass]
public sealed class UpdatePreferencesTests
{
    [TestMethod]
    public void Defaults_EnableChecksButRequireExplicitDownloadChoice()
    {
        var updates = ConfigurationDefaults.Create().Preferences.Updates;

        Assert.IsTrue(updates.CheckAutomatically);
        Assert.IsFalse(updates.DownloadAutomatically);
        Assert.IsNull(updates.SkippedVersion);
        Assert.IsNull(updates.LastCheckedAtUtc);
    }

    [TestMethod]
    public void DocumentParser_MigratesLegacyCheckPreferenceIntoUpdateSettings()
    {
        const string json = """
            {
              "schemaVersion": 3,
              "preferences": {
                "general": { "checkForUpdates": false },
                "appearance": {}
              },
              "activeUserProfileId": "user-default",
              "userProfiles": []
            }
            """;

        var parsed = ConfigurationDocumentParser.Parse(json);

        Assert.AreEqual(ConfigurationSchema.CurrentVersion, parsed.Configuration.SchemaVersion);
        Assert.IsFalse(parsed.Configuration.Preferences.Updates.CheckAutomatically);
        Assert.IsFalse(parsed.Configuration.Preferences.Updates.DownloadAutomatically);
        Assert.IsFalse(ConfigurationJson.Serialize(parsed.Configuration).Contains(
            "checkForUpdates",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Normalize_RepairsSkippedVersionAndConvertsLastCheckToUtc()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Preferences.Updates.SkippedVersion = " definitely-not-semver ";
        configuration.Preferences.Updates.LastCheckedAtUtc =
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.FromHours(3));

        var result = ConfigurationNormalizer.Normalize(configuration);

        Assert.IsNull(configuration.Preferences.Updates.SkippedVersion);
        Assert.AreEqual(TimeSpan.Zero, configuration.Preferences.Updates.LastCheckedAtUtc!.Value.Offset);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "updates.skippedVersion.removed"));
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Json_RoundTripPreservesUpdatePreferences()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Preferences.Updates.CheckAutomatically = true;
        configuration.Preferences.Updates.DownloadAutomatically = true;
        configuration.Preferences.Updates.SkippedVersion = "2.1.0-beta.2+win64";
        configuration.Preferences.Updates.LastCheckedAtUtc =
            new DateTimeOffset(2026, 9, 3, 7, 30, 0, TimeSpan.Zero);

        var roundTripped = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(configuration));

        Assert.IsTrue(roundTripped.Preferences.Updates.CheckAutomatically);
        Assert.IsTrue(roundTripped.Preferences.Updates.DownloadAutomatically);
        Assert.AreEqual("2.1.0-beta.2+win64", roundTripped.Preferences.Updates.SkippedVersion);
        Assert.AreEqual(configuration.Preferences.Updates.LastCheckedAtUtc, roundTripped.Preferences.Updates.LastCheckedAtUtc);
        Assert.IsTrue(ConfigurationValidator.Validate(roundTripped).IsValid);
    }

    [TestMethod]
    public void Validator_RejectsNonUtcCheckTimeAndInvalidSkippedVersion()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Preferences.Updates.SkippedVersion = "v2";
        configuration.Preferences.Updates.LastCheckedAtUtc =
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(3));

        var result = ConfigurationValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "updates.skippedVersion.invalid"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "updates.lastCheckedAtUtc.notUtc"));
    }

    [TestMethod]
    public void Normalize_DisablesAutomaticDownloadWhenAutomaticChecksAreOff()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Preferences.Updates.CheckAutomatically = false;
        configuration.Preferences.Updates.DownloadAutomatically = true;

        var result = ConfigurationNormalizer.Normalize(configuration);

        Assert.IsFalse(configuration.Preferences.Updates.DownloadAutomatically);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "updates.autoDownload.disabled"));
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }
}
