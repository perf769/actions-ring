using System.Text.Json.Nodes;
using ActionsRing.Core.Domain;

namespace ActionsRing.Core.Configuration;

public sealed class UnsupportedConfigurationVersionException : Exception
{
    public UnsupportedConfigurationVersionException(int version)
        : base($"Configuration schema version {version} is not supported by this application.")
    {
        Version = version;
    }

    public int Version { get; }
}

public sealed record ConfigurationMigrationResult(
    JsonObject Document,
    int SourceVersion,
    int TargetVersion,
    bool WasMigrated);

/// <summary>Applies deterministic, sequential migrations to configuration JSON.</summary>
public static class ConfigurationMigrator
{
    public static ConfigurationMigrationResult Migrate(JsonNode? rootNode)
    {
        if (rootNode is not JsonObject root)
        {
            throw new InvalidDataException("Configuration root must be a JSON object.");
        }

        var hadVersionMarker = root.TryGetPropertyValue("schemaVersion", out var versionNode)
                               && versionNode is not null;
        var sourceVersion = ReadVersion(root);
        if (sourceVersion < ConfigurationSchema.OldestSupportedVersion
            || sourceVersion > ConfigurationSchema.CurrentVersion)
        {
            throw new UnsupportedConfigurationVersionException(sourceVersion);
        }

        if (!hadVersionMarker)
        {
            root["schemaVersion"] = sourceVersion;
        }

        var version = sourceVersion;
        while (version < ConfigurationSchema.CurrentVersion)
        {
            version = version switch
            {
                1 => MigrateVersion1To2(root),
                2 => MigrateVersion2To3(root),
                3 => MigrateVersion3To4(root),
                4 => MigrateVersion4To5(root),
                _ => throw new UnsupportedConfigurationVersionException(version),
            };
        }

        return new ConfigurationMigrationResult(
            root,
            sourceVersion,
            version,
            WasMigrated: !hadVersionMarker || sourceVersion != version);
    }

