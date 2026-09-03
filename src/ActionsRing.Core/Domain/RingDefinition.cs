namespace ActionsRing.Core.Domain;

/// <summary>A radial menu containing between four and eight ordered slots.</summary>
public sealed class RingDefinition
{
    public const int MinimumRootSlots = 4;
    public const int MaximumRootSlots = 8;
    public const int MinimumSubmenuSlots = 1;
    public const int MaximumSubmenuSlots = 9;

    // Compatibility aliases describe the root ring constraint.
    public const int MinimumSlots = MinimumRootSlots;
    public const int MaximumSlots = MaximumRootSlots;

    public string Id { get; set; } = ConfigurationIds.New("ring");

    public string Name { get; set; } = "Кольцо действий";

    /// <summary>The exact number of angular positions rendered by this ring.</summary>
    public int SlotCount { get; set; } = MaximumSlots;

    /// <summary>Slots are ordered clockwise starting at twelve o'clock.</summary>
    public List<RingSlotDefinition> Slots { get; set; } = [];

    /// <summary>
    /// Editable palette used when the profile selects the custom style. Keeping the palette on
    /// the ring lets every application ring remember its own colors while another preset is active.
    /// </summary>
    public RingAppearanceDefinition Appearance { get; set; } = RingAppearanceDefinition.CreateDefault();
}

/// <summary>The four colors required to render the resting and hovered bubble states.</summary>
public sealed class RingAppearanceDefinition
{
    public const string DefaultBubbleColor = "#F5F5F3";
    public const string DefaultBubbleHoverColor = "#050607";
    public const string DefaultIconColor = "#101316";
    public const string DefaultIconHoverColor = "#FFFFFF";

    public string BubbleColor { get; set; } = DefaultBubbleColor;

    public string BubbleHoverColor { get; set; } = DefaultBubbleHoverColor;

    public string IconColor { get; set; } = DefaultIconColor;

    public string IconHoverColor { get; set; } = DefaultIconHoverColor;

    public static RingAppearanceDefinition CreateDefault() => new();

    public RingAppearanceDefinition Clone() => new()
    {
        BubbleColor = BubbleColor,
        BubbleHoverColor = BubbleHoverColor,
        IconColor = IconColor,
        IconHoverColor = IconHoverColor,
    };

    public void ResetToDefaults()
    {
        BubbleColor = DefaultBubbleColor;
        BubbleHoverColor = DefaultBubbleHoverColor;
        IconColor = DefaultIconColor;
        IconHoverColor = DefaultIconHoverColor;
    }
}

/// <summary>
/// Optional per-slot colors. A null channel inherits the matching value from the ring palette.
/// </summary>
public sealed class RingSlotAppearanceDefinition
{
    public string? BubbleColor { get; set; }

    public string? BubbleHoverColor { get; set; }

    public string? IconColor { get; set; }

    public string? IconHoverColor { get; set; }

    public bool IsEmpty => BubbleColor is null
                           && BubbleHoverColor is null
                           && IconColor is null
                           && IconHoverColor is null;

    public RingSlotAppearanceDefinition Clone() => new()
    {
        BubbleColor = BubbleColor,
        BubbleHoverColor = BubbleHoverColor,
        IconColor = IconColor,
        IconHoverColor = IconHoverColor,
    };

    public void ResetToInherited()
    {
        BubbleColor = null;
        BubbleHoverColor = null;
        IconColor = null;
        IconHoverColor = null;
    }
}

/// <summary>
/// One radial position. A slot targets either an action or a nested ring; a submenu takes
/// precedence if malformed input contains both.
/// </summary>
public sealed class RingSlotDefinition
{
    public string Id { get; set; } = ConfigurationIds.New("slot");

    public string Label { get; set; } = "Добавить действие";

    public string? Icon { get; set; }

    public ActionDefinition? Action { get; set; } = ActionDefinition.None();

    public RingDefinition? Submenu { get; set; }

    /// <summary>Colors that override the parent ring palette for this bubble only.</summary>
    public RingSlotAppearanceDefinition? AppearanceOverride { get; set; }

    public static RingSlotDefinition ForAction(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new RingSlotDefinition
        {
            Label = action.Name,
            Icon = action.Icon,
            Action = action,
        };
    }

    public static RingSlotDefinition ForSubmenu(string label, RingDefinition submenu, string? icon = null)
    {
        ArgumentNullException.ThrowIfNull(submenu);
        return new RingSlotDefinition
        {
            Label = label,
            Icon = icon,
            Action = null,
            Submenu = submenu,
        };
    }

    public static RingSlotDefinition Empty(int position) => new()
    {
        Label = $"Добавить действие {position + 1}",
        Action = ActionDefinition.None(),
    };
}
