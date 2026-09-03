using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ActionsRing.App.Services;
using Microsoft.Win32;

namespace ActionsRing.App.Windows;

public partial class InstalledApplicationPickerWindow : Window
{
    private readonly InstalledApplicationDiscoveryService _discovery;
    private readonly ApplicationVisualService _visuals;
    private IReadOnlyList<InstalledApplicationInfo> _applications = [];
    private CancellationTokenSource? _loadingCancellation;

    public InstalledApplicationPickerWindow(
        InstalledApplicationDiscoveryService discovery,
        ApplicationVisualService visuals)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _visuals = visuals ?? throw new ArgumentNullException(nameof(visuals));
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _loadingCancellation?.Cancel();
            _loadingCancellation?.Dispose();
        };
    }

    public InstalledApplicationInfo? SelectedApplication { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loadingCancellation = new CancellationTokenSource();
        try
        {
            _applications = await _discovery.DiscoverAsync(_loadingCancellation.Token);
            RefreshList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Installed application discovery failed", exception);
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyText.Text = "Не удалось получить список приложений";
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void RefreshList()
    {
        var query = SearchBox.Text.Trim();
        var filtered = _applications.Where(application =>
                query.Length == 0
                || application.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(500)
            .ToArray();
        ApplicationsList.Items.Clear();
        foreach (var application in filtered)
        {
            ApplicationsList.Items.Add(CreateItem(application));
        }
        LoadingPanel.Visibility = Visibility.Collapsed;
        EmptyText.Text = "Ничего не найдено";
        EmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private ListBoxItem CreateItem(InstalledApplicationInfo application)
    {
        var iconHost = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = FindResource("AccentSoftBrush") as Brush,
            Child = new TextBlock
            {
                Text = Initials(application.Name),
                Foreground = FindResource("AccentBrush") as Brush,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _ = LoadItemIconAsync(iconHost, application);
        return new ListBoxItem
        {
            Tag = application,
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(48) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Children =
                {
                    iconHost,
                    Place(new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = application.Name, FontWeight = FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = application.IsPackaged ? "Microsoft Store" : application.ExecutablePath,
                                Style = FindResource("CaptionText") as Style,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                TextWrapping = TextWrapping.NoWrap,
                                Margin = new Thickness(0, 3, 0, 0),
                            },
                        },
                    }, 1),
                    Place(new TextBlock
                    {
                        Text = application.IsPackaged ? "STORE" : "WIN32",
                        Foreground = FindResource("TextMutedBrush") as Brush,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(12, 0, 4, 0),
                    }, 2),
                },
            },
        };
    }

    private async Task LoadItemIconAsync(Border host, InstalledApplicationInfo application)
    {
        try
        {
            var cached = await _visuals.CacheApplicationIconAsync(
                application,
                _loadingCancellation?.Token ?? CancellationToken.None);
            var image = _visuals.TryLoadIcon(cached);
            if (image is not null && IsLoaded)
            {
                host.Child = new Image { Source = image, Width = 34, Height = 34, Stretch = Stretch.Uniform };
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (LoadingPanel.Visibility != Visibility.Visible)
        {
            RefreshList();
        }
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = ApplicationsList.SelectedItem is ListBoxItem;

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ApplicationsList.SelectedItem is ListBoxItem)
        {
            CompleteSelection();
        }
    }

    private void OnSelect(object sender, RoutedEventArgs e) => CompleteSelection();

    private void CompleteSelection()
    {
        if ((ApplicationsList.SelectedItem as ListBoxItem)?.Tag is not InstalledApplicationInfo application)
        {
            return;
        }
        SelectedApplication = application;
        DialogResult = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите приложение",
            Filter = "Приложения (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(dialog.FileName);
        try
        {
            name = FileVersionInfo.GetVersionInfo(dialog.FileName).ProductName?.Trim() is { Length: > 0 } productName
                ? productName
                : name;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Could not read selected application metadata", exception);
        }
        SelectedApplication = new InstalledApplicationInfo(
            name,
            dialog.FileName,
            Path.GetFileNameWithoutExtension(dialog.FileName),
            dialog.FileName,
            IconPath: dialog.FileName);
        DialogResult = true;
    }

    private static T Place<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }
}
