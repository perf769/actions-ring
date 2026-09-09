using System.Text.Json;
using System.Text.Json.Serialization;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Services;

/// <summary>Composes action assignments without changing the current ring or the catalog template.</summary>
public static class RingSlotEditing
{
    public static RingSlotDefinition? ComposeAssignment(
        RingSlotDefinition target,
        RingSlotDefinition replacement)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(replacement);

        if (target.Submenu is not null && replacement.Submenu is not null)
        {
            return null;
        }

        var result = Clone(replacement);
        if (target.Submenu is not null)
        {
            var folder = Clone(target);
            folder.Action = result.Action;
            return folder;
        }

        if (result.Submenu is not null && target.Action is { Kind: not ActionKind.None })
        {
            var original = Clone(target);
            result.Action = original.Action;
            result.Label = original.Label;
            result.Icon = original.Icon;
            result.Submenu.Name = original.Label;
        }

        return result;
    }

    private static RingSlotDefinition Clone(RingSlotDefinition slot)
    {
        var options = ConfigurationJson.Options;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        return JsonSerializer.Deserialize<RingSlotDefinition>(JsonSerializer.Serialize(slot, options), options)
               ?? throw new InvalidDataException("Could not clone the ring slot.");
    }
}