    private static int ReadVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("schemaVersion", out var node) || node is null)
        {
            // The original public preview did not write a version marker. Infer newer
            // unversioned documents by shape so removing only the marker from an export
            // cannot discard its user-profile graph during the sequential migrations.
            if (root["userProfiles"] is JsonArray
                && root["preferences"] is JsonObject currentPreferences
                && currentPreferences["updates"] is JsonObject)
            {
                return 4;
            }

            if (root["userProfiles"] is JsonArray)
            {
                return 3;
            }

            if ((root["globalProfile"] is JsonObject || root["applicationProfiles"] is JsonArray)
                && root["preferences"] is JsonObject preferences
                && preferences["general"] is JsonObject)
            {
                return 2;
            }

            return ConfigurationSchema.OldestSupportedVersion;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var version))
        {
            return version;
        }

        throw new InvalidDataException("schemaVersion must be an integer.");
    }

    private static int MigrateVersion1To2(JsonObject root)
    {
        // v1 stored general preferences directly under preferences.
        if (root["preferences"] is JsonObject preferences)
        {
            var general = preferences["general"] as JsonObject ?? new JsonObject();
            MoveIfPresent(preferences, general, "runAtStartup");
            MoveIfPresent(preferences, general, "startMinimized");
            MoveIfPresent(preferences, general, "closeToTray");
            MoveIfPresent(preferences, general, "showTrayIcon");
            MoveIfPresent(preferences, general, "checkForUpdates");
            MoveIfPresent(preferences, general, "culture");
            preferences["general"] = general;
        }

        // An early build called this property activationBehavior.
        if (root["trigger"] is JsonObject trigger
            && trigger["activationMode"] is null
            && trigger["activationBehavior"] is JsonNode legacyMode)
        {
            trigger["activationMode"] = legacyMode.DeepClone();
            trigger.Remove("activationBehavior");
        }

        root["schemaVersion"] = 2;
        return 2;
    }

    private static int MigrateVersion2To3(JsonObject root)
    {
        var globalProfile = root["globalProfile"]?.DeepClone() ?? new JsonObject
        {
            ["id"] = "global",
            ["name"] = "Все приложения",
            ["isEnabled"] = true,
        };
        var applicationProfiles = root["applicationProfiles"]?.DeepClone() ?? new JsonArray();

        if (globalProfile is JsonObject globalProfileObject)
        {
            PromoteLegacyPalette(globalProfileObject);
        }

        if (applicationProfiles is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                PromoteLegacyPalette(profile);
            }
        }

        root["activeUserProfileId"] = "user-default";
        root["userProfiles"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "user-default",
                ["name"] = "Основной",
                ["globalProfile"] = globalProfile,
                ["applicationProfiles"] = applicationProfiles,
            },
        };
        root.Remove("globalProfile");
        root.Remove("applicationProfiles");
        root["schemaVersion"] = 3;
        return 3;
    }

    private static int MigrateVersion3To4(JsonObject root)
    {
        var preferences = root["preferences"] as JsonObject ?? new JsonObject();
        var general = preferences["general"] as JsonObject ?? new JsonObject();
        var updates = preferences["updates"] as JsonObject ?? new JsonObject();

        if (updates["checkAutomatically"] is null)
        {
            updates["checkAutomatically"] = general["checkForUpdates"]?.DeepClone()
                                            ?? JsonValue.Create(true);
        }
        if (updates["downloadAutomatically"] is null)
        {
            updates["downloadAutomatically"] = false;
        }

        general.Remove("checkForUpdates");
        preferences["general"] = general;
        preferences["updates"] = updates;
        root["preferences"] = preferences;
        root["schemaVersion"] = 4;
        return 4;
    }

    private static int MigrateVersion4To5(JsonObject root)
    {
        // Both targets now have independent behavior: click invokes the action, hover opens
        // the submenu. Preserve the complete legacy slot graph, including either target.
        if (root["userProfiles"] is JsonArray userProfiles)
        {
            foreach (var userProfile in userProfiles.OfType<JsonObject>())
            {
                UpgradeLegacyPhotoshopIcons((userProfile["globalProfile"] as JsonObject)?["rootRing"] as JsonObject);
                if (userProfile["applicationProfiles"] is JsonArray applicationProfiles)
                {
                    foreach (var profile in applicationProfiles.OfType<JsonObject>())
                    {
                        UpgradeLegacyPhotoshopIcons(profile["rootRing"] as JsonObject);
                    }
                }
            }
        }
        root["schemaVersion"] = 5;
        return 5;
    }

    private static void UpgradeLegacyPhotoshopIcons(JsonObject? ring)
    {
        if (ring?["slots"] is not JsonArray slots)
        {
            return;
        }

        foreach (var slot in slots.OfType<JsonObject>())
        {
            if (slot["action"] is JsonObject action
                && ReadString(action["kind"]) == "keyboardShortcut"
                && action["keyboardShortcut"] is JsonObject shortcut
                && shortcut["chords"] is JsonArray { Count: 1 } chords
                && chords[0] is JsonObject chord)
            {
                (string? Key, string? OldIcon, string? Icon, KeyboardModifiers Modifiers) replacement = ReadString(action["name"]) switch
                {
                    "Кисть" => ("B", "text", "lucide:brush", KeyboardModifiers.None),
                    "Пипетка" => ("I", "copy", "lucide:pipette", KeyboardModifiers.None),
                    "Перемещение" => ("V", "mouse", "lucide:move", KeyboardModifiers.None),
                    "Прямоугольная область" => ("M", "screenshot", "lucide:scan", KeyboardModifiers.None),
                    "Лассо" => ("L", "mouse", "lucide:lasso", KeyboardModifiers.None),
                    "Рамка" => ("C", "screenshot", "lucide:crop", KeyboardModifiers.None),
                    "Ластик" => ("E", "cut", "lucide:eraser", KeyboardModifiers.None),
                    "Рука" => ("H", "mouse", "lucide:hand", KeyboardModifiers.None),
                    "Масштаб" => ("Z", "search", "lucide:zoom-in", KeyboardModifiers.None),
                    "Новый слой" => ("N", "window", "lucide:layers", KeyboardModifiers.Control | KeyboardModifiers.Shift),
                    _ => default,
                };
                var modifiers = ReadString(chord["modifiers"]) ?? "none";
                if (replacement.Key is not null
                    && ReadString(action["icon"]) == replacement.OldIcon
                    && string.Equals(ReadString(chord["key"]), replacement.Key, StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse<KeyboardModifiers>(modifiers, ignoreCase: true, out var parsedModifiers)
                    && parsedModifiers == replacement.Modifiers)
                {
                    action["icon"] = replacement.Icon;
                    if (ReadString(slot["icon"]) == replacement.OldIcon)
                    {
                        slot["icon"] = replacement.Icon;
                    }
                }
            }
            UpgradeLegacyPhotoshopIcons(slot["submenu"] as JsonObject);
        }
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void PromoteLegacyPalette(JsonObject profile)
    {
        if (profile["style"] is not JsonObject style
            || profile["rootRing"] is not JsonObject rootRing)
        {
            return;
        }

        var bubble = style["bubbleColor"]?.DeepClone();
        var icon = style["iconColor"]?.DeepClone();
        var hover = style["hoverColor"]?.DeepClone();
        if (bubble is null && icon is null && hover is null)
        {
            return;
        }

        PromotePaletteToRing(rootRing, bubble, icon, hover);
    }

    private static void PromotePaletteToRing(
        JsonObject ring,
        JsonNode? bubble,
        JsonNode? icon,
        JsonNode? hover)
    {
        var appearance = ring["appearance"] as JsonObject ?? new JsonObject();
        SetIfMissing(appearance, "bubbleColor", bubble);
        SetIfMissing(appearance, "bubbleHoverColor", hover);
        SetIfMissing(appearance, "iconColor", icon);
        SetIfMissing(
            appearance,
            "iconHoverColor",
            JsonValue.Create(RingAppearanceDefinition.DefaultIconHoverColor));
        ring["appearance"] = appearance;

        if (ring["slots"] is not JsonArray slots)
        {
            return;
        }

        foreach (var slot in slots.OfType<JsonObject>())
        {
            if (slot["submenu"] is JsonObject submenu)
            {
                PromotePaletteToRing(submenu, bubble, icon, hover);
            }
        }
    }

    private static void SetIfMissing(JsonObject target, string propertyName, JsonNode? value)
    {
        if (target[propertyName] is null && value is not null)
        {
            target[propertyName] = value.DeepClone();
        }
    }

    private static void MoveIfPresent(JsonObject source, JsonObject destination, string propertyName)
    {
        if (destination[propertyName] is null && source[propertyName] is JsonNode value)
        {
            destination[propertyName] = value.DeepClone();
        }

        source.Remove(propertyName);
    }
}
