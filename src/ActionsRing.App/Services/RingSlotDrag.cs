using System.Windows;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Services;

/// <summary>A local move, distinct from the action library's copy payload.</summary>
public sealed record RingSlotDrag(RingDefinition Root, RingSlotDefinition Source)
{
    public const string Format = "ActionsRing.ExistingRingSlot";

    public static bool ExceedsThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
}
