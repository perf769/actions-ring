using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;

namespace ActionsRing.App.Controls;

public enum RingInteractionMode
{
    Execute,
    Configure,
}

public sealed class RingSlotEventArgs(RingSlotDefinition slot, bool isSubmenuItem) : EventArgs
{
    public RingSlotDefinition Slot { get; } = slot;
    public bool IsSubmenuItem { get; } = isSubmenuItem;
}

public sealed class RingSlotDropEventArgs(RingSlotDefinition target, object payload) : EventArgs
{
    public RingSlotDefinition Target { get; } = target;
    public object Payload { get; } = payload;
}

/// <summary>
/// Code-native radial renderer shared by the live overlay and the settings preview.  Keeping the
/// geometry in one control prevents the editor from drifting away from the menu the user invokes.
/// </summary>
public partial class RingMenuControl : UserControl
{
    private const double BaseNodeSize = 48;
    private const double BaseSubmenuNodeSize = 48;
    private const double BaseRadius = 82;
    private const double BaseSubmenuRadius = 154;
    private const double BaseCenterSize = 32;

    private readonly List<NodeVisual> _rootNodes = [];
    private readonly List<NodeVisual> _submenuNodes = [];
    private readonly DispatcherTimer _submenuTimer;
    private readonly DispatcherTimer _tooltipTimer;
    private readonly DispatcherTimer _adjustmentFeedbackTimer;
    private RingDefinition? _ring;
    private RingSlotDefinition? _pendingSubmenu;
    private NodeVisual? _pendingTooltip;
    private Border? _centerVisual;
    private ShapePath? _submenuBridge;
    private readonly List<RingSlotDefinition> _openFolders = [];
    private SubmenuAnimationState? _submenuAnimation;
    private long _submenuAnimationRevision;
    private NodeVisual? _adjustmentFeedbackNode;
    private bool _animateOnNextLayout;
    private RingStyleDefinition? _style;

    private static readonly DependencyProperty SubmenuProgressProperty = DependencyProperty.Register(
        "SubmenuProgress", typeof(double), typeof(RingMenuControl),
        new PropertyMetadata(0d, (owner, args) => ((RingMenuControl)owner).RenderSubmenuFrame((double)args.NewValue)));

