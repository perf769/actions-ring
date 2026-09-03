using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;

namespace ActionsRing.Core.Configuration;

public enum ConfigurationIssueSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ConfigurationIssue(
    string Path,
    string Code,
    string Message,
    ConfigurationIssueSeverity Severity);

public sealed record ConfigurationNormalizationResult(
    ActionsRingConfiguration Configuration,
    IReadOnlyList<ConfigurationIssue> Issues);

public sealed record ConfigurationValidationResult(IReadOnlyList<ConfigurationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != ConfigurationIssueSeverity.Error);
}

/// <summary>Checks invariants expected by the presentation and Windows integration layers.</summary>
public static class ConfigurationValidator
{
    public static ConfigurationValidationResult Validate(ActionsRingConfiguration? configuration)
    {
        var issues = new List<ConfigurationIssue>();
        if (configuration is null)
        {
            Add("$", "configuration.null", "Configuration cannot be null.");
            return new ConfigurationValidationResult(issues);
        }

        if (configuration.SchemaVersion != ConfigurationSchema.CurrentVersion)
        {
            Add("$.schemaVersion", "schema.unsupported", "Configuration schema is not current.");
        }

        if (configuration.Trigger is null)
        {
            Add("$.trigger", "trigger.missing", "A trigger binding is required.");
        }
        else if (configuration.Trigger.Kind == InputBindingKind.Keyboard
                 && string.IsNullOrWhiteSpace(configuration.Trigger.Keyboard?.Key))
        {
            Add("$.trigger.keyboard.key", "trigger.key.missing", "A keyboard trigger requires a key.");
        }
        else if (configuration.Trigger.Kind == InputBindingKind.Keyboard
                 && !KeyNames.IsSupported(configuration.Trigger.Keyboard?.Key))
        {
            Add("$.trigger.keyboard.key", "trigger.key.unsupported", "Keyboard trigger key is not supported.");
        }
        else if (configuration.Trigger.Kind == InputBindingKind.MouseButton
                 && configuration.Trigger.Button is null)
        {
            Add("$.trigger.button", "trigger.button.missing", "A mouse trigger requires a button.");
        }
        else if (!Enum.IsDefined(configuration.Trigger.Kind)
                 || !Enum.IsDefined(configuration.Trigger.ActivationMode)
                 || (configuration.Trigger.Button is { } button && !Enum.IsDefined(button))
                 || (configuration.Trigger.MouseModifiers & ~(
                         KeyboardModifiers.Control |
                         KeyboardModifiers.Alt |
                         KeyboardModifiers.Shift |
                         KeyboardModifiers.Windows)) != 0)
        {
            Add("$.trigger", "trigger.enum", "Trigger contains an unknown enum value.");
        }
        else
        {
            var modifiers = configuration.Trigger.Kind == InputBindingKind.Keyboard
                ? configuration.Trigger.Keyboard?.Modifiers ?? KeyboardModifiers.None
                : configuration.Trigger.MouseModifiers;
            if ((modifiers & (KeyboardModifiers.Alt | KeyboardModifiers.Windows)) != 0)
            {
                Add(
                    "$.trigger",
                    "trigger.modifiers.reserved",
                    "Alt and Windows modifiers cannot be used for the activation trigger.");
            }
        }

        if (configuration.Preferences is null)
        {
            Add("$.preferences", "preferences.missing", "Preferences are required.");
        }
        else if (configuration.Preferences.Updates is null)
        {
            Add("$.preferences.updates", "updates.missing", "Update preferences are required.");
        }
        else
        {
            var updates = configuration.Preferences.Updates;
            if (updates.SkippedVersion is not null
                && !UpdateVersionText.IsValid(updates.SkippedVersion))
            {
                Add(
                    "$.preferences.updates.skippedVersion",
                    "updates.skippedVersion.invalid",
                    "Skipped update version must be a semantic version.");
            }

            if (updates.LastCheckedAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            {
                Add(
                    "$.preferences.updates.lastCheckedAtUtc",
                    "updates.lastCheckedAtUtc.notUtc",
                    "Last update check time must use UTC.");
            }

            if (!updates.CheckAutomatically && updates.DownloadAutomatically)
            {
                Add(
                    "$.preferences.updates.downloadAutomatically",
                    "updates.autoDownload.requiresCheck",
                    "Automatic downloads require automatic update checks.");
            }
        }

        if (configuration.UserProfiles is not { Count: > 0 })
        {
            Add("$.userProfiles", "userProfiles.missing", "At least one user profile is required.");
        }
        else
        {
            if (!configuration.UserProfiles.Any(profile => string.Equals(
                    profile?.Id,
                    configuration.ActiveUserProfileId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                Add("$.activeUserProfileId", "userProfile.selection.invalid", "The selected user profile does not exist.");
            }

            for (var userIndex = 0; userIndex < configuration.UserProfiles.Count; userIndex++)
            {
                var userProfile = configuration.UserProfiles[userIndex];
                var userPath = $"$.userProfiles[{userIndex}]";
                if (userProfile is null)
                {
                    Add(userPath, "userProfile.null", "User profile cannot be null.");
                    continue;
                }

                if (userProfile.GlobalProfile is null)
                {
                    Add(userPath + ".globalProfile", "profile.global.missing", "A global fallback profile is required.");
                }
                else
                {
                    ValidateStyle(userProfile.GlobalProfile.Style, userPath + ".globalProfile.style");
                    ValidateRing(userProfile.GlobalProfile.RootRing, userPath + ".globalProfile.rootRing", 0);
                }

                if (userProfile.ApplicationProfiles is null)
                {
                    Add(userPath + ".applicationProfiles", "profiles.missing", "Application profiles collection is required.");
                    continue;
                }

                for (var index = 0; index < userProfile.ApplicationProfiles.Count; index++)
                {
                    var profile = userProfile.ApplicationProfiles[index];
                    var profilePath = $"{userPath}.applicationProfiles[{index}]";
                    if (profile is null)
                    {
                        Add(profilePath, "profile.null", "Profile cannot be null.");
                        continue;
                    }

                    if (profile.IsEnabled && (profile.MatchRules is null || profile.MatchRules.Count == 0))
                    {
                        Add(
                            profilePath + ".matchRules",
                            "profile.rules.empty",
                            "An application profile must contain at least one match rule.");
                    }

                    ValidateStyle(profile.Style, profilePath + ".style");
                    ValidateRing(profile.RootRing, profilePath + ".rootRing", 0);
                }
            }
        }

        void ValidateStyle(RingStyleDefinition? style, string path)
        {
            if (style is null || !Enum.IsDefined(style.Preset))
            {
                Add(path, "style.invalid", "Profile ring style is missing or invalid.");
                return;
            }

            ValidateOptionalColor(style.BubbleColor, path + ".bubbleColor");
            ValidateOptionalColor(style.IconColor, path + ".iconColor");
            ValidateOptionalColor(style.HoverColor, path + ".hoverColor");
        }

        void ValidateOptionalColor(string? value, string path)
        {
            if (value is not null && !IsColor(value))
            {
                Add(path, "style.color.invalid", "Color must use #RRGGBB or #AARRGGBB format.");
            }
        }

        return new ConfigurationValidationResult(issues);

        void ValidateRing(RingDefinition? ring, string path, int depth)
        {
            if (ring is null)
            {
                Add(path, "ring.missing", "Ring is required.");
                return;
            }

            if (depth > ConfigurationNormalizer.MaximumSubmenuDepth)
            {
                Add(path, "ring.depth", "Submenu nesting exceeds the supported depth.");
                return;
            }

            if (ring.Appearance is null)
            {
                Add(path + ".appearance", "ring.appearance.missing", "Ring appearance is required.");
            }
            else
            {
                ValidateRequiredColor(ring.Appearance.BubbleColor, path + ".appearance.bubbleColor");
                ValidateRequiredColor(ring.Appearance.BubbleHoverColor, path + ".appearance.bubbleHoverColor");
                ValidateRequiredColor(ring.Appearance.IconColor, path + ".appearance.iconColor");
                ValidateRequiredColor(ring.Appearance.IconHoverColor, path + ".appearance.iconHoverColor");
            }

            var minimumSlots = depth == 0
                ? RingDefinition.MinimumRootSlots
                : RingDefinition.MinimumSubmenuSlots;
            var maximumSlots = depth == 0
                ? RingDefinition.MaximumRootSlots
                : RingDefinition.MaximumSubmenuSlots;

            if (ring.SlotCount < minimumSlots || ring.SlotCount > maximumSlots)
            {
                Add(
                    path + ".slotCount",
                    "ring.slotCount.range",
                    depth == 0
                        ? "Root ring must contain four to eight slots."
                        : "Submenu must contain one to nine slots.");
            }

            if (ring.Slots is null || ring.Slots.Count != ring.SlotCount)
            {
                Add(path + ".slots", "ring.slots.count", "Slots count must equal slotCount.");
                return;
            }

            for (var index = 0; index < ring.Slots.Count; index++)
            {
                var slot = ring.Slots[index];
                var slotPath = $"{path}.slots[{index}]";
                if (slot is null)
                {
                    Add(slotPath, "slot.null", "Slot cannot be null.");
                    continue;
                }

                if ((slot.Action is null) == (slot.Submenu is null))
                {
                    Add(slotPath, "slot.target", "Slot must have exactly one action or submenu target.");
                }


                if (slot.AppearanceOverride is { } appearanceOverride)
                {
                    ValidateOptionalColor(appearanceOverride.BubbleColor, slotPath + ".appearanceOverride.bubbleColor");
                    ValidateOptionalColor(appearanceOverride.BubbleHoverColor, slotPath + ".appearanceOverride.bubbleHoverColor");
                    ValidateOptionalColor(appearanceOverride.IconColor, slotPath + ".appearanceOverride.iconColor");
                    ValidateOptionalColor(appearanceOverride.IconHoverColor, slotPath + ".appearanceOverride.iconHoverColor");
                }

                if (slot.Submenu is not null)
                {
                    ValidateRing(slot.Submenu, slotPath + ".submenu", depth + 1);
                }
                else if (slot.Action is not null)
                {
                    ValidateAction(slot.Action, slotPath + ".action", 0);
                }
            }
        }

        void ValidateAction(ActionDefinition action, string path, int depth)
        {
            if (action.Kind == ActionKind.Sequence
                && depth >= ConfigurationNormalizer.MaximumSequenceDepth)
            {
                Add(path, "sequence.depth", "Action sequence nesting exceeds the supported depth.");
                return;
            }

            var matchingPayload = action.Kind switch
            {
                ActionKind.None => true,
                ActionKind.KeyboardShortcut => action.KeyboardShortcut?.Chords.Count > 0,
                ActionKind.LaunchApplication => !string.IsNullOrWhiteSpace(action.LaunchApplication?.ExecutablePath),
                ActionKind.OpenUri => Uri.TryCreate(action.OpenUri?.Uri, UriKind.Absolute, out _),
                ActionKind.TypeText => action.TypeText is not null,
                ActionKind.BuiltIn => action.BuiltIn is not null && action.BuiltIn.Command != BuiltInCommand.None,
                ActionKind.MouseInput => action.MouseInput is not null,
                ActionKind.Sequence => action.Sequence?.Steps.Count > 0,
                ActionKind.AdjustParameter => action.AdjustParameter is not null,
                _ => false,
            };

            if (!matchingPayload)
            {
                Add(path, "action.payload", "Action is missing the payload required by its kind.");
                return;
            }

            if (action.Kind == ActionKind.Sequence && action.Sequence is not null)
            {
                for (var index = 0; index < action.Sequence.Steps.Count; index++)
                {
                    var step = action.Sequence.Steps[index];
                    if (step?.Action is null)
                    {
                        Add($"{path}.sequence.steps[{index}]", "sequence.step", "Sequence step requires an action.");
                    }
                    else
                    {
                        ValidateAction(step.Action, $"{path}.sequence.steps[{index}].action", depth + 1);
                    }
                }
            }

            if (action.Kind == ActionKind.KeyboardShortcut && action.KeyboardShortcut is not null)
            {
                for (var index = 0; index < action.KeyboardShortcut.Chords.Count; index++)
                {
                    if (!KeyNames.IsSupported(action.KeyboardShortcut.Chords[index].Key))
                    {
                        Add(
                            $"{path}.keyboardShortcut.chords[{index}].key",
                            "shortcut.key.unsupported",
                            "Keyboard shortcut key is not supported.");
                    }
                }
            }

            if (action.Kind == ActionKind.AdjustParameter
                && action.AdjustParameter is { } adjustment
                && !double.IsFinite(adjustment.Value))
            {
                Add(path + ".adjustParameter.value", "adjustment.value.finite", "Adjustment value must be finite.");
            }
        }

        void Add(string path, string code, string message) =>
            issues.Add(new ConfigurationIssue(path, code, message, ConfigurationIssueSeverity.Error));

        void ValidateRequiredColor(string? value, string path)
        {
            if (!IsColor(value))
            {
                Add(path, "style.color.invalid", "Color must use #RRGGBB or #AARRGGBB format.");
            }
        }
    }

    private static bool IsColor(string? value)
    {
        if (value is null || value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        return value.AsSpan(1).ToString().All(Uri.IsHexDigit);
    }
}
