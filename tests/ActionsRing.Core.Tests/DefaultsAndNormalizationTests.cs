using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.Core.Tests;

[TestClass]
public sealed class DefaultsAndNormalizationTests
{
    [TestMethod]
    public void Defaults_AreImmediatelyValidAndUseForwardThumbButton()
    {
        var configuration = ConfigurationDefaults.Create();

        var validation = ConfigurationValidator.Validate(configuration);

        Assert.IsTrue(validation.IsValid);
        Assert.AreEqual(ConfigurationSchema.CurrentVersion, configuration.SchemaVersion);
        Assert.AreEqual("user-default", configuration.ActiveUserProfileId);
        Assert.AreEqual(1, configuration.UserProfiles.Count);
        Assert.AreEqual("Основной", configuration.GetActiveUserProfile().Name);
        Assert.AreEqual(InputBindingKind.MouseButton, configuration.Trigger.Kind);
        Assert.AreEqual(MouseButton.XButton2, configuration.Trigger.Button);
        Assert.AreEqual(KeyboardModifiers.None, configuration.Trigger.MouseModifiers);
        Assert.AreEqual(ActivationMode.Hold, configuration.Trigger.ActivationMode);
        Assert.AreEqual(8, configuration.GlobalProfile.RootRing.SlotCount);
        Assert.AreEqual(8, configuration.GlobalProfile.RootRing.Slots.Count);
        Assert.AreEqual("#824EF9", configuration.Preferences.Appearance.AccentColor);
        Assert.AreEqual(212, configuration.Preferences.Appearance.RingDiameter);
        Assert.AreEqual(32, configuration.Preferences.Appearance.CenterCloseDiameter);
        Assert.AreEqual(240, configuration.Preferences.Appearance.OpenAnimationMilliseconds);
        Assert.AreEqual(360, configuration.Preferences.Appearance.SubmenuAnimationMilliseconds);
        Assert.AreEqual(350, configuration.Preferences.Appearance.TooltipDelayMilliseconds);
        Assert.IsTrue(configuration.Preferences.General.ShowKeyStateNotifications);
    }