    public RingMenuControl()
    {
        InitializeComponent();
        _submenuTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(135),
        };
        _submenuTimer.Tick += (_, _) =>
        {
            _submenuTimer.Stop();
            var folder = _pendingSubmenu;
            _pendingSubmenu = null;
            if (folder?.Submenu is not null && !ReferenceEquals(OpenFolder, folder))
            {
                OpenSubmenu(folder, animate: true);
            }
        };
        _tooltipTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _tooltipTimer.Tick += (_, _) =>
        {
            _tooltipTimer.Stop();
            if (ShowTooltips && _pendingTooltip is { } node && ReferenceEquals(HoveredSlot, node.Slot)
                && !_openFolders.Contains(node.Slot))
            {
                SetLabelVisible(node, true, animate: AnimationsEnabled);
            }
        };
        _adjustmentFeedbackTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(720),
        };
        _adjustmentFeedbackTimer.Tick += (_, _) =>
        {
            _adjustmentFeedbackTimer.Stop();
            if (_adjustmentFeedbackNode is { } node)
            {
                UpdateAdjustmentFeedback(node, 0, animate: AnimationsEnabled);
            }
            _adjustmentFeedbackNode = null;
        };

        SizeChanged += (_, _) => RepositionWithoutAnimation();
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    public event EventHandler<RingSlotEventArgs>? SlotInvoked;
    public event EventHandler<RingSlotEventArgs>? SlotFocused;
    public event EventHandler? SelectionChanged;
    public event EventHandler<RingSlotDropEventArgs>? SlotDropRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<int>? AdjustmentRequested;

    public RingInteractionMode InteractionMode { get; set; } = RingInteractionMode.Execute;
    public bool AnimationsEnabled { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public bool ShowTooltips { get; set; } = true;
    public bool ShowConfigurationLabels
    {
        get => _showConfigurationLabels;
        set
        {
            if (_showConfigurationLabels == value)
            {
                return;
            }
            _showConfigurationLabels = value;
            Rebuild(animate: false);
        }
    }
    private bool _showConfigurationLabels = true;
    public int OpenAnimationMilliseconds { get; set; } = 240;
    public int SubmenuAnimationMilliseconds { get; set; } = 360;
    public double CenterDiameter { get; set; } = BaseCenterSize;

    public int TooltipDelayMilliseconds
    {
        get => (int)_tooltipTimer.Interval.TotalMilliseconds;
        set => _tooltipTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(value, 0, 5000));
    }

    public double RingScale
    {
        get => _ringScale;
        set
        {
            _ringScale = Math.Clamp(value, 0.60, 2.00);
            Rebuild(animate: false);
        }
    }
    private double _ringScale = 1.0;

    /// <summary>NaN uses the center of this control; overlays provide cursor-relative coordinates.</summary>
    public Point RingCenter
    {
        get => _ringCenter;
        set
        {
            _ringCenter = value;
            RepositionWithoutAnimation();
        }
    }
    private Point _ringCenter = new(double.NaN, double.NaN);

    /// <summary>
    /// Optional safe rectangle in control coordinates. The live overlay supplies the monitor work
    /// area so labels stay clear of taskbars; editor previews use the whole control.
    /// </summary>
    public Rect TooltipBounds
    {
        get => _tooltipBounds;
        set
        {
            _tooltipBounds = value;
            RepositionWithoutAnimation();
        }
    }
    private Rect _tooltipBounds = Rect.Empty;

    public RingSlotDefinition? HoveredSlot { get; private set; }
    public RingSlotDefinition? OpenFolder { get; private set; }
    public RingSlotDefinition? SelectedSlot { get; private set; }

    public void SetSelectedSlot(RingSlotDefinition? slot, bool animate = false)
    {
        if (slot is not null && (_ring is null || !FindSlotPath(_ring, slot, [], out _)))
        {
            slot = null;
        }
        var changed = !ReferenceEquals(SelectedSlot, slot);
        SelectedSlot = slot;
        if (InteractionMode == RingInteractionMode.Configure)
        {
            HoveredSlot = null;
        }
        CloseSubmenus(animate: false);
        if (slot is not null && _ring is not null && FindSlotPath(_ring, slot, [], out var path))
        {
            foreach (var folder in path)
            {
                OpenSubmenu(folder, animate: animate && ReferenceEquals(folder, slot));
            }
        }
        RefreshNodeStates(animate);
        if (changed)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool FindSlotPath(
        RingDefinition ring, RingSlotDefinition target, List<RingSlotDefinition> ancestors,
        out List<RingSlotDefinition> path)
    {
        foreach (var slot in ring.Slots)
        {
            if (ReferenceEquals(slot, target))
            {
                path = slot.Submenu is null ? ancestors : [.. ancestors, slot];
                return true;
            }
            if (slot.Submenu is not null
                && FindSlotPath(slot.Submenu, target, [.. ancestors, slot], out path))
            {
                return true;
            }
        }
        path = [];
        return false;
    }

    public void ApplyStyle(RingStyleDefinition? style)
    {
        _style = style;
        UpdatePaletteResources();
        Rebuild(animate: false);
    }

    private void UpdatePaletteResources()
    {
        var style = _style;
        var preset = style?.Preset ?? RingStylePreset.Inherit;
        var custom = _ring?.Appearance;
        var (bubble, icon, hover, iconHover) = preset switch
        {
            RingStylePreset.Dark => ("#050607", "#FFFFFF", "#824EF9", "#FFFFFF"),
            RingStylePreset.Ocean => ("#001C30", "#2B9FFF", "#0A3958", "#FFFFFF"),
            RingStylePreset.Purple => ("#24007A", "#E3D9FF", "#5A2AC2", "#FFFFFF"),
            RingStylePreset.Custom => (
                ValidColor(custom?.BubbleColor ?? style?.BubbleColor, RingAppearanceDefinition.DefaultBubbleColor),
                ValidColor(custom?.IconColor ?? style?.IconColor, RingAppearanceDefinition.DefaultIconColor),
                ValidColor(custom?.BubbleHoverColor ?? style?.HoverColor, RingAppearanceDefinition.DefaultBubbleHoverColor),
                ValidColor(custom?.IconHoverColor, RingAppearanceDefinition.DefaultIconHoverColor)),
            RingStylePreset.Light => ("#F5F5F3", "#101316", "#050607", "#FFFFFF"),
            _ => (null, null, null, null),
        };

        if (bubble is null)
        {
            Resources.Remove("RingBubbleBrush");
            Resources.Remove("RingIconBrush");
            Resources.Remove("RingBubbleHoverBrush");
            Resources.Remove("RingIconHoverBrush");
        }
        else
        {
            Resources["RingBubbleBrush"] = new SolidColorBrush(ParseColor(bubble));
            Resources["RingIconBrush"] = new SolidColorBrush(ParseColor(icon!));
            Resources["RingBubbleHoverBrush"] = new SolidColorBrush(ParseColor(hover!));
            Resources["RingIconHoverBrush"] = new SolidColorBrush(ParseColor(iconHover!));
        }
    }

    public void Present(RingDefinition ring, bool animate = true)
    {
        ArgumentNullException.ThrowIfNull(ring);
        animate &= AnimationsEnabled;
        _ring = ring;
        UpdatePaletteResources();
        OpenFolder = null;
        SelectedSlot = null;
        _openFolders.Clear();
        HoveredSlot = null;
        _pendingSubmenu = null;
        _pendingTooltip = null;
        _adjustmentFeedbackNode = null;
        _submenuTimer.Stop();
        _tooltipTimer.Stop();
        _adjustmentFeedbackTimer.Stop();
        Rebuild(animate);
    }

    public void Clear()
    {
        _ring = null;
        OpenFolder = null;
        SelectedSlot = null;
        _openFolders.Clear();
        HoveredSlot = null;
        _pendingSubmenu = null;
        _submenuTimer.Stop();
        _tooltipTimer.Stop();
        _adjustmentFeedbackTimer.Stop();
        _pendingTooltip = null;
        _adjustmentFeedbackNode = null;
        RingCanvas.Children.Clear();
        TooltipCanvas.Children.Clear();
        StopSubmenuAnimation();
        _rootNodes.Clear();
        _submenuNodes.Clear();
        _centerVisual = null;
        _submenuBridge = null;
        _animateOnNextLayout = false;
    }

    public void OpenSubmenu(RingSlotDefinition folder, bool animate = true)
    {
        if (folder.Submenu is null || _ring is null)
        {
            return;
        }

        animate &= AnimationsEnabled;
        var parent = _rootNodes.Concat(_submenuNodes)
            .FirstOrDefault(node => ReferenceEquals(node.Slot, folder));
        if (parent is null)
        {
            return;
        }
        var parentPoint = NodeCenter(parent);
        var level = parent.ParentFolder is null ? 0 : _openFolders.IndexOf(parent.ParentFolder) + 1;
        RemoveSubmenuVisuals(level);
        _openFolders.Add(folder);
        OpenFolder = folder;

        var slots = folder.Submenu.Slots.Take(Math.Max(folder.Submenu.SlotCount, 0)).ToList();
        if (slots.Count == 0)
        {
            RefreshNodeStates(animate);
            return;
        }

        var center = EffectiveCenter();
        var parentAngle = parent.Angle;
        var nodeSize = BaseSubmenuNodeSize * RingScale;
        var obstacles = _rootNodes.Concat(_submenuNodes)
            .Select(NodeCenter)
            .Append(center)
            .ToArray();
        var targets = RingLayoutGeometry.ArrangeSubmenu(
            center,
            parentPoint,
            new Size(ActualWidth, ActualHeight),
            slots.Count,
            nodeSize,
            BaseSubmenuRadius * RingScale,
            parentAngle,
            obstacles,
            10d * RingScale);
        var leadIndex = (slots.Count - 1) / 2;
        var newNodes = new List<NodeVisual>();

        for (var index = 0; index < slots.Count; index++)
        {
            var target = targets[index];
            var angle = AngleBetween(center, target);
            var node = CreateNode(slots[index], angle, isSubmenu: true);
            node.ParentFolder = folder;
            _submenuNodes.Add(node);
            newNodes.Add(node);
            RingCanvas.Children.Add(node.Container);
            TooltipCanvas.Children.Add(node.Label);
            PositionNode(node, target, center);
            Panel.SetZIndex(node.Container, 30 + level * 10 + index);
        }
        RefreshNodeStates(animate: false);
        if (animate && !ReduceMotion)
        {
            StartSubmenuAnimation(parent, newNodes, targets, leadIndex);
        }
        else if (animate)
        {
            foreach (var node in newNodes)
            {
                AnimateNodeEntrance(node, parentPoint, node.Center, 90, 0);
            }
        }
    }

    public Task AnimateOutAsync()
    {
        if (_rootNodes.Count == 0)
        {
            return Task.CompletedTask;
        }

        _submenuTimer.Stop();
        _tooltipTimer.Stop();
        _pendingSubmenu = null;
        _pendingTooltip = null;
        _submenuAnimationRevision++;
        _submenuAnimation = null;
        BeginAnimation(SubmenuProgressProperty, null);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodes = _rootNodes.Concat(_submenuNodes).ToArray();
        var center = EffectiveCenter();
        var longest = 0;
        var exitDuration = ReduceMotion ? 75 : 115;
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var delay = ReduceMotion ? 0 : Math.Min(index * 7, 42);
            longest = Math.Max(longest, delay);
            AnimateNodeExit(node, center, exitDuration, delay);
        }
        _centerVisual?.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(exitDuration))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            });
        _submenuBridge?.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(exitDuration)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(exitDuration + 15 + longest) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completion.TrySetResult();
        };
        timer.Start();
        return completion.Task;
    }

    private void Rebuild(bool animate)
    {
        var selection = SelectedSlot;
        var openPath = _openFolders.ToArray();
        StopSubmenuAnimation();
        RingCanvas.Children.Clear();
        TooltipCanvas.Children.Clear();
        _rootNodes.Clear();
        _submenuNodes.Clear();
        _openFolders.Clear();
        OpenFolder = null;
        _centerVisual = null;
        _submenuBridge = null;
        _animateOnNextLayout = animate;

        if (_ring is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var center = EffectiveCenter();
        CreateCenter(center, animate);

        var requestedCount = Math.Clamp(_ring.SlotCount, 1, RingDefinition.MaximumSlots);
        var slots = _ring.Slots.Take(requestedCount).ToList();
        var targets = RingLayoutGeometry.ArrangeRoot(
            center,
            new Size(ActualWidth, ActualHeight),
            slots.Count,
            BaseNodeSize * RingScale,
            BaseRadius * RingScale,
            10d * RingScale);
        for (var index = 0; index < slots.Count; index++)
        {
            var target = targets[index];
            var angle = AngleBetween(center, target);
            var node = CreateNode(slots[index], angle, isSubmenu: false);
            _rootNodes.Add(node);
            RingCanvas.Children.Add(node.Container);
            TooltipCanvas.Children.Add(node.Label);
            PositionNode(node, target, center);
            Panel.SetZIndex(node.Container, 10 + index);
            if (animate)
            {
                var total = Math.Clamp(OpenAnimationMilliseconds, 80, 1000);
                var staggerBudget = Math.Min(120, Math.Max(0, total / 3));
                var delay = slots.Count <= 1
                    ? 0
                    : (int)Math.Round(index * staggerBudget / (double)(slots.Count - 1));
                AnimateNodeEntrance(node, center, target, Math.Max(80, total - staggerBudget), delay);
            }
        }
        var restoredPath = openPath.Length > 0
            ? openPath
            : selection is not null && FindSlotPath(_ring, selection, [], out var selectedPath)
                ? selectedPath.ToArray()
                : [];
        foreach (var folder in restoredPath)
        {
            OpenSubmenu(folder, animate: false);
        }
        SelectedSlot = selection;
        if (!animate || restoredPath.Length > 0 || selection is not null)
        {
            RefreshNodeStates(animate: false);
        }
        _animateOnNextLayout = false;
    }

    private void CreateCenter(Point center, bool animate)
    {
        var size = Math.Clamp(CenterDiameter, 24, 120) * RingScale;
        var glyph = new ShapePath
        {
            Data = Geometry.Parse("M 1,1 L 9,9 M 9,1 L 1,9"),
            Width = 10 * RingScale,
            Height = 10 * RingScale,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6 * RingScale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = ResourceBrush("RingIconBrush", Brushes.Black),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2d),
            Background = ResourceBrush("RingBubbleBrush", Brushes.WhiteSmoke),
            Child = glyph,
            Cursor = Cursors.Hand,
            Effect = TryFindResource("SoftShadow") as Effect,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        border.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            if (InteractionMode == RingInteractionMode.Configure)
            {
                SetSelectedSlot(null);
            }
            else
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        border.MouseLeftButtonDown += (_, args) => args.Handled = true;
        border.MouseEnter += (_, _) =>
        {
            border.Background = ResourceBrush("DangerBrush", new SolidColorBrush(Color.FromRgb(222, 102, 135)));
            glyph.Stroke = Brushes.White;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = ResourceBrush("RingBubbleBrush", Brushes.WhiteSmoke);
            glyph.Stroke = ResourceBrush("RingIconBrush", Brushes.Black);
        };
        Canvas.SetLeft(border, center.X - size / 2d);
        Canvas.SetTop(border, center.Y - size / 2d);
        Panel.SetZIndex(border, 50);
        RingCanvas.Children.Add(border);
        _centerVisual = border;

        if (animate && AnimationsEnabled)
        {
            border.Opacity = 0;
            var duration = TimeSpan.FromMilliseconds(ReduceMotion ? 90 : 150);
            border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            if (!ReduceMotion)
            {
                var transform = (ScaleTransform)border.RenderTransform;
                transform.ScaleX = transform.ScaleY = 0.45;
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, EntranceScaleAnimation(150, 0));
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, EntranceScaleAnimation(150, 0));
            }
        }
    }

    private NodeVisual CreateNode(
        RingSlotDefinition slot,
        double angle,
        bool isSubmenu)
    {
        var size = (isSubmenu ? BaseSubmenuNodeSize : BaseNodeSize) * RingScale;
        var useOverrides = _style?.Preset == RingStylePreset.Custom;
        var appearance = useOverrides ? slot.AppearanceOverride : null;
        var restingBackground = SlotBrush(
            appearance?.BubbleColor,
            "RingBubbleBrush",
            Brushes.WhiteSmoke);
        var restingForeground = SlotBrush(
            appearance?.IconColor,
            "RingIconBrush",
            Brushes.Black);
        var hoverBackground = SlotBrush(
            appearance?.BubbleHoverColor,
            "RingBubbleHoverBrush",
            Brushes.Black);
        var hoverForeground = SlotBrush(
            appearance?.IconHoverColor,
            "RingIconHoverBrush",
            Brushes.White);
        var foreground = restingForeground;
        var background = CloneBrush(restingBackground);
        var icon = new ActionIconView
        {
            Width = size * 0.54d,
            Height = size * 0.54d,
            Foreground = foreground,
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        icon.SetIcon(string.IsNullOrWhiteSpace(slot.Icon)
            && slot.Submenu is not null && slot.Action is null or { Kind: ActionKind.None }
                ? "folder" : slot.Icon, slot.Action);
        FrameworkElement iconVisual = icon;

        Grid? adjustmentContent = null;
        Grid? adjustmentDetails = null;
        TextBlock? adjustmentName = null;
        Border? adjustmentFeedbackChip = null;
        TextBlock? adjustmentFeedback = null;
        FrameworkElement bubbleContent = iconVisual;
        var adjustment = InteractionMode == RingInteractionMode.Execute
            && slot.Submenu is null
            && slot.Action is { Kind: ActionKind.AdjustParameter, AdjustParameter: not null }
                ? slot.Action.AdjustParameter
                : null;
        if (adjustment is not null)
        {
            var text = AdjustmentVisualFormatter.Format(adjustment, 0);
            adjustmentContent = new Grid
            {
                IsHitTestVisible = false,
                ClipToBounds = true,
            };
            adjustmentContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size) });
            adjustmentContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(iconVisual, 0);
            adjustmentContent.Children.Add(iconVisual);

            adjustmentDetails = new Grid
            {
                Margin = new Thickness(3 * RingScale, 0, 9 * RingScale, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            adjustmentDetails.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            adjustmentDetails.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            adjustmentName = new TextBlock
            {
                Text = text.ParameterLabel,
                FontSize = 11.5 * RingScale,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            adjustmentFeedback = new TextBlock
            {
                Text = text.FeedbackText,
                FontSize = 11.5 * RingScale,
                FontWeight = FontWeights.Bold,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            adjustmentFeedbackChip = new Border
            {
                MinWidth = 43 * RingScale,
                Margin = new Thickness(6 * RingScale, 0, 0, 0),
                Padding = new Thickness(6 * RingScale, 3 * RingScale, 6 * RingScale, 3 * RingScale),
                CornerRadius = new CornerRadius(10 * RingScale),
                Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Child = adjustmentFeedback,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            Grid.SetColumn(adjustmentName, 0);
            Grid.SetColumn(adjustmentFeedbackChip, 1);
            adjustmentDetails.Children.Add(adjustmentName);
            adjustmentDetails.Children.Add(adjustmentFeedbackChip);
            Grid.SetColumn(adjustmentDetails, 1);
            adjustmentContent.Children.Add(adjustmentDetails);
            bubbleContent = adjustmentContent;
        }

        var bubble = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2d),
            Background = background,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = bubbleContent,
            Cursor = slot.Action?.Kind == ActionKind.None && slot.Submenu is null && InteractionMode == RingInteractionMode.Execute
                ? Cursors.Arrow
                : Cursors.Hand,
            Effect = adjustment is null ? TryFindResource("SoftShadow") as Effect : null,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            AllowDrop = InteractionMode == RingInteractionMode.Configure,
            ClipToBounds = adjustment is not null,
        };

        var labelText = string.IsNullOrWhiteSpace(slot.Label) ? "Действие" : slot.Label;
        var label = new Border
        {
            Background = ResourceBrush("RingTooltipBrush", Brushes.White),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(11, 7, 11, 7),
            Opacity = InteractionMode == RingInteractionMode.Configure && ShowConfigurationLabels ? 1 : 0,
            IsHitTestVisible = false,
            Effect = TryFindResource("SoftShadow") as Effect,
            Child = new TextBlock
            {
                Text = labelText,
                FontSize = 12.5 * RingScale,
                FontWeight = FontWeights.Medium,
                Foreground = ResourceBrush("RingTooltipTextBrush", Brushes.Black),
                MaxWidth = 360d * RingScale,
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var container = new Canvas
        {
            Width = adjustment is null
                ? size
                : AdjustmentVisualFormatter.PreferredWidth(adjustment.Parameter) * RingScale,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Opacity = 1,
        };
        Border? adjustmentShadow = null;
        if (adjustment is not null)
        {
            // Keep the effect outside the clipped, width-animated bubble. Combining a
            // DropShadowEffect with ClipToBounds on the same animated Border leaves a
            // rectangular shader surface behind on some WPF render paths.
            adjustmentShadow = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2d),
                Background = CloneBrush(background),
                Effect = TryFindResource("SoftShadow") as Effect,
                IsHitTestVisible = false,
            };
            container.Children.Add(adjustmentShadow);
            Canvas.SetLeft(adjustmentShadow, 0);
            Canvas.SetTop(adjustmentShadow, 0);
        }
        ShapePath? tail = null;
        ShapePath? tailChevron = null;
        if (slot.Submenu is not null)
        {
            tail = new ShapePath
            {
                Fill = background,
                Cursor = Cursors.Hand,
                Effect = TryFindResource("SoftShadow") as Effect,
            };
            bubble.Effect = null;
            container.Children.Add(tail);
            tailChevron = new ShapePath
            {
                Data = Geometry.Parse("M -1.2,-2.2 L 1,0 L -1.2,2.2"),
                Stroke = foreground,
                StrokeThickness = 1.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
        }

        container.Children.Add(bubble);
        Canvas.SetLeft(bubble, 0);
        Canvas.SetTop(bubble, 0);
        if (tailChevron is not null)
        {
            container.Children.Add(tailChevron);
        }

        var visual = new NodeVisual(
            slot,
            container,
            bubble,
            iconVisual,
            label,
            tail,
            tailChevron,
            angle,
            isSubmenu,
            size,
            adjustment,
            adjustmentContent,
            adjustmentDetails,
            adjustmentName,
            adjustmentFeedbackChip,
            adjustmentFeedback,
            adjustmentShadow,
            restingBackground,
            restingForeground,
            hoverBackground,
            hoverForeground);
        container.MouseEnter += (_, _) => OnNodeEntered(visual);
        container.MouseLeave += (_, _) => OnNodeLeft(visual);
        bubble.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            OnNodeClicked(visual);
        };
        bubble.MouseLeftButtonDown += (_, args) => args.Handled = true;
        if (tail is not null)
        {
            tail.MouseLeftButtonDown += (_, args) => args.Handled = true;
            tail.MouseLeftButtonUp += (_, args) =>
            {
                args.Handled = true;
                OnNodeClicked(visual);
            };
        }
        bubble.DragEnter += (_, args) =>
        {
            if (InteractionMode != RingInteractionMode.Configure)
            {
                return;
            }
            args.Effects = DragDropEffects.Copy;
            args.Handled = true;
            SetNodeHover(visual, true, animate: true);
        };
        bubble.DragLeave += (_, args) =>
        {
            SetNodeHover(visual, IsNodeActive(visual), animate: true);
            args.Handled = true;
        };
        bubble.Drop += (_, args) =>
        {
            if (InteractionMode != RingInteractionMode.Configure)
            {
                return;
            }
            var payload = args.Data.GetData("ActionsRing.ActionCatalogItem");
            if (payload is not null)
            {
                SlotDropRequested?.Invoke(this, new RingSlotDropEventArgs(slot, payload));
            }
            args.Handled = true;
        };
        return visual;
    }

    private void OnNodeEntered(NodeVisual node)
    {
        if (InteractionMode == RingInteractionMode.Execute)
        {
            if (!node.IsSubmenu && !ReferenceEquals(node.Slot, _openFolders.FirstOrDefault()))
            {
                CloseSubmenus(animate: false);
            }
            else if (node.ParentFolder is not null && !ReferenceEquals(node.Slot, OpenFolder))
            {
                var level = _openFolders.IndexOf(node.ParentFolder) + 1;
                if (level > 0 && level < _openFolders.Count)
                {
                    RemoveSubmenuVisuals(level);
                }
            }
        }
        HoveredSlot = node.Slot;
        RefreshNodeStates(animate: AnimationsEnabled);
        _tooltipTimer.Stop();
        _pendingTooltip = node;
        if (InteractionMode == RingInteractionMode.Execute && ShowTooltips && node.Adjustment is null
            && !_openFolders.Contains(node.Slot))
        {
            if (TooltipDelayMilliseconds == 0)
            {
                SetLabelVisible(node, true, animate: AnimationsEnabled);
            }
            else
            {
                _tooltipTimer.Start();
            }
        }
        if (InteractionMode == RingInteractionMode.Execute)
        {
            SlotFocused?.Invoke(this, new RingSlotEventArgs(node.Slot, node.IsSubmenu));
        }

        if (InteractionMode == RingInteractionMode.Execute
            && node.Slot.Submenu is not null
            && !ReferenceEquals(OpenFolder, node.Slot))
        {
            _pendingSubmenu = node.Slot;
            _submenuTimer.Stop();
            _submenuTimer.Start();
        }
    }

    private void OnNodeLeft(NodeVisual node)
    {
        if (ReferenceEquals(HoveredSlot, node.Slot))
        {
            HoveredSlot = null;
        }
        RefreshNodeStates(animate: AnimationsEnabled);
        if (ReferenceEquals(_pendingSubmenu, node.Slot))
        {
            _submenuTimer.Stop();
            _pendingSubmenu = null;
        }
        if (ReferenceEquals(_pendingTooltip, node))
        {
            _tooltipTimer.Stop();
            _pendingTooltip = null;
        }
        if (ReferenceEquals(_adjustmentFeedbackNode, node))
        {
            _adjustmentFeedbackTimer.Stop();
            _adjustmentFeedbackNode = null;
        }
        UpdateAdjustmentFeedback(node, 0, animate: false);
    }

    private void OnNodeClicked(NodeVisual node)
    {
        if (InteractionMode == RingInteractionMode.Configure)
        {
            SetSelectedSlot(ReferenceEquals(SelectedSlot, node.Slot) ? null : node.Slot, animate: true);
            if (SelectedSlot is not null)
            {
                SlotFocused?.Invoke(this, new RingSlotEventArgs(node.Slot, node.IsSubmenu));
                SlotInvoked?.Invoke(this, new RingSlotEventArgs(node.Slot, node.IsSubmenu));
            }
            return;
        }

        if (node.Slot.Submenu is not null)
        {
            if (!ReferenceEquals(OpenFolder, node.Slot))
            {
                OpenSubmenu(node.Slot, animate: true);
            }
            if (node.Slot.Action is null or { Kind: ActionKind.None })
            {
                return;
            }
        }

        if (node.Slot.Action is null or { Kind: ActionKind.None })
        {
            return;
        }
        SlotInvoked?.Invoke(this, new RingSlotEventArgs(node.Slot, node.IsSubmenu));
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (InteractionMode != RingInteractionMode.Execute
            || HoveredSlot?.Action is not { Kind: ActionKind.AdjustParameter, AdjustParameter: not null }
            || args.Delta == 0)
        {
            return;
        }

        var direction = Math.Sign(args.Delta);
        var node = _rootNodes.Concat(_submenuNodes)
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Slot, HoveredSlot));
        if (node is not null)
        {
            UpdateAdjustmentFeedback(node, direction, animate: AnimationsEnabled);
            _adjustmentFeedbackNode = node;
            _adjustmentFeedbackTimer.Stop();
            _adjustmentFeedbackTimer.Start();
        }

        AdjustmentRequested?.Invoke(this, direction);
        args.Handled = true;
    }

    private void SetAdjustmentExpanded(NodeVisual node, bool expanded, bool animate)
    {
        if (node.Adjustment is null
            || node.AdjustmentContent is null
            || node.AdjustmentDetails is null)
        {
            return;
        }

        var pillWidth = node.Container.Width;
        ConfigureAdjustmentDirection(node, node.ExpandsRight);

        var targetWidth = expanded ? pillWidth : node.NodeSize;
        var targetLeft = expanded || node.ExpandsRight ? 0d : pillWidth - node.NodeSize;
        var targetDetailsOpacity = expanded ? 1d : 0d;
        var wasExpanded = node.IsAdjustmentExpanded;
        if (expanded && !wasExpanded)
        {
            node.RestingZIndex = Panel.GetZIndex(node.Container);
            Panel.SetZIndex(node.Container, 80);
        }
        else if (!expanded && wasExpanded)
        {
            Panel.SetZIndex(node.Container, node.RestingZIndex);
        }
        node.IsAdjustmentExpanded = expanded;

        animate &= AnimationsEnabled && !ReduceMotion;
        if (!animate)
        {
            SetAdjustmentSurfaceSize(node.Bubble, targetWidth, targetLeft);
            if (node.AdjustmentShadow is not null)
            {
                SetAdjustmentSurfaceSize(node.AdjustmentShadow, targetWidth, targetLeft);
            }
            node.AdjustmentDetails.BeginAnimation(OpacityProperty, null);
            node.AdjustmentDetails.Opacity = targetDetailsOpacity;
            return;
        }

        var width = double.IsFinite(node.Bubble.ActualWidth) && node.Bubble.ActualWidth > 0
            ? node.Bubble.ActualWidth
            : node.Bubble.Width;
        var left = Canvas.GetLeft(node.Bubble);
        if (!double.IsFinite(left))
        {
            left = 0;
        }
        var detailsOpacity = node.AdjustmentDetails.Opacity;
        var duration = TimeSpan.FromMilliseconds(expanded ? 225 : 155);
        var easing = new CubicEase
        {
            EasingMode = expanded ? EasingMode.EaseOut : EasingMode.EaseInOut,
        };

        node.Bubble.Width = targetWidth;
        Canvas.SetLeft(node.Bubble, targetLeft);
        node.AdjustmentDetails.Opacity = targetDetailsOpacity;
        AnimateAdjustmentSurface(node.Bubble, width, targetWidth, left, targetLeft, duration, easing);
        if (node.AdjustmentShadow is not null)
        {
            var shadowWidth = double.IsFinite(node.AdjustmentShadow.ActualWidth) && node.AdjustmentShadow.ActualWidth > 0
                ? node.AdjustmentShadow.ActualWidth
                : node.AdjustmentShadow.Width;
            var shadowLeft = Canvas.GetLeft(node.AdjustmentShadow);
            if (!double.IsFinite(shadowLeft))
            {
                shadowLeft = 0;
            }
            node.AdjustmentShadow.Width = targetWidth;
            Canvas.SetLeft(node.AdjustmentShadow, targetLeft);
            AnimateAdjustmentSurface(
                node.AdjustmentShadow,
                shadowWidth,
                targetWidth,
                shadowLeft,
                targetLeft,
                duration,
                easing);
        }
        node.AdjustmentDetails.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(detailsOpacity, targetDetailsOpacity, TimeSpan.FromMilliseconds(expanded ? 145 : 85))
            {
                BeginTime = expanded ? TimeSpan.FromMilliseconds(55) : TimeSpan.Zero,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
    }

    private static void SetAdjustmentSurfaceSize(FrameworkElement surface, double width, double left)
    {
        surface.BeginAnimation(WidthProperty, null);
        surface.BeginAnimation(Canvas.LeftProperty, null);
        surface.Width = width;
        Canvas.SetLeft(surface, left);
    }

    private static void AnimateAdjustmentSurface(
        FrameworkElement surface,
        double fromWidth,
        double toWidth,
        double fromLeft,
        double toLeft,
        Duration duration,
        IEasingFunction easing)
    {
        surface.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(fromWidth, toWidth, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        surface.BeginAnimation(
            Canvas.LeftProperty,
            new DoubleAnimation(fromLeft, toLeft, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    private static void ConfigureAdjustmentDirection(NodeVisual node, bool expandRight)
    {
        if (node.AdjustmentContent is null || node.AdjustmentDetails is null)
        {
            return;
        }

        var columns = node.AdjustmentContent.ColumnDefinitions;
        columns[0].Width = expandRight
            ? new GridLength(node.NodeSize)
            : new GridLength(1, GridUnitType.Star);
        columns[1].Width = expandRight
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(node.NodeSize);
        Grid.SetColumn(node.IconVisual, expandRight ? 0 : 1);
        Grid.SetColumn(node.AdjustmentDetails, expandRight ? 1 : 0);
        var scale = node.NodeSize / BaseNodeSize;
        node.AdjustmentDetails.Margin = expandRight
            ? new Thickness(3 * scale, 0, 9 * scale, 0)
            : new Thickness(9 * scale, 0, 3 * scale, 0);
    }

    private static void UpdateAdjustmentFeedback(NodeVisual node, int wheelDirection, bool animate)
    {
        if (node.Adjustment is null
            || node.AdjustmentFeedback is null
            || node.AdjustmentFeedbackChip is null)
        {
            return;
        }

        var text = AdjustmentVisualFormatter.Format(node.Adjustment, wheelDirection);
        node.AdjustmentFeedback.Text = text.FeedbackText;
        if (node.AdjustmentFeedbackChip.Background is SolidColorBrush brush)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = wheelDirection == 0
                ? Color.FromArgb(38, 255, 255, 255)
                : Color.FromArgb(72, 255, 255, 255);
        }

        if (!animate || wheelDirection == 0 || node.AdjustmentFeedbackChip.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(145))
            {
                EasingFunction = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut },
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(145))
            {
                EasingFunction = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut },
            });
    }

    private void SetNodeHover(NodeVisual node, bool hovered, bool animate)
    {
        node.IsActive = hovered;
        SetAdjustmentExpanded(node, hovered && node.Slot.Submenu is null, animate);
        var targetBackground = hovered ? node.HoverBackground : node.RestingBackground;
        var targetForeground = hovered ? node.HoverForeground : node.RestingForeground;

        animate &= AnimationsEnabled;
        if (animate && node.Bubble.Background is SolidColorBrush current && targetBackground is SolidColorBrush target)
        {
            current.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target.Color, TimeSpan.FromMilliseconds(105))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            node.Bubble.Background = CloneBrush(targetBackground);
        }
        if (node.Tail is not null)
        {
            node.Tail.Fill = node.Bubble.Background;
        }
        if (node.TailChevron is not null)
        {
            node.TailChevron.Stroke = targetForeground;
        }
        if (node.AdjustmentShadow is not null)
        {
            if (animate
                && node.AdjustmentShadow.Background is SolidColorBrush shadowCurrent
                && targetBackground is SolidColorBrush shadowTarget)
            {
                shadowCurrent.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(shadowTarget.Color, TimeSpan.FromMilliseconds(105))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    });
            }
            else
            {
                node.AdjustmentShadow.Background = CloneBrush(targetBackground);
            }
        }
        if (node.IconVisual is ActionIconView icon)
        {
            icon.Foreground = targetForeground;
            icon.Background = targetBackground;
        }
        if (node.AdjustmentName is not null)
        {
            node.AdjustmentName.Foreground = targetForeground;
        }
        if (node.AdjustmentFeedback is not null)
        {
            node.AdjustmentFeedback.Foreground = targetForeground;
        }

        if (node.Container.RenderTransform is ScaleTransform transform)
        {
            var scaleTarget = hovered && !ReduceMotion ? 1.025 : 1.0;
            if (animate && !ReduceMotion)
            {
                var animation = new DoubleAnimation(scaleTarget, TimeSpan.FromMilliseconds(115))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
            else
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleX = scaleTarget;
                transform.ScaleY = scaleTarget;
            }
        }
    }

    private bool IsNodeActive(NodeVisual node) => ReferenceEquals(SelectedSlot, node.Slot)
        || ReferenceEquals(HoveredSlot, node.Slot)
        || _openFolders.Contains(node.Slot);

    private void RefreshNodeStates(bool animate)
    {
        foreach (var node in _rootNodes.Concat(_submenuNodes))
        {
            var active = IsNodeActive(node);
            var dimmed = OpenFolder is not null
                && !ReferenceEquals(node.Slot, OpenFolder)
                && !ReferenceEquals(node.ParentFolder, OpenFolder);
            if (node.IsActive != active || !animate)
            {
                SetNodeHover(node, active, animate);
            }
            var selectedOutline = ReferenceEquals(SelectedSlot, node.Slot) && !_openFolders.Contains(node.Slot);
            node.Bubble.BorderBrush = selectedOutline
                ? ResourceBrush("AccentBrush", new SolidColorBrush(Color.FromRgb(129, 76, 255)))
                : Brushes.Transparent;
            node.Bubble.BorderThickness = new Thickness(selectedOutline ? 2 : 0);
            var opacity = dimmed ? 0.28d : 1d;
            if ((node.IsDimmed != dimmed || !animate) && (!_animateOnNextLayout || OpenFolder is not null))
            {
                node.Container.BeginAnimation(OpacityProperty, null);
                node.Container.Opacity = opacity;
            }
            node.IsDimmed = dimmed;
            var labelsVisible = InteractionMode == RingInteractionMode.Configure
                && ShowConfigurationLabels && !dimmed
                && !ReferenceEquals(node.Slot, OpenFolder);
            if (InteractionMode == RingInteractionMode.Execute)
            {
                labelsVisible = ShowTooltips && ReferenceEquals(HoveredSlot, node.Slot)
                    && node.Label.Opacity > 0 && node.Adjustment is null && !_openFolders.Contains(node.Slot);
            }
            SetLabelVisible(node, labelsVisible, animate: false);
            if (node.Tail is not null)
            {
                var visible = !_openFolders.Contains(node.Slot);
                node.Tail.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
                if (node.TailChevron is not null)
                {
                    node.TailChevron.Visibility = node.Tail.Visibility;
                }
                node.Bubble.Effect = visible ? null : TryFindResource("SoftShadow") as Effect;
            }
        }
    }

    private void CloseSubmenus(bool animate)
    {
        _submenuTimer.Stop();
        _pendingSubmenu = null;
        RemoveSubmenuVisuals();
        RefreshNodeStates(animate);
    }

    private static void SetLabelVisible(NodeVisual node, bool visible, bool animate)
    {
        var target = visible ? 1d : 0d;
        if (animate)
        {
            node.Label.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(visible ? 115 : 70)));
            return;
        }

        node.Label.BeginAnimation(OpacityProperty, null);
        node.Label.Opacity = target;
    }

    private void AnimateNodeEntrance(NodeVisual node, Point origin, Point target, int durationMs, int delayMs)
    {
        var anchorX = node.AnchorOffsetX;
        var halfHeight = node.NodeSize / 2d;
        if (ReduceMotion)
        {
            Canvas.SetLeft(node.Container, target.X - anchorX);
            Canvas.SetTop(node.Container, target.Y - halfHeight);
            node.Container.Opacity = 0;
            if (node.Container.RenderTransform is ScaleTransform reducedScale)
            {
                reducedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                reducedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                reducedScale.ScaleX = reducedScale.ScaleY = 1;
            }
            node.Container.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
            return;
        }

        Canvas.SetLeft(node.Container, origin.X - anchorX);
        Canvas.SetTop(node.Container, origin.Y - halfHeight);
        node.Container.Opacity = 0;
        var transform = (ScaleTransform)node.Container.RenderTransform;
        transform.ScaleX = transform.ScaleY = 0.38;

        var duration = TimeSpan.FromMilliseconds(durationMs);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
        var x = new DoubleAnimation(origin.X - anchorX, target.X - anchorX, duration) { BeginTime = begin, EasingFunction = ease };
        var y = new DoubleAnimation(origin.Y - halfHeight, target.Y - halfHeight, duration) { BeginTime = begin, EasingFunction = ease };
        Canvas.SetLeft(node.Container, target.X - anchorX);
        Canvas.SetTop(node.Container, target.Y - halfHeight);
        node.Container.BeginAnimation(Canvas.LeftProperty, x);
        node.Container.BeginAnimation(Canvas.TopProperty, y);
        node.Container.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = new QuadraticEase() });
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, EntranceScaleAnimation(durationMs, delayMs));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, EntranceScaleAnimation(durationMs, delayMs));
    }

    private static DoubleAnimation EntranceScaleAnimation(int durationMs, int delayMs) =>
        new(0.38, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut },
        };

    private void AnimateNodeExit(NodeVisual node, Point center, int durationMs, int delayMs)
    {
        node.Label.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(ReduceMotion ? 75 : durationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            });
        if (ReduceMotion)
        {
            node.Container.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(75))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                });
            return;
        }

        var anchorX = node.AnchorOffsetX;
        var halfHeight = node.NodeSize / 2d;
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        node.Container.BeginAnimation(
            Canvas.LeftProperty,
            new DoubleAnimation(center.X - anchorX, duration) { BeginTime = begin, EasingFunction = ease });
        node.Container.BeginAnimation(
            Canvas.TopProperty,
            new DoubleAnimation(center.Y - halfHeight, duration) { BeginTime = begin, EasingFunction = ease });
        node.Container.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, duration) { BeginTime = begin, EasingFunction = ease });
        if (node.Container.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.32, duration) { BeginTime = begin, EasingFunction = ease });
            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.32, duration) { BeginTime = begin, EasingFunction = ease });
        }
    }

    private void RemoveSubmenuVisuals(int level = 0)
    {
        StopSubmenuAnimation();
        if (_submenuBridge is not null)
        {
            RingCanvas.Children.Remove(_submenuBridge);
            _submenuBridge = null;
        }
        var removedFolders = _openFolders.Skip(level).ToHashSet();
        foreach (var node in _submenuNodes.Where(node => node.ParentFolder is null || removedFolders.Contains(node.ParentFolder)).ToArray())
        {
            RingCanvas.Children.Remove(node.Container);
            TooltipCanvas.Children.Remove(node.Label);
            _submenuNodes.Remove(node);
        }
        if (level < _openFolders.Count)
        {
            _openFolders.RemoveRange(level, _openFolders.Count - level);
        }
        OpenFolder = _openFolders.LastOrDefault();
    }

    private void StartSubmenuAnimation(
        NodeVisual parent, IReadOnlyList<NodeVisual> nodes, IReadOnlyList<Point> targets, int leadIndex)
    {
        var bridge = new ShapePath
        {
            Fill = parent.HoverBackground,
            IsHitTestVisible = false,
            Effect = TryFindResource("SoftShadow") as Effect,
        };
        Panel.SetZIndex(bridge, 0);
        RingCanvas.Children.Add(bridge);
        _submenuBridge = bridge;
        _submenuAnimation = new SubmenuAnimationState(parent, nodes, targets, leadIndex);
        var revision = ++_submenuAnimationRevision;
        RenderSubmenuFrame(0);
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Math.Clamp(SubmenuAnimationMilliseconds, 80, 1000)));
        animation.Completed += (_, _) =>
        {
            if (revision == _submenuAnimationRevision)
            {
                StopSubmenuAnimation();
            }
        };
        BeginAnimation(SubmenuProgressProperty, animation);
    }

    private void StopSubmenuAnimation()
    {
        _submenuAnimationRevision++;
        BeginAnimation(SubmenuProgressProperty, null);
        var hadAnimation = _submenuAnimation is not null;
        if (_submenuAnimation is not null)
        {
            RenderSubmenuFrame(1);
            _submenuAnimation = null;
        }
        if (_submenuBridge is not null)
        {
            RingCanvas.Children.Remove(_submenuBridge);
            _submenuBridge = null;
        }
        if (hadAnimation)
        {
            RefreshNodeStates(animate: false);
        }
    }

    private void RenderSubmenuFrame(double progress)
    {
        if (_submenuAnimation is not { } state)
        {
            return;
        }
        progress = Math.Clamp(progress, 0, 1);
        var parent = state.Parent;
        var parentScale = parent.Container.RenderTransform is ScaleTransform parentTransform ? parentTransform.ScaleX : 1d;
        var parentRadius = parent.NodeSize / 2d * parentScale;
        var lead = state.Nodes[state.LeadIndex];
        var leadTarget = state.Targets[state.LeadIndex];
        var direction = new Vector(Math.Cos(parent.Angle), Math.Sin(parent.Angle));
        var origin = parent.Center + direction * (parentRadius * 1.05);
        var travel = RingSubmenuMotion.Ease(progress / 0.76);
        var leadCenter = RingSubmenuMotion.Lerp(origin, leadTarget, travel);
        var growth = RingSubmenuMotion.Ease(progress / 0.61);
        var leadRadius = lead.NodeSize / 2d * (0.25 + 0.75 * growth);
        var distance = (leadCenter - parent.Center).Length;
        var stretch = distance - parentRadius - leadRadius;
        var strength = 1 - RingSubmenuMotion.Ease(Math.Max(0, stretch) / (23 * RingScale));
        if (_submenuBridge is not null)
        {
            if (strength > 0.001)
            {
                _submenuBridge.Data = RingSubmenuMotion.Surface(parent.Center, parentRadius, leadCenter, leadRadius, strength);
            }
            else
            {
                _submenuBridge.Data = Geometry.Empty;
            }
        }
        parent.Bubble.Effect = strength > 0.001 ? null : TryFindResource("SoftShadow") as Effect;

        for (var index = 0; index < state.Nodes.Count; index++)
        {
            var node = state.Nodes[index];
            Point point;
            double scale;
            double opacity;
            if (index == state.LeadIndex)
            {
                point = leadCenter;
                scale = leadRadius / (node.NodeSize / 2d);
                opacity = 1;
                var colorProgress = RingSubmenuMotion.Ease((progress - 0.47) / 0.33);
                node.Bubble.Background = InterpolateBrush(parent.HoverBackground, node.RestingBackground, colorProgress);
                if (node.IconVisual is ActionIconView icon)
                {
                    icon.Foreground = InterpolateBrush(parent.HoverForeground, node.RestingForeground, colorProgress);
                    icon.Background = node.Bubble.Background;
                }
                node.Bubble.Effect = strength > 0.001 ? null : TryFindResource("SoftShadow") as Effect;
                node.IconVisual.Opacity = RingSubmenuMotion.Ease((progress - 0.20) / 0.26);
            }
            else
            {
                var delay = 0.32 + Math.Abs(index - state.LeadIndex) * 0.035;
                var stage = RingSubmenuMotion.Ease((progress - delay) / (1 - delay));
                point = RingSubmenuMotion.Lerp(RingSubmenuMotion.Lerp(leadTarget, state.Targets[index], 0.78), state.Targets[index], stage);
                scale = 0.12 + 0.88 * stage;
                opacity = Math.Min(1, stage * 3);
            }
            Canvas.SetLeft(node.Container, point.X - node.AnchorOffsetX);
            Canvas.SetTop(node.Container, point.Y - node.NodeSize / 2d);
            node.Container.Opacity = opacity;
            if (node.Container.RenderTransform is ScaleTransform transform)
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleX = transform.ScaleY = scale;
            }
            if (InteractionMode == RingInteractionMode.Configure)
            {
                SetLabelVisible(node, progress >= 0.98 && ShowConfigurationLabels, animate: false);
            }
        }
    }

    private static Brush InterpolateBrush(Brush from, Brush to, double progress)
    {
        if (from is not SolidColorBrush left || to is not SolidColorBrush right)
        {
            return progress < 0.5 ? from : to;
        }
        static byte Mix(byte a, byte b, double p) => (byte)Math.Round(a + (b - a) * p);
        return new SolidColorBrush(Color.FromArgb(
            Mix(left.Color.A, right.Color.A, progress), Mix(left.Color.R, right.Color.R, progress),
            Mix(left.Color.G, right.Color.G, progress), Mix(left.Color.B, right.Color.B, progress)));
    }

    private void RepositionWithoutAnimation()
    {
        if (_animateOnNextLayout)
        {
            Rebuild(animate: true);
            return;
        }
        if (_ring is null)
        {
            return;
        }
        if (_rootNodes.Count == 0)
        {
            Rebuild(animate: false);
            return;
        }

        var center = EffectiveCenter();
        if (_centerVisual is not null)
        {
            Canvas.SetLeft(_centerVisual, center.X - _centerVisual.Width / 2d);
            Canvas.SetTop(_centerVisual, center.Y - _centerVisual.Height / 2d);
        }
        var targets = RingLayoutGeometry.ArrangeRoot(
            center,
            new Size(ActualWidth, ActualHeight),
            _rootNodes.Count,
            BaseNodeSize * RingScale,
            BaseRadius * RingScale,
            10d * RingScale);
        for (var index = 0; index < _rootNodes.Count; index++)
        {
            PositionNode(_rootNodes[index], targets[index], center);
        }
        if (OpenFolder is not null)
        {
            var path = _openFolders.ToArray();
            RemoveSubmenuVisuals();
            foreach (var folder in path)
            {
                OpenSubmenu(folder, animate: false);
            }
        }
    }

    private void PositionNode(NodeVisual node, Point point, Point center)
    {
        node.Center = point;
        node.Angle = AngleBetween(center, point);
        if (node.Adjustment is not null)
        {
            var preferredWidth = AdjustmentVisualFormatter.PreferredWidth(node.Adjustment.Parameter) * RingScale;
            var inset = 6d * RingScale;
            var nodeLeft = point.X - node.NodeSize / 2d;
            var nodeRight = point.X + node.NodeSize / 2d;
            var rightCapacity = Math.Max(node.NodeSize, ActualWidth - inset - nodeLeft);
            var leftCapacity = Math.Max(node.NodeSize, nodeRight - inset);
            var preferRight = Math.Cos(node.Angle) >= 0;
            node.ExpandsRight = preferRight switch
            {
                true when rightCapacity >= preferredWidth => true,
                false when leftCapacity >= preferredWidth => false,
                _ when rightCapacity >= preferredWidth => true,
                _ when leftCapacity >= preferredWidth => false,
                _ => rightCapacity >= leftCapacity,
            };
            var availableWidth = node.ExpandsRight ? rightCapacity : leftCapacity;
            node.Container.Width = Math.Clamp(preferredWidth, node.NodeSize, availableWidth);
            node.AnchorOffsetX = node.ExpandsRight
                ? node.NodeSize / 2d
                : node.Container.Width - node.NodeSize / 2d;
            node.Container.RenderTransformOrigin = new Point(
                node.Container.Width > 0 ? node.AnchorOffsetX / node.Container.Width : 0.5,
                0.5);
            ConfigureAdjustmentDirection(node, node.ExpandsRight);
            SetAdjustmentExpanded(node, node.IsAdjustmentExpanded, animate: false);
        }
        else
        {
            node.AnchorOffsetX = node.NodeSize / 2d;
        }

        var left = point.X - node.AnchorOffsetX;
        var top = point.Y - node.NodeSize / 2d;
        Canvas.SetLeft(node.Container, left);
        Canvas.SetTop(node.Container, top);

        PositionLabel(node, point);

        if (node.Tail is not null)
        {
            var localCenter = new Point(node.AnchorOffsetX, node.NodeSize / 2d);
            node.Tail.Data = RingSubmenuMotion.AttachedSurface(localCenter, node.NodeSize / 2d, node.Angle);
            if (node.TailChevron is not null)
            {
                var direction = new Vector(Math.Cos(node.Angle), Math.Sin(node.Angle));
                var position = localCenter + direction * (node.NodeSize / 2d + 2.6 * RingScale);
                var transforms = new TransformGroup();
                transforms.Children.Add(new ScaleTransform(RingScale, RingScale));
                transforms.Children.Add(new RotateTransform(node.Angle * 180 / Math.PI));
                transforms.Children.Add(new TranslateTransform(position.X, position.Y));
                node.TailChevron.RenderTransform = transforms;
            }
        }
    }

    private void PositionLabel(NodeVisual node, Point nodeCenter)
    {
        var bounds = EffectiveTooltipBounds();
        var margin = Math.Max(14d, 8d * RingScale);
        // Keep the tooltip clear of both the bubble and its soft shadow.  The larger
        // floor is especially important for long labels on the inward-facing nodes,
        // where a clamped label used to appear underneath the bubble.
        var gap = Math.Max(22d, 14d * RingScale);
        if (node.Label.Child is TextBlock text)
        {
            var longestWordLength = text.Text
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length)
                .DefaultIfEmpty(0)
                .Max();
            var minimumTextWidth = Math.Max(
                80d * Math.Min(RingScale, 1.25d),
                longestWordLength * text.FontSize * 0.58d);
            var availableTextWidth = Math.Max(
                minimumTextWidth,
                bounds.Width - margin * 2d - node.Label.Padding.Left - node.Label.Padding.Right);
            var maximumTextWidth = Math.Min(360d * RingScale, availableTextWidth);
            var preferredSide = RingTooltipGeometry.GetPreferredSide(node.Angle);
            var horizontalCapacity = preferredSide switch
            {
                RingTooltipSide.Right => bounds.Right - margin - (nodeCenter.X + node.NodeSize / 2d + gap),
                RingTooltipSide.Left => nodeCenter.X - node.NodeSize / 2d - gap - (bounds.Left + margin),
                _ => 0d,
            };
            var sideTextCapacity = horizontalCapacity - node.Label.Padding.Left - node.Label.Padding.Right;
            if (sideTextCapacity >= minimumTextWidth)
            {
                maximumTextWidth = Math.Min(maximumTextWidth, sideTextCapacity);
            }
            text.MaxWidth = maximumTextWidth;
            text.TextTrimming = TextTrimming.None;
            text.TextWrapping = TextWrapping.Wrap;
        }

        node.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = node.Label.DesiredSize;
        var placement = RingTooltipGeometry.Place(
            nodeCenter,
            node.NodeSize,
            desired,
            bounds,
            node.Angle,
            gap,
            margin);
        Canvas.SetLeft(node.Label, placement.TopLeft.X);
        Canvas.SetTop(node.Label, placement.TopLeft.Y);
    }

    private Rect EffectiveTooltipBounds()
    {
        var canvas = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        if (_tooltipBounds.IsEmpty
            || !double.IsFinite(_tooltipBounds.X)
            || !double.IsFinite(_tooltipBounds.Y)
            || !double.IsFinite(_tooltipBounds.Width)
            || !double.IsFinite(_tooltipBounds.Height))
        {
            return canvas;
        }

        var bounds = Rect.Intersect(canvas, _tooltipBounds);
        return bounds.IsEmpty ? canvas : bounds;
    }

    private Point EffectiveCenter() => new(
        double.IsNaN(RingCenter.X) ? ActualWidth / 2d : RingCenter.X,
        double.IsNaN(RingCenter.Y) ? ActualHeight / 2d : RingCenter.Y);

    private static Point NodeCenter(NodeVisual node) => node.Center;

    private static double AngleBetween(Point center, Point point) =>
        Math.Atan2(point.Y - center.Y, point.X - center.X);

    private static string ValidColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        try
        {
            _ = ColorConverter.ConvertFromString(value);
            return value!;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            return fallback;
        }
    }

    private static Color ParseColor(string value) => (Color)ColorConverter.ConvertFromString(value)!;

    private Brush ResourceBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private Brush SlotBrush(string? value, string resourceKey, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ResourceBrush(resourceKey, fallback);
        }
        try
        {
            return new SolidColorBrush(ParseColor(value));
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            return ResourceBrush(resourceKey, fallback);
        }
    }

    private static Brush CloneBrush(Brush source)
    {
        var clone = source.CloneCurrentValue();
        if (clone.CanFreeze)
        {
            // Animated hover brushes must remain mutable.
        }
        return clone;
    }

    private sealed class NodeVisual(
        RingSlotDefinition slot,
        Canvas container,
        Border bubble,
        FrameworkElement iconVisual,
        Border label,
        ShapePath? tail,
        ShapePath? tailChevron,
        double angle,
        bool isSubmenu,
        double nodeSize,
        AdjustParameterAction? adjustment,
        Grid? adjustmentContent,
        Grid? adjustmentDetails,
        TextBlock? adjustmentName,
        Border? adjustmentFeedbackChip,
        TextBlock? adjustmentFeedback,
        Border? adjustmentShadow,
        Brush restingBackground,
        Brush restingForeground,
        Brush hoverBackground,
        Brush hoverForeground)
    {
        public RingSlotDefinition Slot { get; } = slot;
        public Canvas Container { get; } = container;
        public Border Bubble { get; } = bubble;
        public FrameworkElement IconVisual { get; } = iconVisual;
        public Border Label { get; } = label;
        public ShapePath? Tail { get; } = tail;
        public ShapePath? TailChevron { get; } = tailChevron;
        public RingSlotDefinition? ParentFolder { get; set; }
        public double Angle { get; set; } = angle;
        public bool IsSubmenu { get; } = isSubmenu;
        public double NodeSize { get; } = nodeSize;
        public AdjustParameterAction? Adjustment { get; } = adjustment;
        public Grid? AdjustmentContent { get; } = adjustmentContent;
        public Grid? AdjustmentDetails { get; } = adjustmentDetails;
        public TextBlock? AdjustmentName { get; } = adjustmentName;
        public Border? AdjustmentFeedbackChip { get; } = adjustmentFeedbackChip;
        public TextBlock? AdjustmentFeedback { get; } = adjustmentFeedback;
        public Border? AdjustmentShadow { get; } = adjustmentShadow;
        public Brush RestingBackground { get; } = restingBackground;
        public Brush RestingForeground { get; } = restingForeground;
        public Brush HoverBackground { get; } = hoverBackground;
        public Brush HoverForeground { get; } = hoverForeground;
        public bool IsAdjustmentExpanded { get; set; }
        public bool ExpandsRight { get; set; } = true;
        public double AnchorOffsetX { get; set; } = nodeSize / 2d;
        public Point Center { get; set; }
        public int RestingZIndex { get; set; }
        public bool IsActive { get; set; }
        public bool IsDimmed { get; set; }
    }

    private sealed record SubmenuAnimationState(
        NodeVisual Parent, IReadOnlyList<NodeVisual> Nodes, IReadOnlyList<Point> Targets, int LeadIndex);
}

