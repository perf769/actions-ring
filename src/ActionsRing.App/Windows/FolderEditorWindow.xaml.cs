using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Windows;

public partial class FolderEditorWindow : Window
{
    private readonly RingSlotDefinition _target;
    private readonly Func<CancellationToken, Task<KeyChord>> _captureShortcut;
    private readonly ActionCatalogContext? _catalogContext;
    private ActionDefinition? _clickAction;
    private readonly RingDefinition _draftOrder;
    private RingSlotDefinition? _dragChild;
    private Point _childDragStart;

    public FolderEditorWindow(
        RingSlotDefinition target,
        Func<CancellationToken, Task<KeyChord>>? captureShortcut = null,
        ActionCatalogContext? catalogContext = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Submenu is null)
        {
            throw new ArgumentException("The selected slot does not contain a submenu.", nameof(target));
        }

        _target = target;
        _draftOrder = new RingDefinition { SlotCount = target.Submenu.SlotCount, Slots = [.. target.Submenu.Slots] };
        _catalogContext = catalogContext;
        _clickAction = target.Action?.Kind is null or ActionKind.None ? null : Clone(target.Action);
        _captureShortcut = captureShortcut ?? (_ => Task.FromCanceled<KeyChord>(new CancellationToken(canceled: true)));
        InitializeComponent();
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        NameBox.Text = target.Submenu.Name;
        SlotCountSlider.Value = target.Submenu.SlotCount;
        SlotCountText.Text = target.Submenu.SlotCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        RefreshClickAction();
        RefreshChildOrder();
        QueryContinueDrag += (_, args) =>
        {
            if (args.EscapePressed)
            {
                args.Action = DragAction.Cancel;
                ClearChildDragFeedback();
                args.Handled = true;
            }
        };
        ApplyResponsiveLayout();
    }

    private void RefreshChildOrder(RingSlotDefinition? selected = null)
    {
        ChildOrderList.Items.Clear();
        for (var index = 0; index < _draftOrder.Slots.Count; index++)
        {
            var slot = _draftOrder.Slots[index];
            var icon = new ActionIconView { Width = 24, Height = 24, Margin = new Thickness(10, 0, 12, 0) };
            icon.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            icon.SetIcon(slot.Icon, slot.Action);
            var row = new DockPanel();
            var number = new TextBlock { Text = (index + 1).ToString(), Width = 18, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            DockPanel.SetDock(number, Dock.Left);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(number);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = slot.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var dropFrame = new Border { BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(7), Child = row };
            var item = new ListBoxItem
            {
                Tag = slot, Content = dropFrame, AllowDrop = true, Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsSelected = ReferenceEquals(slot, selected),
            };
            item.PreviewMouseLeftButtonDown += (_, args) => { _dragChild = slot; _childDragStart = args.GetPosition(ChildOrderList); };
            item.PreviewMouseLeftButtonUp += (_, _) => _dragChild = null;
            item.PreviewMouseMove += (_, args) =>
            {
                if (args.LeftButton != MouseButtonState.Pressed) { _dragChild = null; return; }
                if (!ReferenceEquals(_dragChild, slot) || !RingSlotDrag.ExceedsThreshold(_childDragStart, args.GetPosition(ChildOrderList))) return;
                _dragChild = null;
                try { DragDrop.DoDragDrop(item, new DataObject(RingSlotDrag.Format, new RingSlotDrag(_draftOrder, slot)), DragDropEffects.Move); }
                finally { ClearChildDragFeedback(); }
                args.Handled = true;
            };
            void PreviewDrop(DragEventArgs args)
            {
                args.Effects = CanDropChild(args.Data, slot) ? DragDropEffects.Move & args.AllowedEffects : DragDropEffects.None;
                dropFrame.BorderBrush = args.Effects == DragDropEffects.Move ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
                args.Handled = true;
            }
            item.DragEnter += (_, args) => PreviewDrop(args);
            item.DragOver += (_, args) => PreviewDrop(args);
            item.DragLeave += (_, args) => { ClearChildDragFeedback(); args.Handled = true; };
            item.Drop += (_, args) =>
            {
                args.Effects = DragDropEffects.None;
                if ((args.AllowedEffects & DragDropEffects.Move) != 0 && CanDropChild(args.Data, slot)
                    && args.Data.GetData(RingSlotDrag.Format) is RingSlotDrag move && SwapChildSlots(move.Source, slot))
                    args.Effects = DragDropEffects.Move;
                ClearChildDragFeedback();
                args.Handled = true;
            };
            ChildOrderList.Items.Add(item);
        }
        UpdateOrderButtons();
    }

    private bool CanDropChild(IDataObject data, RingSlotDefinition target) =>
        data.GetData(RingSlotDrag.Format) is RingSlotDrag move && ReferenceEquals(move.Root, _draftOrder)
            && RingSlotEditing.CanSwap(_draftOrder, move.Source, target);

    internal bool SwapChildSlots(RingSlotDefinition source, RingSlotDefinition target)
    {
        if (!_draftOrder.Slots.Contains(source) || !_draftOrder.Slots.Contains(target)) return false;
        if (!RingSlotEditing.TrySwap(_draftOrder, source, target)) return false;
        RefreshChildOrder(source);
        return true;
    }

    internal void CommitChildOrder() => _target.Submenu!.Slots = [.. _draftOrder.Slots];

    private void ClearChildDragFeedback()
    {
        _dragChild = null;
        foreach (var item in ChildOrderList.Items.OfType<ListBoxItem>())
        {
            if (item.Content is Border dropFrame) dropFrame.BorderBrush = Brushes.Transparent;
        }
    }

    private void OnChildSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateOrderButtons();
    private void UpdateOrderButtons()
    {
        if (MoveChildUpButton is null || MoveChildDownButton is null) return;
        MoveChildUpButton.IsEnabled = ChildOrderList.SelectedIndex > 0;
        MoveChildDownButton.IsEnabled = ChildOrderList.SelectedIndex >= 0 && ChildOrderList.SelectedIndex < _draftOrder.Slots.Count - 1;
    }
    private void OnMoveChildUp(object sender, RoutedEventArgs args) => MoveChild(-1);
    private void OnMoveChildDown(object sender, RoutedEventArgs args) => MoveChild(1);
    private void MoveChild(int offset)
    {
        var index = ChildOrderList.SelectedIndex;
        var targetIndex = index + offset;
        if (index >= 0 && targetIndex >= 0 && targetIndex < _draftOrder.Slots.Count)
            SwapChildSlots(_draftOrder.Slots[index], _draftOrder.Slots[targetIndex]);
    }

    private void FitToMonitorWorkArea()
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var workArea = System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(workArea.Left, workArea.Top));
        var bottomRight = fromDevice.Transform(new Point(workArea.Right, workArea.Bottom));
        var bounds = WindowPlacementCalculator.FitCentered(
            Width,
            Height,
            MinWidth,
            MinHeight,
            new Rect(topLeft, bottomRight));

        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private void ApplyResponsiveLayout()
    {
        var availableHeight = ActualHeight > 0 ? ActualHeight : Height;
        var compact = availableHeight < 410;
        LayoutRoot.Margin = compact ? new Thickness(18) : new Thickness(28);
        FolderContentScroller.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0, 22, 0, 0);
        FolderCard.Padding = compact ? new Thickness(14) : new Thickness(20);
        SlotCountPanel.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0, 22, 0, 0);
        SlotCountSlider.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0, 14, 0, 0);
        FooterPanel.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0, 20, 0, 0);
    }

    private void OnSlotCountChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SlotCountText is not null)
        {
            SlotCountText.Text = Math.Round(e.NewValue).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfigureClickAction(object sender, RoutedEventArgs e)
    {
        if (_clickAction is null)
        {
            return;
        }

        var editor = new ActionEditorWindow(_clickAction, _captureShortcut) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } result)
        {
            _clickAction = result;
            RefreshClickAction();
        }
    }

    private void OnChooseClickAction(object sender, RoutedEventArgs e)
    {
        var picker = new ActionPickerWindow(_catalogContext) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedItem is not { } item
            || item.CreateSlot().Action is not { } action)
        {
            return;
        }

        if (item.RequiresConfiguration)
        {
            var editor = new ActionEditorWindow(action, _captureShortcut) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result is not { } configured)
            {
                return;
            }
            action = configured;
        }

        _clickAction = Clone(action);
        RefreshClickAction();
    }

    private void OnRemoveClickAction(object sender, RoutedEventArgs e)
    {
        _clickAction = null;
        RefreshClickAction();
    }

    private void RefreshClickAction()
    {
        ClickActionName.Text = _clickAction?.Name ?? "Не назначено";
        ClickActionDescription.Text = _clickAction is null
            ? "Пузырь только открывает подменю."
            : new[] { _clickAction.Description, _clickAction.LaunchApplication?.ExecutablePath, _clickAction.OpenUri?.Uri }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Выполняется при нажатии на пузырь.";
        ClickActionIcon.SetIcon(_clickAction is null ? "lucide:mouse-pointer-2" : _clickAction.Icon, _clickAction);
        ChooseClickActionButton.Content = _clickAction is null ? "Выбрать действие" : "Заменить";
        ConfigureClickActionButton.Visibility = _clickAction is null ? Visibility.Collapsed : Visibility.Visible;
        RemoveClickActionButton.Visibility = _clickAction is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Введите название папки.", "Actions Ring", MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }

        var submenu = _target.Submenu!;
        var requestedCount = Math.Clamp(
            (int)Math.Round(SlotCountSlider.Value),
            RingDefinition.MinimumSubmenuSlots,
            RingDefinition.MaximumSubmenuSlots);
        if (requestedCount < _draftOrder.Slots.Count && _draftOrder.Slots.Skip(requestedCount).Any(IsConfigured))
        {
            var answer = MessageBox.Show(
                this,
                "В удаляемых позициях есть назначенные действия. Продолжить?",
                "Уменьшить папку",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        CommitChildOrder();
        while (submenu.Slots.Count < requestedCount)
        {
            submenu.Slots.Add(RingSlotDefinition.Empty(submenu.Slots.Count));
        }
        if (submenu.Slots.Count > requestedCount)
        {
            submenu.Slots.RemoveRange(requestedCount, submenu.Slots.Count - requestedCount);
        }

        submenu.Name = name;
        submenu.SlotCount = requestedCount;
        _target.Label = name;
        _target.Action = _clickAction;
        _target.Icon ??= _clickAction?.Icon ?? "folder";
        DialogResult = true;
    }

    private static bool IsConfigured(RingSlotDefinition slot) =>
        slot.Submenu is not null || slot.Action?.Kind is not (null or ActionKind.None);

    private static ActionDefinition Clone(ActionDefinition action) =>
        JsonSerializer.Deserialize<ActionDefinition>(JsonSerializer.Serialize(action, ConfigurationJson.Options), ConfigurationJson.Options)!;
}
