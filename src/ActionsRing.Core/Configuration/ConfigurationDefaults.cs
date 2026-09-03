using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;

namespace ActionsRing.Core.Configuration;

/// <summary>Factory for a complete, immediately usable first-run configuration.</summary>
public static class ConfigurationDefaults
{
    public static ActionsRingConfiguration Create() => new()
    {
        SchemaVersion = ConfigurationSchema.CurrentVersion,
        Trigger = new TriggerBinding
        {
            Kind = InputBindingKind.MouseButton,
            Button = MouseButton.XButton2,
            ActivationMode = ActivationMode.Hold,
        },
        ActiveUserProfileId = "user-default",
        UserProfiles = [CreateDefaultUserProfile()],
        Preferences = new UserPreferences(),
        Onboarding = new OnboardingState(),
    };

    public static UserProfile CreateDefaultUserProfile() => new()
    {
        Id = "user-default",
        Name = "Основной",
        GlobalProfile = new RingProfile
        {
            Id = "global",
            Name = "Все приложения",
            IsEnabled = true,
            Style = new RingStyleDefinition { Preset = RingStylePreset.Purple },
            RootRing = CreateDefaultRing(),
        },
        ApplicationProfiles = [],
    };

    public static RingDefinition CreateDefaultRing()
    {
        var actions = new[]
        {
            ActionDefinition.BuiltInCommand("Копировать", BuiltInCommand.Copy, "copy"),
            ActionDefinition.BuiltInCommand("Вставить", BuiltInCommand.Paste, "paste"),
            ActionDefinition.BuiltInCommand("Отменить", BuiltInCommand.Undo, "undo"),
            ActionDefinition.BuiltInCommand("Повторить", BuiltInCommand.Redo, "redo"),
            ActionDefinition.BuiltInCommand("Представление задач", BuiltInCommand.TaskView, "window"),
            ActionDefinition.BuiltInCommand("Проводник", BuiltInCommand.OpenFileExplorer, "folder"),
            ActionDefinition.BuiltInCommand("Параметры Windows", BuiltInCommand.OpenSettings, "settings"),
            ActionDefinition.BuiltInCommand("Заблокировать", BuiltInCommand.LockWorkstation, "lock"),
        };

        return new RingDefinition
        {
            Id = "global-root",
            Name = "Основное кольцо",
            SlotCount = actions.Length,
            Slots = actions.Select((action, _) => RingSlotDefinition.ForAction(action)).ToList(),
        };
    }
}