public enum RingTooltipSide
{
    Right,
    Bottom,
    Left,
    Top,
}

public readonly record struct RingTooltipPlacement(Point TopLeft, RingTooltipSide Side);

/// <summary>Chooses a readable tooltip side without moving the label through its bubble.</summary>
public static class RingTooltipGeometry
{
    private const double HorizontalDirectionThreshold = 0.56d;

    public static RingTooltipPlacement Place(
        Point nodeCenter,
        double nodeDiameter,
        Size labelSize,
        Rect bounds,
        double angle,
        double gap,
        double margin)
    {
        nodeDiameter = Math.Max(0, nodeDiameter);
        labelSize = new Size(Math.Max(0, labelSize.Width), Math.Max(0, labelSize.Height));
        gap = Math.Max(0, gap);
        margin = Math.Max(0, margin);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new RingTooltipPlacement(nodeCenter, GetPreferredSide(angle));
        }

        var preferred = GetPreferredSide(angle);
        var secondary = SecondaryOutwardSide(angle, preferred);
        var candidates = UniqueSides(preferred, secondary, Opposite(secondary), Opposite(preferred));
        var nodeBounds = new Rect(
            nodeCenter.X - nodeDiameter / 2d,
            nodeCenter.Y - nodeDiameter / 2d,
            nodeDiameter,
            nodeDiameter);
        RingTooltipPlacement? best = null;
        var bestOverlap = double.PositiveInfinity;
        foreach (var side in candidates)
        {
            var desired = DesiredTopLeft(nodeCenter, nodeDiameter, labelSize, side, gap);
            var clamped = ClampToBounds(desired, labelSize, bounds, margin);
            var labelBounds = new Rect(clamped, labelSize);
            var overlap = IntersectionArea(labelBounds, nodeBounds);
            if (overlap <= 0.001d)
            {
                return new RingTooltipPlacement(clamped, side);
            }
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                best = new RingTooltipPlacement(clamped, side);
            }
        }

        return best ?? new RingTooltipPlacement(
            ClampToBounds(nodeCenter, labelSize, bounds, margin),
            preferred);
    }

    public static RingTooltipSide GetPreferredSide(double angle)
    {
        var x = Math.Cos(angle);
        var y = Math.Sin(angle);
        if (Math.Abs(x) > HorizontalDirectionThreshold)
        {
            return x >= 0 ? RingTooltipSide.Right : RingTooltipSide.Left;
        }
        return y >= 0 ? RingTooltipSide.Bottom : RingTooltipSide.Top;
    }

    private static RingTooltipSide SecondaryOutwardSide(double angle, RingTooltipSide primary)
    {
        var x = Math.Cos(angle);
        var y = Math.Sin(angle);
        return primary is RingTooltipSide.Left or RingTooltipSide.Right
            ? y >= 0 ? RingTooltipSide.Bottom : RingTooltipSide.Top
            : x >= 0 ? RingTooltipSide.Right : RingTooltipSide.Left;
    }

    private static IReadOnlyList<RingTooltipSide> UniqueSides(params RingTooltipSide[] sides)
    {
        var result = new List<RingTooltipSide>(4);
        foreach (var side in sides)
        {
            if (!result.Contains(side))
            {
                result.Add(side);
            }
        }
        return result;
    }

    private static RingTooltipSide Opposite(RingTooltipSide side) => side switch
    {
        RingTooltipSide.Right => RingTooltipSide.Left,
        RingTooltipSide.Left => RingTooltipSide.Right,
        RingTooltipSide.Top => RingTooltipSide.Bottom,
        _ => RingTooltipSide.Top,
    };

    private static Point DesiredTopLeft(
        Point center,
        double nodeDiameter,
        Size label,
        RingTooltipSide side,
        double gap)
    {
        var radius = nodeDiameter / 2d;
        return side switch
        {
            RingTooltipSide.Right => new Point(center.X + radius + gap, center.Y - label.Height / 2d),
            RingTooltipSide.Left => new Point(center.X - radius - gap - label.Width, center.Y - label.Height / 2d),
            RingTooltipSide.Bottom => new Point(center.X - label.Width / 2d, center.Y + radius + gap),
            _ => new Point(center.X - label.Width / 2d, center.Y - radius - gap - label.Height),
        };
    }

    private static Point ClampToBounds(Point desired, Size element, Rect bounds, double margin)
    {
        var minX = bounds.Left + Math.Min(margin, bounds.Width / 2d);
        var minY = bounds.Top + Math.Min(margin, bounds.Height / 2d);
        var maxX = Math.Max(minX, bounds.Right - margin - element.Width);
        var maxY = Math.Max(minY, bounds.Bottom - margin - element.Height);
        return new Point(
            Math.Clamp(desired.X, minX, maxX),
            Math.Clamp(desired.Y, minY, maxY));
    }

    private static double IntersectionArea(Rect left, Rect right)
    {
        var intersection = Rect.Intersect(left, right);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
}

