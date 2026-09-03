using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;

namespace ActionsRing.Core.Configuration;

/// <summary>
/// Repairs untrusted or hand-edited configuration into a deterministic shape. The supplied
/// instance is modified in place; callers that need copy semantics should clone it first.
/// </summary>
public static class ConfigurationNormalizer
{
    public const int MaximumSubmenuDepth = 6;
    public const int MaximumSequenceDepth = 3;
    public const int MaximumUserProfiles = 24;
    public const int MaximumProfiles = 128;
    private const int MaximumMatchRules = 16;
    public const int MaximumShortcutChords = 16;
    public const int MaximumSequenceSteps = 32;
    private const int MaximumActionNodes = 2048;
    private const int MaximumRingNodes = 512;

    private const KeyboardModifiers AllModifiers =
        KeyboardModifiers.Control
        | KeyboardModifiers.Alt
        | KeyboardModifiers.Shift
        | KeyboardModifiers.Windows;
    private const KeyboardModifiers SafeTriggerModifiers =
        KeyboardModifiers.Control | KeyboardModifiers.Shift;

    public static ConfigurationNormalizationResult Normalize(ActionsRingConfiguration? configuration)
    {
        var issues = new List<ConfigurationIssue>();
        if (configuration is null)
        {
            configuration = ConfigurationDefaults.Create();
            Warn("$", "configuration.defaulted", "Missing configuration was replaced with defaults.");
            return new ConfigurationNormalizationResult(configuration, issues);
        }

        if (configuration.SchemaVersion != ConfigurationSchema.CurrentVersion)
        {
            configuration.SchemaVersion = ConfigurationSchema.CurrentVersion;
            Warn("$.schemaVersion", "schema.normalized", "Schema version was set to the current version.");
        }

        NormalizeTrigger(configuration);
        NormalizePreferences(configuration);
        NormalizeOnboarding(configuration);

        var context = new NormalizationContext();
        configuration.UserProfiles ??= [];
        for (var index = configuration.UserProfiles.Count - 1; index >= 0; index--)
        {
            if (configuration.UserProfiles[index] is null)
            {
                configuration.UserProfiles.RemoveAt(index);
                Warn($"$.userProfiles[{index}]", "userProfile.removed", "A null user profile was removed.");
            }
        }

        if (configuration.UserProfiles.Count == 0)
        {
            configuration.UserProfiles.Add(ConfigurationDefaults.CreateDefaultUserProfile());
            Warn("$.userProfiles", "userProfiles.defaulted", "A default user profile was created.");
        }
        else if (configuration.UserProfiles.Count > MaximumUserProfiles)
        {
            configuration.UserProfiles.RemoveRange(
                MaximumUserProfiles,
                configuration.UserProfiles.Count - MaximumUserProfiles);
            Warn("$.userProfiles", "userProfiles.truncated", $"Only the first {MaximumUserProfiles} user profiles were retained.");
        }

        for (var index = 0; index < configuration.UserProfiles.Count; index++)
        {
            NormalizeUserProfile(configuration.UserProfiles[index], index, context);
        }

        var selectedUserProfile = configuration.UserProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, configuration.ActiveUserProfileId, StringComparison.OrdinalIgnoreCase));
        if (selectedUserProfile is null)
        {
            configuration.ActiveUserProfileId = configuration.UserProfiles[0].Id;
            Warn("$.activeUserProfileId", "userProfile.selection.defaulted", "The first user profile was selected.");
        }

        return new ConfigurationNormalizationResult(configuration, issues);

        void NormalizeTrigger(ActionsRingConfiguration document)
        {
            document.Trigger ??= new TriggerBinding();
            var trigger = document.Trigger;
            if (!Enum.IsDefined(trigger.Kind))
            {
                trigger.Kind = InputBindingKind.MouseButton;
                Warn("$.trigger.kind", "trigger.kind.defaulted", "Unknown trigger kind was replaced.");
            }

            if (!Enum.IsDefined(trigger.ActivationMode))
            {
                trigger.ActivationMode = ActivationMode.Hold;
                Warn("$.trigger.activationMode", "trigger.mode.defaulted", "Unknown activation mode was replaced.");
            }

            if (trigger.Kind == InputBindingKind.Keyboard)
            {
                trigger.Keyboard ??= new KeyChord { Key = "Space" };
                NormalizeChord(trigger.Keyboard, "$.trigger.keyboard");
                if ((trigger.Keyboard.Modifiers & ~SafeTriggerModifiers) != 0)
                {
                    document.Trigger = new TriggerBinding();
                    Warn(
                        "$.trigger",
                        "trigger.modifiers.defaulted",
                        "Triggers using Alt or Windows modifiers were replaced with XButton2.");
                    return;
                }
                trigger.Button = null;
                trigger.MouseModifiers = KeyboardModifiers.None;
            }
            else
            {
                if (trigger.Button is null || !Enum.IsDefined(trigger.Button.Value))
                {
                    trigger.Button = MouseButton.XButton2;
                    Warn("$.trigger.button", "trigger.button.defaulted", "Unknown mouse button was replaced with XButton2.");
                }

                trigger.Keyboard = null;
                trigger.MouseModifiers &= AllModifiers;
                if ((trigger.MouseModifiers & ~SafeTriggerModifiers) != 0)
                {
                    document.Trigger = new TriggerBinding();
                    Warn(
                        "$.trigger",
                        "trigger.modifiers.defaulted",
                        "Mouse triggers using Alt or Windows modifiers were replaced with XButton2.");
                }
            }
        }

        void NormalizePreferences(ActionsRingConfiguration document)
        {
            document.Preferences ??= new UserPreferences();
            document.Preferences.General ??= new GeneralPreferences();
            document.Preferences.Appearance ??= new AppearancePreferences();

            var appearance = document.Preferences.Appearance;
            if (!Enum.IsDefined(appearance.Theme))
            {
                appearance.Theme = ThemePreference.System;
                Warn("$.preferences.appearance.theme", "appearance.theme.defaulted", "Unknown theme was replaced.");
            }

            if (!IsColor(appearance.AccentColor))
            {
                appearance.AccentColor = "#824EF9";
                Warn("$.preferences.appearance.accentColor", "appearance.color.defaulted", "Invalid accent color was replaced.");
            }
            else
            {
                appearance.AccentColor = appearance.AccentColor.ToUpperInvariant();
            }

            appearance.RingDiameter = Clamp(
                appearance.RingDiameter,
                160,
                424,
                "$.preferences.appearance.ringDiameter");
            appearance.CenterCloseDiameter = Clamp(
                appearance.CenterCloseDiameter,
                24,
                Math.Min(120, appearance.RingDiameter / 2),
                "$.preferences.appearance.centerCloseDiameter");
            appearance.OpenAnimationMilliseconds = Clamp(
                appearance.OpenAnimationMilliseconds,
                0,
                1000,
                "$.preferences.appearance.openAnimationMilliseconds");
            appearance.SubmenuAnimationMilliseconds = Clamp(
                appearance.SubmenuAnimationMilliseconds,
                0,
                1000,
                "$.preferences.appearance.submenuAnimationMilliseconds");
            appearance.TooltipDelayMilliseconds = Clamp(
                appearance.TooltipDelayMilliseconds,
                0,
                5000,
                "$.preferences.appearance.tooltipDelayMilliseconds");
        }

        void NormalizeOnboarding(ActionsRingConfiguration document)
        {
            document.Onboarding ??= new OnboardingState();
            var onboarding = document.Onboarding;
            onboarding.LastCompletedStep = Clamp(
                onboarding.LastCompletedStep,
                0,
                10,
                "$.onboarding.lastCompletedStep");
            onboarding.LastSeenAppVersion = CleanOptionalText(
                onboarding.LastSeenAppVersion,
                40,
                "$.onboarding.lastSeenAppVersion");

            if (!onboarding.IsCompleted)
            {
                onboarding.CompletedAtUtc = null;
            }
            else
            {
                onboarding.WelcomeCompleted = true;
                onboarding.TriggerSetupCompleted = true;
            }
        }

        void NormalizeUserProfile(
            UserProfile userProfile,
            int userProfileIndex,
            NormalizationContext normalizationContext)
        {
            var path = $"$.userProfiles[{userProfileIndex}]";
            userProfile.Id = EnsureId(
                userProfile.Id,
                "user-default",
                "user",
                path + ".id",
                normalizationContext.UserProfileIds);
            userProfile.Name = CleanText(userProfile.Name, "Профиль", 80, path + ".name");

            userProfile.GlobalProfile ??= new RingProfile();
            userProfile.GlobalProfile.Id = EnsureId(
                userProfile.GlobalProfile.Id,
                "global",
                "profile",
                path + ".globalProfile.id",
                normalizationContext.ProfileIds);
            userProfile.GlobalProfile.Name = CleanText(
                userProfile.GlobalProfile.Name,
                "Все приложения",
                80,
                path + ".globalProfile.name");
            userProfile.GlobalProfile.Style ??= new RingStyleDefinition();
            NormalizeRingStyle(userProfile.GlobalProfile.Style, path + ".globalProfile.style");
            userProfile.GlobalProfile.RootRing ??= ConfigurationDefaults.CreateDefaultRing();
            NormalizeRing(userProfile.GlobalProfile.RootRing, path + ".globalProfile.rootRing", 0, normalizationContext);

            userProfile.ApplicationProfiles ??= [];
            if (userProfile.ApplicationProfiles.Count > MaximumProfiles)
            {
                userProfile.ApplicationProfiles.RemoveRange(
                    MaximumProfiles,
                    userProfile.ApplicationProfiles.Count - MaximumProfiles);
                Warn(path + ".applicationProfiles", "profiles.truncated", $"Only the first {MaximumProfiles} profiles were retained.");
            }

            for (var index = userProfile.ApplicationProfiles.Count - 1; index >= 0; index--)
            {
                if (userProfile.ApplicationProfiles[index] is null)
                {
                    userProfile.ApplicationProfiles.RemoveAt(index);
                    Warn($"{path}.applicationProfiles[{index}]", "profile.removed", "A null profile was removed.");
                }
            }

            for (var index = 0; index < userProfile.ApplicationProfiles.Count; index++)
            {
                NormalizeApplicationProfile(
                    userProfile.ApplicationProfiles[index],
                    index,
                    path + ".applicationProfiles",
                    normalizationContext);
            }
        }

        void NormalizeApplicationProfile(
            ApplicationProfile profile,
            int profileIndex,
            string collectionPath,
            NormalizationContext normalizationContext)
        {
            var path = $"{collectionPath}[{profileIndex}]";
            profile.Id = EnsureId(profile.Id, "profile", "profile", path + ".id", normalizationContext.ProfileIds);
            profile.Name = CleanText(profile.Name, "Приложение", 80, path + ".name");
            profile.Priority = Clamp(profile.Priority, -10_000, 10_000, path + ".priority");
            profile.MatchRules ??= [];
            profile.AppUserModelId = CleanOptionalText(profile.AppUserModelId, 512, path + ".appUserModelId");
            profile.LaunchTarget = CleanOptionalText(profile.LaunchTarget, 4096, path + ".launchTarget");
            profile.IconPath = CleanOptionalText(profile.IconPath, 4096, path + ".iconPath");
            profile.Style ??= new RingStyleDefinition();
            NormalizeRingStyle(profile.Style, path + ".style");

            if (profile.MatchRules.Count > MaximumMatchRules)
            {
                profile.MatchRules.RemoveRange(MaximumMatchRules, profile.MatchRules.Count - MaximumMatchRules);
                Warn(path + ".matchRules", "rules.truncated", $"Only the first {MaximumMatchRules} match rules were retained.");
            }

            for (var ruleIndex = profile.MatchRules.Count - 1; ruleIndex >= 0; ruleIndex--)
            {
                var rule = profile.MatchRules[ruleIndex];
                if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    profile.MatchRules.RemoveAt(ruleIndex);
                    Warn($"{path}.matchRules[{ruleIndex}]", "rule.removed", "An empty match rule was removed.");
                    continue;
                }

                if (!Enum.IsDefined(rule.Kind))
                {
                    rule.Kind = ApplicationMatchKind.ProcessName;
                    Warn($"{path}.matchRules[{ruleIndex}].kind", "rule.kind.defaulted", "Unknown match kind was replaced.");
                }

                if (!Enum.IsDefined(rule.Mode))
                {
                    rule.Mode = TextMatchMode.Equals;
                    Warn($"{path}.matchRules[{ruleIndex}].mode", "rule.mode.defaulted", "Unknown match mode was replaced.");
                }

                rule.Pattern = CleanText(rule.Pattern, string.Empty, 1024, $"{path}.matchRules[{ruleIndex}].pattern");
            }

            if (profile.IsEnabled && profile.MatchRules.Count == 0)
            {
                profile.IsEnabled = false;
                Warn(path + ".isEnabled", "profile.disabled", "A profile without match rules was disabled.");
            }

            profile.RootRing ??= new RingDefinition { Name = profile.Name };
            NormalizeRing(profile.RootRing, path + ".rootRing", 0, normalizationContext);
        }

        void NormalizeRing(RingDefinition ring, string path, int depth, NormalizationContext normalizationContext)
        {
            ring.Id = EnsureId(ring.Id, "ring", "ring", path + ".id", normalizationContext.RingIds);
            ring.Name = CleanText(ring.Name, depth == 0 ? "Кольцо действий" : "Подменю", 80, path + ".name");
            ring.Appearance ??= RingAppearanceDefinition.CreateDefault();
            NormalizeRingAppearance(ring.Appearance, path + ".appearance");
            var minimumSlots = depth == 0
                ? RingDefinition.MinimumRootSlots
                : RingDefinition.MinimumSubmenuSlots;
            var maximumSlots = depth == 0
                ? RingDefinition.MaximumRootSlots
                : RingDefinition.MaximumSubmenuSlots;
            normalizationContext.RingNodes++;
            if (normalizationContext.RingNodes > MaximumRingNodes)
            {
                ring.SlotCount = minimumSlots;
                ring.Slots = Enumerable.Range(0, minimumSlots).Select(RingSlotDefinition.Empty).ToList();
                Warn(path, "rings.limit", "Ring graph exceeded its safety limit; remaining submenus were reset.");
                return;
            }

            ring.SlotCount = Clamp(ring.SlotCount, minimumSlots, maximumSlots, path + ".slotCount");
            ring.Slots ??= [];

            if (ring.Slots.Count > ring.SlotCount)
            {
                ring.Slots.RemoveRange(ring.SlotCount, ring.Slots.Count - ring.SlotCount);
                Warn(path + ".slots", "slots.truncated", "Extra slots were removed to match slotCount.");
            }

            while (ring.Slots.Count < ring.SlotCount)
            {
                ring.Slots.Add(RingSlotDefinition.Empty(ring.Slots.Count));
                Warn(path + ".slots", "slots.filled", "A missing slot was filled with an empty action.");
            }

            for (var slotIndex = 0; slotIndex < ring.Slots.Count; slotIndex++)
            {
                var slotPath = $"{path}.slots[{slotIndex}]";
                var slot = ring.Slots[slotIndex];
                if (slot is null)
                {
                    slot = RingSlotDefinition.Empty(slotIndex);
                    ring.Slots[slotIndex] = slot;
                    Warn(slotPath, "slot.defaulted", "A null slot was replaced.");
                }

                slot.Id = EnsureId(slot.Id, "slot", "slot", slotPath + ".id", normalizationContext.SlotIds);
                slot.Icon = CleanOptionalText(slot.Icon, 1024, slotPath + ".icon");
                if (slot.AppearanceOverride is { } appearanceOverride)
                {
                    appearanceOverride.BubbleColor = NormalizeOptionalColor(
                        appearanceOverride.BubbleColor,
                        slotPath + ".appearanceOverride.bubbleColor");
                    appearanceOverride.BubbleHoverColor = NormalizeOptionalColor(
                        appearanceOverride.BubbleHoverColor,
                        slotPath + ".appearanceOverride.bubbleHoverColor");
                    appearanceOverride.IconColor = NormalizeOptionalColor(
                        appearanceOverride.IconColor,
                        slotPath + ".appearanceOverride.iconColor");
                    appearanceOverride.IconHoverColor = NormalizeOptionalColor(
                        appearanceOverride.IconHoverColor,
                        slotPath + ".appearanceOverride.iconHoverColor");
                    if (appearanceOverride.IsEmpty)
                    {
                        slot.AppearanceOverride = null;
                    }
                }

                if (slot.Submenu is not null)
                {
                    if (depth >= MaximumSubmenuDepth)
                    {
                        slot.Submenu = null;
                        slot.Action = ActionDefinition.None();
                        Warn(slotPath + ".submenu", "submenu.depth", "Submenu beyond the maximum nesting depth was removed.");
                    }
                    else
                    {
                        slot.Action = null;
                        NormalizeRing(slot.Submenu, slotPath + ".submenu", depth + 1, normalizationContext);
                    }
                }
                else
                {
                    slot.Action ??= ActionDefinition.None();
                    NormalizeAction(slot.Action, slotPath + ".action", normalizationContext, 0);
                }

                var fallbackLabel = slot.Submenu?.Name ?? slot.Action?.Name ?? "Добавить действие";
                slot.Label = CleanText(slot.Label, fallbackLabel, 80, slotPath + ".label");
            }
        }

        void NormalizeRingStyle(RingStyleDefinition style, string path)
        {
            if (!Enum.IsDefined(style.Preset))
            {
                style.Preset = RingStylePreset.Inherit;
                Warn(path + ".preset", "style.preset.defaulted", "Unknown ring style preset was replaced.");
            }

            style.BubbleColor = NormalizeOptionalColor(style.BubbleColor, path + ".bubbleColor");
            style.IconColor = NormalizeOptionalColor(style.IconColor, path + ".iconColor");
            style.HoverColor = NormalizeOptionalColor(style.HoverColor, path + ".hoverColor");
        }

        void NormalizeRingAppearance(RingAppearanceDefinition appearance, string path)
        {
            appearance.BubbleColor = NormalizeRequiredColor(
                appearance.BubbleColor,
                RingAppearanceDefinition.DefaultBubbleColor,
                path + ".bubbleColor");
            appearance.BubbleHoverColor = NormalizeRequiredColor(
                appearance.BubbleHoverColor,
                RingAppearanceDefinition.DefaultBubbleHoverColor,
                path + ".bubbleHoverColor");
            appearance.IconColor = NormalizeRequiredColor(
                appearance.IconColor,
                RingAppearanceDefinition.DefaultIconColor,
                path + ".iconColor");
            appearance.IconHoverColor = NormalizeRequiredColor(
                appearance.IconHoverColor,
                RingAppearanceDefinition.DefaultIconHoverColor,
                path + ".iconHoverColor");
        }

        void NormalizeAction(
            ActionDefinition action,
            string path,
            NormalizationContext normalizationContext,
            int sequenceDepth)
        {
            action.Id = EnsureId(action.Id, "action", "action", path + ".id", normalizationContext.ActionIds);
            normalizationContext.ActionNodes++;
            if (normalizationContext.ActionNodes > MaximumActionNodes)
            {
                MakeNoAction(action);
                Warn(path, "actions.limit", "Action graph exceeded its safety limit; remaining actions were disabled.");
                return;
            }

            action.Name = CleanText(action.Name, "Нет действия", 80, path + ".name");
            action.Description = CleanOptionalText(action.Description, 500, path + ".description");
            action.Icon = CleanOptionalText(action.Icon, 1024, path + ".icon");

            if (!Enum.IsDefined(action.Kind))
            {
                action.Kind = ActionKind.None;
                Warn(path + ".kind", "action.kind.defaulted", "Unknown action kind was replaced.");
            }

            switch (action.Kind)
            {
                case ActionKind.None:
                    break;

                case ActionKind.KeyboardShortcut:
                    action.KeyboardShortcut ??= new KeyboardShortcutAction();
                    action.KeyboardShortcut.Chords ??= [];
                    action.KeyboardShortcut.Chords.RemoveAll(chord => chord is null);
                    if (action.KeyboardShortcut.Chords.Count > MaximumShortcutChords)
                    {
                        action.KeyboardShortcut.Chords.RemoveRange(
                            MaximumShortcutChords,
                            action.KeyboardShortcut.Chords.Count - MaximumShortcutChords);
                        Warn(path + ".keyboardShortcut.chords", "shortcut.truncated", "Extra shortcut chords were removed.");
                    }

                    for (var chordIndex = action.KeyboardShortcut.Chords.Count - 1;
                         chordIndex >= 0;
                         chordIndex--)
                    {
                        var chord = action.KeyboardShortcut.Chords[chordIndex];
                        var chordPath = $"{path}.keyboardShortcut.chords[{chordIndex}]";
                        NormalizeChord(chord, chordPath);
                        if (!KeyNames.IsSupported(chord.Key))
                        {
                            action.KeyboardShortcut.Chords.RemoveAt(chordIndex);
                            Warn(
                                chordPath + ".key",
                                "shortcut.key.removed",
                                "An unsupported shortcut key was removed.");
                        }
                    }

                    if (action.KeyboardShortcut.Chords.Count == 0)
                    {
                        MakeNoAction(action);
                        Warn(path, "shortcut.empty", "An empty keyboard shortcut was converted to no action.");
                    }

                    break;

                case ActionKind.LaunchApplication:
                    action.LaunchApplication ??= new LaunchApplicationAction();
                    action.LaunchApplication.ExecutablePath = CleanText(
                        action.LaunchApplication.ExecutablePath,
                        string.Empty,
                        4096,
                        path + ".launchApplication.executablePath");
                    action.LaunchApplication.Arguments = CleanOptionalText(
                        action.LaunchApplication.Arguments,
                        8192,
                        path + ".launchApplication.arguments");
                    action.LaunchApplication.WorkingDirectory = CleanOptionalText(
                        action.LaunchApplication.WorkingDirectory,
                        4096,
                        path + ".launchApplication.workingDirectory");
                    if (action.LaunchApplication.ExecutablePath.Length == 0)
                    {
                        MakeNoAction(action);
                        Warn(path, "launch.path.empty", "A launch action without a path was converted to no action.");
                    }

                    break;

                case ActionKind.OpenUri:
                    action.OpenUri ??= new OpenUriAction();
                    action.OpenUri.Uri = CleanText(action.OpenUri.Uri, string.Empty, 4096, path + ".openUri.uri");
                    if (!Uri.TryCreate(action.OpenUri.Uri, UriKind.Absolute, out _))
                    {
                        MakeNoAction(action);
                        Warn(path, "uri.invalid", "An invalid URI action was converted to no action.");
                    }

                    break;

                case ActionKind.TypeText:
                    action.TypeText ??= new TypeTextAction();
                    action.TypeText.Text = Truncate(action.TypeText.Text ?? string.Empty, 32_768, path + ".typeText.text");
                    action.TypeText.CharacterDelayMilliseconds = Clamp(
                        action.TypeText.CharacterDelayMilliseconds,
                        0,
                        5000,
                        path + ".typeText.characterDelayMilliseconds");
                    break;

                case ActionKind.BuiltIn:
                    action.BuiltIn ??= new BuiltInAction();
                    if (!Enum.IsDefined(action.BuiltIn.Command) || action.BuiltIn.Command == BuiltInCommand.None)
                    {
                        MakeNoAction(action);
                        Warn(path, "builtin.invalid", "An invalid built-in action was converted to no action.");
                    }

                    break;

                case ActionKind.MouseInput:
                    action.MouseInput ??= new MouseInputAction();
                    if (!Enum.IsDefined(action.MouseInput.Button))
                    {
                        action.MouseInput.Button = MouseButton.Left;
                        Warn(path + ".mouseInput.button", "mouse.button.defaulted", "Unknown mouse button was replaced.");
                    }

                    action.MouseInput.ClickCount = Clamp(action.MouseInput.ClickCount, 1, 3, path + ".mouseInput.clickCount");
                    action.MouseInput.WheelDelta = Clamp(action.MouseInput.WheelDelta, 1, 1200, path + ".mouseInput.wheelDelta");
                    break;

                case ActionKind.Sequence:
                    if (sequenceDepth >= MaximumSequenceDepth)
                    {
                        MakeNoAction(action);
                        Warn(path, "sequence.depth", "A sequence beyond the maximum nesting depth was converted to no action.");
                        break;
                    }

                    action.Sequence ??= new SequenceAction();
                    action.Sequence.Steps ??= [];
                    for (var index = action.Sequence.Steps.Count - 1; index >= 0; index--)
                    {
                        if (action.Sequence.Steps[index] is null)
                        {
                            action.Sequence.Steps.RemoveAt(index);
                            Warn($"{path}.sequence.steps[{index}]", "sequence.step.removed", "A null sequence step was removed.");
                        }
                    }

                    if (action.Sequence.Steps.Count > MaximumSequenceSteps)
                    {
                        action.Sequence.Steps.RemoveRange(
                            MaximumSequenceSteps,
                            action.Sequence.Steps.Count - MaximumSequenceSteps);
                        Warn(path + ".sequence.steps", "sequence.truncated", $"Only the first {MaximumSequenceSteps} steps were retained.");
                    }

                    for (var index = 0; index < action.Sequence.Steps.Count; index++)
                    {
                        var step = action.Sequence.Steps[index];
                        var stepPath = $"{path}.sequence.steps[{index}]";
                        step.Id = EnsureId(step.Id, "step", "step", stepPath + ".id", normalizationContext.StepIds);
                        step.DelayAfterMilliseconds = Clamp(
                            step.DelayAfterMilliseconds,
                            0,
                            60_000,
                            stepPath + ".delayAfterMilliseconds");
                        step.Action ??= ActionDefinition.None();
                        NormalizeAction(step.Action, stepPath + ".action", normalizationContext, sequenceDepth + 1);
                    }

                    if (action.Sequence.Steps.Count == 0)
                    {
                        MakeNoAction(action);
                        Warn(path, "sequence.empty", "An empty sequence was converted to no action.");
                    }

                    break;

                case ActionKind.AdjustParameter:
                    action.AdjustParameter ??= new AdjustParameterAction();
                    if (!Enum.IsDefined(action.AdjustParameter.Parameter))
                    {
                        action.AdjustParameter.Parameter = AdjustableParameter.SystemVolume;
                        Warn(path + ".adjustParameter.parameter", "adjustment.parameter.defaulted", "Unknown parameter was replaced.");
                    }

                    if (!Enum.IsDefined(action.AdjustParameter.Mode))
                    {
                        action.AdjustParameter.Mode = AdjustmentMode.Relative;
                        Warn(path + ".adjustParameter.mode", "adjustment.mode.defaulted", "Unknown adjustment mode was replaced.");
                    }

                    if (!double.IsFinite(action.AdjustParameter.Value))
                    {
                        action.AdjustParameter.Value = 5;
                        Warn(path + ".adjustParameter.value", "adjustment.value.defaulted", "A non-finite value was replaced.");
                    }
                    else
                    {
                        action.AdjustParameter.Value = Math.Clamp(action.AdjustParameter.Value, -100_000, 100_000);
                    }

                    action.AdjustParameter.CustomParameterId = CleanOptionalText(
                        action.AdjustParameter.CustomParameterId,
                        120,
                        path + ".adjustParameter.customParameterId");

                    if (action.AdjustParameter.Parameter == AdjustableParameter.Custom
                        && action.AdjustParameter.CustomParameterId is null)
                    {
                        MakeNoAction(action);
                        Warn(path, "adjustment.customId.empty", "A custom adjustment without an identifier was converted to no action.");
                    }

                    break;
            }

            ClearInactivePayloads(action);
        }

        void NormalizeChord(KeyChord chord, string path)
        {
            chord.Key = KeyNames.Normalize(chord.Key);
            if (chord.Key.Length == 0)
            {
                chord.Key = "Space";
                Warn(path + ".key", "key.defaulted", "An empty key was replaced with Space.");
            }
            else if (chord.Key.Length > 64)
            {
                chord.Key = chord.Key[..64];
                Warn(path + ".key", "key.truncated", "A key name was truncated.");
            }

            var normalizedModifiers = chord.Modifiers & AllModifiers;
            if (chord.Modifiers != normalizedModifiers)
            {
                chord.Modifiers = normalizedModifiers;
                Warn(path + ".modifiers", "modifiers.normalized", "Unknown modifier flags were removed.");
            }
        }

        string EnsureId(
            string? current,
            string fallback,
            string prefix,
            string path,
            HashSet<string> used)
        {
            var candidate = string.IsNullOrWhiteSpace(current) ? fallback : current.Trim();
            if (candidate.Length > 120)
            {
                candidate = candidate[..120];
                Warn(path, "id.truncated", "An identifier was truncated.");
            }

            if (candidate.Length == 0 || !used.Add(candidate))
            {
                do
                {
                    candidate = ConfigurationIds.New(prefix);
                }
                while (!used.Add(candidate));

                Warn(path, "id.regenerated", "A missing or duplicate identifier was regenerated.");
            }

            return candidate;
        }

        string CleanText(string? value, string fallback, int maximumLength, string path)
        {
            var cleaned = value?.Trim() ?? string.Empty;
            if (cleaned.Length == 0)
            {
                if (fallback.Length > 0)
                {
                    Warn(path, "text.defaulted", "An empty value was replaced.");
                }

                return fallback;
            }

            return Truncate(cleaned, maximumLength, path);
        }

        string? CleanOptionalText(string? value, int maximumLength, string path)
        {
            var cleaned = value?.Trim();
            return string.IsNullOrEmpty(cleaned) ? null : Truncate(cleaned, maximumLength, path);
        }

        string? NormalizeOptionalColor(string? value, string path)
        {
            var cleaned = CleanOptionalText(value, 9, path);
            if (cleaned is null)
            {
                return null;
            }

            if (IsColor(cleaned))
            {
                return cleaned.ToUpperInvariant();
            }

            Warn(path, "style.color.removed", "Invalid optional color was removed.");
            return null;
        }

        string NormalizeRequiredColor(string? value, string fallback, string path)
        {
            var cleaned = CleanOptionalText(value, 9, path);
            if (cleaned is not null && IsColor(cleaned))
            {
                return cleaned.ToUpperInvariant();
            }

            Warn(path, "style.color.defaulted", "Invalid color was replaced with its default.");
            return fallback;
        }

        string Truncate(string value, int maximumLength, string path)
        {
            if (value.Length <= maximumLength)
            {
                return value;
            }

            Warn(path, "text.truncated", $"Value was truncated to {maximumLength} characters.");
            return value[..maximumLength];
        }

        int Clamp(int value, int minimum, int maximum, string path)
        {
            var clamped = Math.Clamp(value, minimum, maximum);
            if (clamped != value)
            {
                Warn(path, "number.clamped", $"Value was clamped to the range {minimum}..{maximum}.");
            }

            return clamped;
        }

        void Warn(string path, string code, string message) =>
            issues.Add(new ConfigurationIssue(path, code, message, ConfigurationIssueSeverity.Warning));
    }

    private static bool IsColor(string? value)
    {
        if (value is null || value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        return value.AsSpan(1).ToString().All(Uri.IsHexDigit);
    }

    private static void MakeNoAction(ActionDefinition action)
    {
        action.Kind = ActionKind.None;
        ClearInactivePayloads(action);
    }

    private static void ClearInactivePayloads(ActionDefinition action)
    {
        if (action.Kind != ActionKind.KeyboardShortcut)
        {
            action.KeyboardShortcut = null;
        }

        if (action.Kind != ActionKind.LaunchApplication)
        {
            action.LaunchApplication = null;
        }

        if (action.Kind != ActionKind.OpenUri)
        {
            action.OpenUri = null;
        }

        if (action.Kind != ActionKind.TypeText)
        {
            action.TypeText = null;
        }

        if (action.Kind != ActionKind.BuiltIn)
        {
            action.BuiltIn = null;
        }

        if (action.Kind != ActionKind.MouseInput)
        {
            action.MouseInput = null;
        }

        if (action.Kind != ActionKind.Sequence)
        {
            action.Sequence = null;
        }

        if (action.Kind != ActionKind.AdjustParameter)
        {
            action.AdjustParameter = null;
        }
    }

    private sealed class NormalizationContext
    {
        public HashSet<string> UserProfileIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ProfileIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> RingIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SlotIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ActionIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> StepIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ActionNodes { get; set; }

        public int RingNodes { get; set; }
    }
}
