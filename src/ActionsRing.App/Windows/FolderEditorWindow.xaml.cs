using System.Windows;
using System.Windows.Interop;
using System.Text.Json;
using System.Windows.Controls;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;

namespace ActionsRing.App.Windows;

public partial class FolderEditorWindow : Window
{
    private readonly RingSlotDefinition _target;
    private readonly Func<CancellationToken, Task<KeyChord>> _captureShortcut;
    private ActionDefinition? _clickAction;

    public FolderEditorWindow(
        RingSlotDefinition target,
        Func<CancellationToken, Task<KeyChord>>? captureShortcut = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Submenu is null)
        {
            throw new ArgumentException("The selected slot does not contain a submenu.", nameof(target));
        }

        _target = target;
        _clickAction = target.Action?.Kind is null or ActionKind.None ? null : Clone(target.Action);
        _captureShortcut = captureShortcut ?? (_ => Task.FromCanceled<KeyChord>(new CancellationToken(canceled: true)));
        InitializeComponent();
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        NameBox.Text = target.Submenu.Name;
        SlotCountSlider.Value = target.Submenu.SlotCount;
        SlotCountText.Text = target.Submenu.SlotCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        ClickActionChoice.ItemsSource = ActionCatalog.Groups
            .SelectMany(group => group.Items
                .Where(item => item.Kind == CatalogItemKind.Action)
                .Select(item => new ClickActionOption(group.Title, item)))
            .ToArray();
        RefreshClickAction();
        ApplyResponsiveLayout();
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
        var action = (ClickActionChoice.SelectedItem as ClickActionOption)?.Item.CreateSlot().Action
                     ?? _clickAction;
        if (action is null)
        {
            ClickActionChoice.IsDropDownOpen = true;
            return;
        }

        var editor = new ActionEditorWindow(action, _captureShortcut) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } result)
        {
            _clickAction = result;
            ClickActionChoice.SelectedIndex = -1;
            RefreshClickAction();
        }
    }

    private void OnRemoveClickAction(object sender, RoutedEventArgs e)
    {
        _clickAction = null;
        ClickActionChoice.SelectedIndex = -1;
        RefreshClickAction();
    }

    private void OnClickActionChoiceChanged(object sender, SelectionChangedEventArgs e) => RefreshClickAction();

    private void RefreshClickAction()
    {
        ClickActionName.Text = _clickAction?.Name ?? "Не назначено";
        ClickActionDescription.Text = _clickAction is null
            ? "Выберите действие из списка и нажмите «Настроить»."
            : _clickAction.Description ?? _clickAction.LaunchApplication?.ExecutablePath
              ?? _clickAction.OpenUri?.Uri ?? "Выполняется при нажатии на пузырь.";
        RemoveClickActionButton.IsEnabled = _clickAction is not null;
        ClickActionPlaceholder.Text = _clickAction is null ? "Выберите действие…" : "Заменить действие…";
        ClickActionPlaceholder.Visibility = ClickActionChoice.SelectedIndex < 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfigureClickActionButton.Content = _clickAction is not null && ClickActionChoice.SelectedIndex < 0
            ? "Изменить"
            : "Настроить";
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
        if (requestedCount < submenu.Slots.Count && submenu.Slots.Skip(requestedCount).Any(IsConfigured))
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

    private sealed record ClickActionOption(string Group, ActionCatalogItem Item)
    {
        public string Title => Item.Title;

        public string Label => $"{Group} · {Title}";
    }
}