public readonly record struct AdjustmentVisualText(string ParameterLabel, string FeedbackText);

public static class AdjustmentVisualFormatter
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static AdjustmentVisualText Format(AdjustParameterAction adjustment, int wheelDirection)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        var parameterLabel = adjustment.Parameter switch
        {
            AdjustableParameter.SystemVolume => "Громкость",
            AdjustableParameter.ScreenBrightness => "Яркость",
            AdjustableParameter.Zoom => "Масштаб",
            AdjustableParameter.VerticalScroll => "Прокрутка ↑↓",
            AdjustableParameter.HorizontalScroll => "Прокрутка ←→",
            _ => "Параметр",
        };
        var direction = Math.Sign(wheelDirection);
        var directionGlyph = adjustment.Parameter == AdjustableParameter.HorizontalScroll
            ? direction switch { > 0 => "→", < 0 => "←", _ => "↔" }
            : direction switch { > 0 => "↑", < 0 => "↓", _ => "↕" };
        var magnitude = double.IsFinite(adjustment.Value) ? Math.Abs(adjustment.Value) : 0;
        var number = magnitude.ToString("0.##", RussianCulture);
        var suffix = adjustment.Parameter is AdjustableParameter.SystemVolume or AdjustableParameter.ScreenBrightness
            ? "%"
            : string.Empty;
        var sign = direction switch
        {
            > 0 => "+",
            < 0 => "−",
            _ => "±",
        };

        return new AdjustmentVisualText(parameterLabel, $"{directionGlyph} {sign}{number}{suffix}");
    }

    public static double PreferredWidth(AdjustableParameter parameter) => parameter switch
    {
        AdjustableParameter.VerticalScroll or AdjustableParameter.HorizontalScroll => 220,
        _ => 196,
    };
}

