using ActionsRing.Core.Domain;

namespace ActionsRing.Core.Configuration;

/// <summary>Restores the standard eight actions without discarding the ring identity or palette.</summary>
public static class RingDefaults
{
    public static void RestoreSlots(RingDefinition ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var defaults = ConfigurationDefaults.CreateDefaultRing();
        ring.SlotCount = defaults.SlotCount;
        ring.Slots = defaults.Slots;
    }
}
