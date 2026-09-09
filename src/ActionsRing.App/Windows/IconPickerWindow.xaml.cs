using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using Microsoft.Win32;

namespace ActionsRing.App.Windows;

public partial class IconPickerWindow : Window
{
    private const int PageSize = 90;
    private IReadOnlyList<IconLibraryEntry> _results = [];
    private IReadOnlyList<InstalledApplicationInfo> _applications = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ready;
    private bool _applicationsStarted;
    private int _page;
    private long _selectionVersion;
    public IconPickerWindow(string? currentIcon)
    {
        InitializeComponent();
        SelectedIcon = currentIcon;
        PreviewIcon.SetIcon(currentIcon);
        ImportIllustration.SetIcon("lucide:image-up");
        CategoryBox.ItemsSource = IconLibrary.Categories;
        CategoryBox.SelectedIndex = 0;
        _ready = true;
        RefreshIcons();
        SourceInitialized += (_, _) => { FitToMonitor(); ApplyResponsiveLayout(); };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Closed += (_, _) => _lifetime.Cancel();
    }
    public string? SelectedIcon { get; private set; }

    private void RefreshIcons()
    {
        if (!_ready) return;
        _results = IconLibrary.Search(SearchBox.Text, CategoryBox.SelectedItem as string);
        _page = Math.Clamp(_page, 0, Math.Max(0, (_results.Count - 1) / PageSize));
        IconsList.Items.Clear();
        foreach (var icon in _results.Skip(_page * PageSize).Take(PageSize))
        {
            var visual = MakeIcon(icon.Reference, 28);
            var text = new TextBlock { Text = icon.DisplayName, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)FindResource("TextSecondaryBrush") };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var item = new ListBoxItem
            {
                Tag = icon.Reference,
                ToolTip = icon.DisplayName + " · " + icon.Reference[(icon.Reference.IndexOf(':') + 1)..],
                Width = 78,
                Height = IsCompact ? 64 : 76,
                Padding = new Thickness(6, 8, 6, 5),
                Margin = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new StackPanel { Children = { visual, text } },
                IsSelected = string.Equals(icon.Reference, SelectedIcon, StringComparison.OrdinalIgnoreCase),
                Style = (Style)FindResource(typeof(ListBoxItem)),
            };
            IconsList.Items.Add(item);
        }
        ResultCountText.Text = $"Иконок: {_results.Count:N0}";
        PageText.Text = _results.Count == 0 ? "Ничего не найдено" : $"{_page + 1} / {Math.Max(1, (int)Math.Ceiling(_results.Count / (double)PageSize))}";
        PreviousButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = (_page + 1) * PageSize < _results.Count;
    }

    private async void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != SourceTabs) return;
        _selectionVersion++;
        SearchPanel.Visibility = SourceTabs.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        if (SourceTabs.SelectedIndex != 1 || _applicationsStarted) return;
        _applicationsStarted = true;
        try
        {
            _applications = await new InstalledApplicationDiscoveryService().DiscoverAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested) RefreshApplications();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLog.Error("Could not discover application icons", exception); ApplicationsStatusText.Text = "Не удалось получить список приложений"; }
    }

    private void RefreshApplications()
    {
        ApplicationsList.Items.Clear();
        foreach (var application in _applications.Where(app => app.SearchText.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            var icon = MakeIcon(application.IconPath ?? application.ExecutablePath ?? application.LaunchTarget, 32, new ActionDefinition { Kind = ActionKind.LaunchApplication, LaunchApplication = new LaunchApplicationAction { ExecutablePath = application.LaunchTarget } });
            var label = new TextBlock { Text = application.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 0, 0) };
            label.Foreground = (Brush)FindResource("TextPrimaryBrush");
            var content = new DockPanel(); DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon); content.Children.Add(label);
            ApplicationsList.Items.Add(new ListBoxItem { Tag = application, Content = content, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        ApplicationsStatusText.Visibility = ApplicationsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsStatusText.Text = "Приложения не найдены";
    }

    private void OnIconSelected(object sender, SelectionChangedEventArgs e)
    {
        if ((IconsList.SelectedItem as ListBoxItem)?.Tag is not string reference) return;
        Choose(reference, (IconsList.SelectedItem as ListBoxItem)?.ToolTip as string);
    }
    private async void OnApplicationSelected(object sender, SelectionChangedEventArgs e)
    {
        if ((ApplicationsList.SelectedItem as ListBoxItem)?.Tag is not InstalledApplicationInfo application) return;
        var version = ++_selectionVersion;
        var action = new ActionDefinition { Kind = ActionKind.LaunchApplication, LaunchApplication = new LaunchApplicationAction { ExecutablePath = application.LaunchTarget } };
        var reference = IconLibrary.KnownBrandReference(action);
        SelectButton.IsEnabled = false;
        try
        {
            var cached = await new ApplicationVisualService().CacheApplicationIconAsync(application, _lifetime.Token);
            if (cached is not null && !_lifetime.IsCancellationRequested && version == _selectionVersion && SourceTabs.SelectedIndex == 1 && Equals((ApplicationsList.SelectedItem as ListBoxItem)?.Tag, application))
            { var imported = new IconImportService().Import(cached); Choose(imported, application.Name); }
            else if (cached is null && reference is not null && !_lifetime.IsCancellationRequested && version == _selectionVersion && SourceTabs.SelectedIndex == 1)
            { Choose(reference, application.Name); }
            else if (cached is null && !_lifetime.IsCancellationRequested && version == _selectionVersion)
            { ApplicationsStatusText.Text = "Иконка приложения недоступна"; ApplicationsStatusText.Visibility = Visibility.Visible; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        { AppLog.Error("Could not select application icon", exception); if (version == _selectionVersion) { ApplicationsStatusText.Text = "Иконка приложения недоступна"; ApplicationsStatusText.Visibility = Visibility.Visible; } }
    }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите иконку", Filter = "Иконки (*.svg;*.png;*.ico;*.jpg;*.jpeg)|*.svg;*.png;*.ico;*.jpg;*.jpeg", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var version = ++_selectionVersion;
        ImportButton.IsEnabled = false;
        SelectButton.IsEnabled = false;
        try
        {
            var reference = await Task.Run(() => new IconImportService().Import(dialog.FileName));
            if (_lifetime.IsCancellationRequested || version != _selectionVersion || SourceTabs.SelectedIndex != 2) return;
            Choose(reference, Path.GetFileName(dialog.FileName));
            ImportStatusText.Text = Path.GetFileName(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException or System.Xml.XmlException)
        { ImportStatusText.Text = exception is FormatException ? exception.Message : "Не удалось прочитать иконку. Попробуйте другой SVG, PNG или ICO."; }
        finally { ImportButton.IsEnabled = true; }
    }
    private void Choose(string reference, string? name) { _selectionVersion++; SelectedIcon = reference; PreviewIcon.SetIcon(reference); SubtitleText.Text = name ?? "Выбранная иконка"; SelectButton.IsEnabled = true; }
    private void OnAutomatic(object sender, RoutedEventArgs e) { SelectedIcon = null; DialogResult = true; }
    private void OnSelect(object sender, RoutedEventArgs e) { if (SelectButton.IsEnabled) DialogResult = true; }
    private void OnIconDoubleClick(object sender, MouseButtonEventArgs e) { if (SelectButton.IsEnabled && e.OriginalSource is DependencyObject) DialogResult = true; }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (!_ready) return; ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; _page = 0; RefreshIcons(); if (_applicationsStarted) RefreshApplications(); }
    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Clear();
    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) { _page = 0; RefreshIcons(); }
    private void OnPrevious(object sender, RoutedEventArgs e) { _page--; RefreshIcons(); }
    private void OnNext(object sender, RoutedEventArgs e) { _page++; RefreshIcons(); }
    private bool IsCompact => (ActualHeight > 0 ? ActualHeight : Height) <= 460;
    private void ApplyResponsiveLayout()
    {
        var compact = IsCompact;
        RootGrid.Margin = compact ? new Thickness(16, 7, 16, 9) : new Thickness(24);
        HeadingPanel.Margin = new Thickness(0, 0, 0, compact ? 7 : 14);
        HeadingText.FontSize = compact ? 18 : 20;
        SubtitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PreviewSurface.Width = PreviewSurface.Height = compact ? 32 : 54;
        PreviewIcon.Width = PreviewIcon.Height = compact ? 22 : 30;
        SearchPanel.Margin = new Thickness(0, 0, 0, compact ? 7 : 12);
        SearchBox.Height = compact ? 32 : double.NaN;
        SearchBox.Padding = compact ? new Thickness(9, 3, 38, 3) : new Thickness(12, 9, 40, 9);
        LibraryPanel.Margin = new Thickness(0, compact ? 7 : 12, 0, 0);
        CategoryPanel.Margin = new Thickness(0, 0, 0, compact ? 5 : 10);
        CategoryBox.Height = compact ? 30 : double.NaN;
        CategoryBox.Padding = compact ? new Thickness(10, 4, 30, 4) : new Thickness(12, 9, 12, 9);
        PaginationPanel.Margin = new Thickness(0, compact ? 3 : 9, 0, 0);
        Grid.SetRow(PaginationPanel, compact ? 0 : 2);
        PaginationPanel.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        PaginationPanel.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        PaginationPanel.Width = compact ? 104 : double.NaN;
        PageText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ResultCountText.Margin = compact ? new Thickness(0, 0, 116, 0) : new Thickness(0);
        PreviousButton.Padding = NextButton.Padding = compact ? new Thickness(12, 4, 12, 4) : new Thickness(18, 10, 18, 10);
        FooterPanel.Margin = new Thickness(0, compact ? 7 : 16, 0, 0);
        foreach (var button in FooterPanel.Children.OfType<Button>()) button.Padding = compact ? new Thickness(14, 7, 14, 7) : new Thickness(18, 10, 18, 10);
        foreach (var tab in SourceTabs.Items.OfType<TabItem>()) tab.Padding = compact ? new Thickness(12, 7, 12, 7) : new Thickness(15, 9, 15, 9);
        foreach (var item in IconsList.Items.OfType<ListBoxItem>()) item.Height = compact ? 64 : 76;
        ImportIllustration.Visibility = ImportHeadingText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ImportPanel.Margin = compact ? new Thickness(4) : new Thickness(16);
        ImportDescriptionText.Margin = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 7, 0, 18);
        ImportStatusText.Margin = new Thickness(0, compact ? 5 : 12, 0, 0);
        ImportButton.Padding = compact ? new Thickness(15, 7, 15, 7) : new Thickness(18, 10, 18, 10);
    }
    private ActionIconView MakeIcon(string? reference, double size, ActionDefinition? action = null)
    {
        var icon = new ActionIconView { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); icon.SetResourceReference(BackgroundProperty, "WindowBrush"); icon.SetIcon(reference, action); return icon;
    }
    private void FitToMonitor()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } target } source) return;
        var area = System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea;
        var fromDevice = target.TransformFromDevice;
        var bounds = WindowPlacementCalculator.FitCentered(Width, Height, MinWidth, MinHeight, new Rect(fromDevice.Transform(new Point(area.Left, area.Top)), fromDevice.Transform(new Point(area.Right, area.Bottom))));
        MinWidth = Math.Min(MinWidth, bounds.Width); MinHeight = Math.Min(MinHeight, bounds.Height); Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
    }
}