internal static class IconGlyphs
{
    public static bool UsesTextFont(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim().ToLowerInvariant();
        return normalized.Length <= 3
               && normalized.All(char.IsLetterOrDigit)
               && normalized is not ("app" or "cut" or "url" or "web");
    }

    public static string For(RingSlotDefinition slot)
    {
        if (slot.Submenu is not null)
        {
            return "\uE8B7"; // folder
        }
        if (!string.IsNullOrWhiteSpace(slot.Icon))
        {
            return FromKey(slot.Icon);
        }
        return slot.Action?.Kind switch
        {
            ActionKind.KeyboardShortcut => "\uE765",
            ActionKind.LaunchApplication => "\uE8A7",
            ActionKind.OpenUri => "\uE71B",
            ActionKind.TypeText => "\uE8D2",
            ActionKind.MouseInput => "\uE962",
            ActionKind.Sequence => "\uE8FD",
            ActionKind.AdjustParameter => "\uE9E9",
            ActionKind.BuiltIn => FromBuiltIn(slot.Action.BuiltIn?.Command ?? BuiltInCommand.None),
            _ => "\uE710",
        };
    }

    public static string FromKey(string key) => key.Trim().ToLowerInvariant() switch
    {
        "copy" => "\uE8C8",
        "paste" => "\uE77F",
        "cut" => "\uE8C6",
        "undo" => "\uE7A7",
        "redo" => "\uE7A6",
        "search" => "\uE721",
        "browser" or "web" or "url" => "\uE774",
        "folder" => "\uE8B7",
        "app" => "\uE8A7",
        "keyboard" => "\uE765",
        "mouse" => "\uE962",
        "play" or "media" => "\uE768",
        "volume" => "\uE767",
        "mute" => "\uE74F",
        "screenshot" => "\uE722",
        "desktop" => "\uE7F4",
        "window" => "\uE737",
        "settings" => "\uE713",
        "lock" => "\uE72E",
        "text" => "\uE8D2",
        "multi" => "\uE8FD",
        _ when key.Length <= 3 => key.ToUpperInvariant(),
        _ => "\uE945",
    };

