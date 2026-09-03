namespace ActionsRing.Core.Domain;

public readonly record struct RingAppearancePalette(
    string BubbleColor,
    string BubbleHoverColor,
    string IconColor,
    string IconHoverColor);

/// <summary>Resolves a slot palette by layering its optional channels over the ring palette.</summary>
public static class RingAppearanceResolver
{
    public static RingAppearancePalette Resolve(
        RingAppearanceDefinition ringAppearance,
        RingSlotAppearanceDefinition? slotAppearance = null)
    {
        ArgumentNullException.ThrowIfNull(ringAppearance);
        return new RingAppearancePalette(
            slotAppearance?.BubbleColor ?? ringAppearance.BubbleColor,
            slotAppearance?.BubbleHoverColor ?? ringAppearance.BubbleHoverColor,
            slotAppearance?.IconColor ?? ringAppearance.IconColor,
            slotAppearance?.IconHoverColor ?? ringAppearance.IconHoverColor);
    }
}