    [TestMethod]
    public void Json_RoundTripPreservesHorizontalWheelTriggerAndModifiers()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.MouseButton,
            Button = MouseButton.WheelRight,
            MouseModifiers = KeyboardModifiers.Control | KeyboardModifiers.Shift,
            ActivationMode = ActivationMode.Toggle,
        };

        var roundTripped = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(configuration));

        Assert.AreEqual(MouseButton.WheelRight, roundTripped.Trigger.Button);
        Assert.AreEqual(
            KeyboardModifiers.Control | KeyboardModifiers.Shift,
            roundTripped.Trigger.MouseModifiers);
    }

    [TestMethod]
    public void Normalize_ClampsRootAndSubmenuUsingTheirDifferentLimits()
    {
        var configuration = ConfigurationDefaults.Create();
        var root = configuration.GlobalProfile.RootRing;
        root.SlotCount = 2;
        root.Slots =
        [
            RingSlotDefinition.ForSubmenu(
                "Folder",
                new RingDefinition
                {
                    SlotCount = 15,
                    Slots = Enumerable.Range(0, 15).Select(RingSlotDefinition.Empty).ToList(),
                }),
        ];

        var result = ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual(4, root.SlotCount);
        Assert.AreEqual(4, root.Slots.Count);
        Assert.IsNotNull(root.Slots[0].Submenu);
        var submenu = root.Slots[0].Submenu!;
        Assert.AreEqual(9, submenu.SlotCount);
        Assert.AreEqual(9, submenu.Slots.Count);
        Assert.IsTrue(result.Issues.Count > 0);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Normalize_RepairsDuplicateIdsAndMalformedKeyboardTrigger()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.Keyboard,
            Keyboard = new KeyChord
            {
                Key = "  f12 ",
                Modifiers = KeyboardModifiers.Control | (KeyboardModifiers)128,
            },
            Button = MouseButton.Left,
            ActivationMode = ActivationMode.Toggle,
        };

        var root = configuration.GlobalProfile.RootRing;
        root.Slots[0].Id = "duplicate";
        root.Slots[1].Id = "duplicate";

        ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual("F12", configuration.Trigger.Keyboard!.Key);
        Assert.AreEqual(KeyboardModifiers.Control, configuration.Trigger.Keyboard.Modifiers);
        Assert.IsNull(configuration.Trigger.Button);
        Assert.AreNotEqual(root.Slots[0].Id, root.Slots[1].Id);
    }

    [TestMethod]
    public void Normalize_RepairsSequenceAndAdjustableAction()
    {
        var configuration = ConfigurationDefaults.Create();
        var sequence = new ActionDefinition
        {
            Kind = ActionKind.Sequence,
            Name = "Workflow",
            Sequence = new SequenceAction
            {
                Steps =
                [
                    new ActionSequenceStep
                    {
                        DelayAfterMilliseconds = -1,
                        Action = ActionDefinition.Shortcut("Save", "s", KeyboardModifiers.Control),
                    },
                    new ActionSequenceStep
                    {
                        DelayAfterMilliseconds = 250,
                        Action = new ActionDefinition
                        {
                            Kind = ActionKind.AdjustParameter,
                            Name = "Volume up",
                            AdjustParameter = new AdjustParameterAction
                            {
                                Parameter = AdjustableParameter.SystemVolume,
                                Mode = AdjustmentMode.Relative,
                                Value = 5,
                            },
                        },
                    },
                ],
            },
        };
        configuration.GlobalProfile.RootRing.Slots[0] = RingSlotDefinition.ForAction(sequence);

        ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual(ActionKind.Sequence, sequence.Kind);
        Assert.AreEqual(0, sequence.Sequence!.Steps[0].DelayAfterMilliseconds);
        Assert.AreEqual(AdjustableParameter.SystemVolume, sequence.Sequence.Steps[1].Action.AdjustParameter!.Parameter);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Normalize_UnsupportedActionShortcutDoesNotInvalidateOtherSettings()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.GetActiveUserProfile().Name = "Мой профиль";
        configuration.GlobalProfile.RootRing.Slots[0] = RingSlotDefinition.ForAction(
            ActionDefinition.Shortcut("Неизвестная клавиша", "Banana"));

        var result = ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual("Мой профиль", configuration.GetActiveUserProfile().Name);
        Assert.AreEqual(ActionKind.None, configuration.GlobalProfile.RootRing.Slots[0].Action!.Kind);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "shortcut.key.removed"));
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Json_RoundTripPreservesNestedTypedActionsAndEnums()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.Keyboard,
            Keyboard = new KeyChord { Key = "K", Modifiers = KeyboardModifiers.Control | KeyboardModifiers.Shift },
            ActivationMode = ActivationMode.Toggle,
        };

        var json = ConfigurationJson.Serialize(configuration);
        var roundTripped = ConfigurationJson.Deserialize(json);

        Assert.AreEqual(InputBindingKind.Keyboard, roundTripped.Trigger.Kind);
        Assert.AreEqual("K", roundTripped.Trigger.Keyboard!.Key);
        Assert.AreEqual(
            KeyboardModifiers.Control | KeyboardModifiers.Shift,
            roundTripped.Trigger.Keyboard.Modifiers);
        StringAssert.Contains(json, "\"kind\": \"keyboard\"");
        StringAssert.Contains(json, "\"activationMode\": \"toggle\"");
        StringAssert.Contains(json, "\"userProfiles\"");
        Assert.IsFalse(json.Contains("\"globalProfile\"", StringComparison.Ordinal)
                       && json.IndexOf("\"globalProfile\"", StringComparison.Ordinal)
                       < json.IndexOf("\"userProfiles\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Normalize_RepairsMissingAndDuplicateUserProfiles()
    {
        var configuration = ConfigurationDefaults.Create();
        var duplicate = ConfigurationDefaults.CreateDefaultUserProfile();
        duplicate.Name = "Работа";
        configuration.UserProfiles.Add(duplicate);
        configuration.ActiveUserProfileId = "missing";

        var result = ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual(2, configuration.UserProfiles.Count);
        Assert.AreNotEqual(configuration.UserProfiles[0].Id, configuration.UserProfiles[1].Id);
        Assert.AreEqual(configuration.UserProfiles[0].Id, configuration.ActiveUserProfileId);
        Assert.IsTrue(result.Issues.Count > 0);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Normalize_ValidatesPerProfilePaletteOverrides()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.GlobalProfile.Style = new RingStyleDefinition
        {
            Preset = RingStylePreset.Custom,
            BubbleColor = "#1a2b3c",
            IconColor = "not-a-color",
            HoverColor = "#80112233",
        };

        ConfigurationNormalizer.Normalize(configuration);

        Assert.AreEqual("#1A2B3C", configuration.GlobalProfile.Style.BubbleColor);
        Assert.IsNull(configuration.GlobalProfile.Style.IconColor);
        Assert.AreEqual("#80112233", configuration.GlobalProfile.Style.HoverColor);
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).IsValid);
    }

    [TestMethod]
    public void Validator_RejectsRawVirtualKeyOutsideWin32Range()
    {
        var configuration = ConfigurationDefaults.Create();
        configuration.Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.Keyboard,
            Keyboard = new KeyChord { Key = "VK_0x0100" },
        };

        var validation = ConfigurationValidator.Validate(configuration);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Issues.Any(issue => issue.Path.Contains("trigger.keyboard.key")));
    }
}
