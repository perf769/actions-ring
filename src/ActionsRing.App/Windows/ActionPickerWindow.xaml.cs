using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;

namespace ActionsRing.App.Windows;

public partial class ActionPickerWindow : Window
{
    private readonly IReadOnlyList<ActionPickerEntry> _entries;

    public ActionPickerWindow(ActionCatalogContext? context = null)
    {
        _entries = ActionPickerCatalog.Create(ActionCatalog.GetGroups(context));
        InitializeComponent();
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => SearchBox.Focus();
        CategoryList.ItemsSource = new[] { new CategoryOption("Все действия", null, _entries.Count) }
            .Concat(_entries.GroupBy(entry => entry.Group)
                .Select(group => new CategoryOption(group.Key, group.Key, group.Count())))
            .ToArray();
        CategoryList.SelectedIndex = 0;
        ApplyResponsiveLayout();
    }

    public ActionCatalogItem? SelectedItem { get; private set; }

    private void RefreshActions()
    {
        if (ActionsList is null || SearchBox is null || CategoryList is null) return;
        var query = SearchBox.Text;
        var category = (CategoryList.SelectedItem as CategoryOption)?.Group;
        var matches = ActionPickerCatalog.Filter(_entries, query, category);
        var view = new ListCollectionView(matches.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionPickerEntry.Group)));
        ActionsList.ItemsSource = view;
        ActionsList.SelectedIndex = -1;
        SelectButton.IsEnabled = false;
        SelectButton.Content = "Назначить";
        ResultCountText.Text = $"Действий: {matches.Count}";
        EmptyPanel.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefreshActions();
    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => RefreshActions();
    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnActionIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ActionIconView { DataContext: ActionPickerEntry entry } icon)
            icon.SetIcon(entry.Item.Icon);
    }

    private void OnActionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectButton is null) return;
        var entry = ActionsList.SelectedItem as ActionPickerEntry;
        SelectButton.IsEnabled = entry is not null;
        SelectButton.Content = entry?.Item.RequiresConfiguration == true ? "Настроить…" : "Назначить";
    }

    private void OnActionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        if (hit is not null && ItemsControl.ContainerFromElement(ActionsList, hit) is ListBoxItem { DataContext: ActionPickerEntry })
        {
            CompleteSelection();
            e.Handled = true;
        }
    }

    private void OnSelect(object sender, RoutedEventArgs e) => CompleteSelection();
    private void CompleteSelection()
    {
        if (ActionsList.SelectedItem is not ActionPickerEntry entry) return;
        SelectedItem = entry.Item;
        DialogResult = true;
    }

    private void ApplyResponsiveLayout()
    {
        var compact = (ActualHeight > 0 ? ActualHeight : Height) < 470;
        LayoutRoot.Margin = compact ? new Thickness(16) : new Thickness(24);
        SubtitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchPanel.Margin = compact ? new Thickness(0, 10, 0, 10) : new Thickness(0, 18, 0, 16);
        FooterPanel.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0, 18, 0, 0);
        CategoryColumn.Width = new GridLength((ActualWidth > 0 ? ActualWidth : Width) < 650 ? 166 : 190);
    }

    private void FitToMonitorWorkArea()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } target } source) return;
        var workArea = System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea;
        var topLeft = target.TransformFromDevice.Transform(new Point(workArea.Left, workArea.Top));
        var bottomRight = target.TransformFromDevice.Transform(new Point(workArea.Right, workArea.Bottom));
        var bounds = WindowPlacementCalculator.FitCentered(Width, Height, MinWidth, MinHeight, new Rect(topLeft, bottomRight));
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private sealed record CategoryOption(string Title, string? Group, int Count);
}
