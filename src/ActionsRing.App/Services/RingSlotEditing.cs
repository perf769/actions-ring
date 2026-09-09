using System.Text.Json;
using System.Text.Json.Serialization;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Services;

/// <summary>Composes action assignments without changing the current ring or the catalog template.</summary>
public static class RingSlotEditing
{
    /// <summary>Only siblings exchange positions; entire slot objects stay intact.</summary>
    public static bool CanSwap(RingDefinition root, RingSlotDefinition source, RingSlotDefinition target)
    {
        if (ReferenceEquals(source, target)) return false;
        var owner = FindOwner(root, source, new HashSet<RingDefinition>());
        return owner is not null && owner.Slots.Any(slot => ReferenceEquals(slot, target));
    }

    public static bool TrySwap(RingDefinition root, RingSlotDefinition source, RingSlotDefinition target)
    {
        if (!CanSwap(root, source, target)) return false;
        var owner = FindOwner(root, source, new HashSet<RingDefinition>())!;
        var sourceIndex = owner.Slots.IndexOf(source);
        var targetIndex = owner.Slots.IndexOf(target);
        (owner.Slots[sourceIndex], owner.Slots[targetIndex]) = (owner.Slots[targetIndex], owner.Slots[sourceIndex]);
        return true;
    }

    private static RingDefinition? FindOwner(RingDefinition ring, RingSlotDefinition source, HashSet<RingDefinition> visited)
    {
        if (!visited.Add(ring)) return null;
        if (ring.Slots.Any(slot => ReferenceEquals(slot, source))) return ring;
        foreach (var slot in ring.Slots)
        {
            if (slot.Submenu is { } submenu && FindOwner(submenu, source, visited) is { } owner) return owner;
        }
        return null;
    }

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