    private static string FromBuiltIn(BuiltInCommand command) => command switch
    {
        BuiltInCommand.Back => "\uE72B",
        BuiltInCommand.Forward => "\uE72A",
        BuiltInCommand.Copy => "\uE8C8",
        BuiltInCommand.Paste => "\uE77F",
        BuiltInCommand.Cut => "\uE8C6",
        BuiltInCommand.Undo => "\uE7A7",
        BuiltInCommand.Redo => "\uE7A6",
        BuiltInCommand.Find or BuiltInCommand.OpenSearch => "\uE721",
        BuiltInCommand.VolumeUp or BuiltInCommand.VolumeDown => "\uE767",
        BuiltInCommand.VolumeMute => "\uE74F",
        BuiltInCommand.MediaPlayPause => "\uE768",
        BuiltInCommand.MediaNext => "\uE893",
        BuiltInCommand.MediaPrevious => "\uE892",
        BuiltInCommand.MediaStop => "\uE71A",
        BuiltInCommand.TakeScreenshot => "\uE722",
        BuiltInCommand.LockWorkstation => "\uE72E",
        BuiltInCommand.OpenFileExplorer => "\uE8B7",
        BuiltInCommand.OpenSettings => "\uE713",
        BuiltInCommand.ShowDesktop => "\uE7F4",
        BuiltInCommand.TaskView or BuiltInCommand.SwitchWindow => "\uE7C4",
        BuiltInCommand.CloseWindow => "\uE8BB",
        BuiltInCommand.MinimizeWindow => "\uE921",
        BuiltInCommand.MaximizeOrRestoreWindow => "\uE922",
        BuiltInCommand.InsertDate or BuiltInCommand.InsertDayOfYear or BuiltInCommand.InsertWeekNumber => "\uE787",
        BuiltInCommand.InsertTime => "\uE823",
        BuiltInCommand.InsertDateTime => "\uE787",
        BuiltInCommand.InsertMoonPhase => "\uE708",
        BuiltInCommand.ClearClipboard => "\uE74D",
        BuiltInCommand.PasteClipboardPlainText => "\uE77F",
        BuiltInCommand.ClipboardLowercase
            or BuiltInCommand.ClipboardSentenceCase
            or BuiltInCommand.ClipboardTitleCase
            or BuiltInCommand.ClipboardUppercase
            or BuiltInCommand.ClipboardToggleCase => "\uE8D2",
        BuiltInCommand.ClipboardUrlEncode or BuiltInCommand.ClipboardUrlDecode => "\uE774",
        BuiltInCommand.ClipboardHtmlEncode or BuiltInCommand.ClipboardHtmlDecode => "\uE943",
        _ => "\uE945",
    };
}
