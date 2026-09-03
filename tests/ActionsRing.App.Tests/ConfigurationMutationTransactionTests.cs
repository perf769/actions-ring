using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ConfigurationMutationTransactionTests
{
    [TestMethod]
    public async Task FailedSave_RestoresDeletedProfileWithoutReplacingLiveRoot()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.ApplicationProfiles.Add(new ApplicationProfile
        {
            Id = "profile-editor",
            Name = "Редактор",
            RootRing = ConfigurationDefaults.CreateDefaultRing(),
        });
        var liveRoot = configuration;
        var transaction = ConfigurationMutationTransaction.Capture(configuration);

        configuration.ApplicationProfiles.RemoveAll(profile => profile.Id == "profile-editor");
        var committed = await transaction.TryCommitAsync(() => Task.FromResult(false));

        Assert.IsFalse(committed);
        Assert.AreSame(liveRoot, configuration);
        Assert.AreEqual(1, configuration.ApplicationProfiles.Count);
        Assert.AreEqual("profile-editor", configuration.ApplicationProfiles[0].Id);
    }

    [TestMethod]
    public async Task FailedSave_RestoresClearedSlotGraph()
    {
        var configuration = ConfigurationDefaults.Create();
        var original = configuration.GlobalProfile.RootRing.Slots[0];
        var originalId = original.Id;
        var originalLabel = original.Label;
        var originalKind = original.Action!.Kind;
        var transaction = ConfigurationMutationTransaction.Capture(configuration);

        original.Label = "Добавить действие";
        original.Icon = null;
        original.Submenu = null;
        original.Action = ActionDefinition.None();
        var committed = await transaction.TryCommitAsync(() => Task.FromResult(false));

        Assert.IsFalse(committed);
        var restored = configuration.GlobalProfile.RootRing.Slots.Single(slot => slot.Id == originalId);
        Assert.AreEqual(originalLabel, restored.Label);
        Assert.AreEqual(originalKind, restored.Action?.Kind);
    }

    [TestMethod]
    public async Task FailedSave_RestoresActiveUserProfileAndItsApplicationGraph()
    {
        var configuration = ConfigurationDefaults.Create();
        var work = ConfigurationDefaults.CreateDefaultUserProfile();
        work.Id = "user-work";
        work.Name = "Работа";
        work.GlobalProfile.Id = "work-global";
        work.ApplicationProfiles.Add(new ApplicationProfile
        {
            Id = "work-editor",
            Name = "Редактор",
            MatchRules =
            [
                new ApplicationMatchRule
                {
                    Kind = ApplicationMatchKind.ProcessName,
                    Pattern = "editor",
                },
            ],
            RootRing = ConfigurationDefaults.CreateDefaultRing(),
        });
        configuration.UserProfiles.Add(work);
        configuration.ActiveUserProfileId = work.Id;
        ConfigurationNormalizer.Normalize(configuration);
        var transaction = ConfigurationMutationTransaction.Capture(configuration);

        configuration.UserProfiles.RemoveAll(profile => profile.Id == work.Id);
        configuration.ActiveUserProfileId = "user-default";
        var committed = await transaction.TryCommitAsync(() => Task.FromResult(false));

        Assert.IsFalse(committed);
        Assert.AreEqual("user-work", configuration.ActiveUserProfileId);
        var restored = configuration.GetActiveUserProfile();
        Assert.AreEqual("Работа", restored.Name);
        Assert.AreEqual("work-editor", restored.ApplicationProfiles.Single().Id);
    }

    [TestMethod]
    public async Task SuccessfulSave_KeepsMutation()
    {
        var configuration = ConfigurationDefaults.Create();
        var transaction = ConfigurationMutationTransaction.Capture(configuration);
        configuration.Preferences.General.CloseToTray = false;

        var committed = await transaction.TryCommitAsync(() => Task.FromResult(true));

        Assert.IsTrue(committed);
        Assert.IsFalse(configuration.Preferences.General.CloseToTray);
        Assert.IsFalse(transaction.IsActive);
    }

    [TestMethod]
    public async Task ThrowingSave_RollsBackBeforeRethrowing()
    {
        var configuration = ConfigurationDefaults.Create();
        var transaction = ConfigurationMutationTransaction.Capture(configuration);
        configuration.GlobalProfile.Name = "Несохранённое имя";

        await Assert.ThrowsExceptionAsync<IOException>(
            () => transaction.TryCommitAsync(() => Task.FromException<bool>(new IOException("locked"))));

        Assert.AreEqual("Все приложения", configuration.GlobalProfile.Name);
        Assert.IsFalse(transaction.IsActive);
    }
}
