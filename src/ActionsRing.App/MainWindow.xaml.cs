using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.App.Windows;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using ActionsRing.Platform.Windows.Foreground;
using Microsoft.Win32;
using CoreMouseButton = ActionsRing.Core.Domain.MouseButton;

namespace ActionsRing.App;

public sealed class UpdateInstallRequestedEventArgs(StagedUpdatePackage package) : EventArgs
{
    public StagedUpdatePackage Package { get; } = package ?? throw new ArgumentNullException(nameof(package));
}

public partial class MainWindow : Window
{
    private readonly ApplicationController _controller;
    private readonly IUpdateService _updateService;
    private readonly DispatcherTimer _saveAppearanceTimer;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly InstalledApplicationDiscoveryService _applicationDiscovery = new();
    private readonly ApplicationVisualService _visuals = new();
    private readonly Dictionary<string, bool> _actionGroupExpansion = new(StringComparer.Ordinal);
    private Point _dragStart;
    private string _selectedProfileId = string.Empty;
    private RingSlotDefinition? _selectedSlot;
    private bool _refreshingEditor;
    private int _onboardingStep = 1;
    private bool _refreshing = true;
    private bool _allowClose;
    private bool _appearanceDirty;
    private bool _configurationMutationInProgress;
    private ConfigurationMutationTransaction? _pendingAppearanceTransaction;
    private UpdateAvailableWindow? _activeUpdateDialog;

    public MainWindow(ApplicationController controller, ThemeService theme, IUpdateService updateService)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        ArgumentNullException.ThrowIfNull(theme);
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        InitializeComponent();
        _selectedProfileId = _controller.Configuration.GetActiveUserProfile().GlobalProfile.Id;

        RingEditor.InteractionMode = RingInteractionMode.Configure;
        RingEditor.SelectionChanged += OnEditorSelectionChanged;
        RingEditor.SlotDropRequested += OnEditorSlotDropRequested;
        _controller.ConfigurationChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            // A save started outside this window (for example trigger capture or
            // the tray autostart command) persists the whole live graph too.
            if (!_configurationMutationInProgress)
            {
                CommitPendingAppearanceTransaction();
            }
            RefreshAll();
        });
        _controller.StatusChanged += (_, args) => Dispatcher.Invoke(() =>
        {
            RefreshRuntimeStatus();
            ShowStatus(args.Message, args.IsError);
        });
        _controller.ActiveContextChanged += (_, args) => Dispatcher.Invoke(() =>
        {
            ActiveProfileStatus.Text = $"{_controller.Configuration.GetActiveUserProfile().Name} · {args.ApplicationName} · {args.ProfileName}";
        });

        _saveAppearanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _saveAppearanceTimer.Tick += async (_, _) =>
        {
            _saveAppearanceTimer.Stop();
            await FlushPendingAppearanceSaveAsync();
        };

        SourceInitialized += (_, _) => FitToMonitorWorkArea();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += OnLoaded;
        _refreshing = false;
        ApplyResponsiveLayout();
    }

    public event EventHandler? ExitRequested;

    public event EventHandler<UpdateInstallRequestedEventArgs>? UpdateInstallRequested;

    public void ShowFromTray()
    {
        RefreshRuntimeStatus();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void RequestExit()
    {
        _allowClose = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowUpdateInstallFailure()
    {
        ShowFromTray();
        AboutNav.IsChecked = true;
        NavigateTo("About");
        UpdateStatusText.Text = "Не удалось установить обновление. Текущая версия продолжает работать, а загруженный пакет можно запустить ещё раз.";
        SetVersionStatus("Ошибка установки", "DangerBrush", "SurfaceRaisedBrush");
        ShowStatus("Не удалось установить обновление", isError: true);
    }

    public async Task PrepareForShutdownAsync()
    {
        _allowClose = true;
        _updateLifetime.Cancel();
        _activeUpdateDialog?.Close();
        _saveAppearanceTimer.Stop();
        await FlushPendingAppearanceSaveAsync();
        await _updateGate.WaitAsync();
        _updateGate.Release();
        Hide();
    }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        bool entered;
        try
        {
            entered = await _updateGate.WaitAsync(0, _updateLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!entered)
        {
            return;
        }
        try
        {
            await _updateService.CleanupObsoleteStagesAsync(_updateLifetime.Token);
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AppLog.Error("Obsolete update cleanup failed", exception);
        }
        finally
        {
            _updateGate.Release();
        }

        var preferences = _controller.Configuration.Preferences.Updates;
        if (!_controller.Configuration.Onboarding.IsCompleted
            || !preferences.CheckAutomatically
            || !IsAutomaticUpdateCheckDue(preferences.LastCheckedAtUtc, DateTimeOffset.UtcNow))
        {
            return;
        }

        await ExecuteUpdateCheckAsync(manual: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        VersionText.Text = $"Версия {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.4.0"}";
        if (!_controller.Configuration.Onboarding.IsCompleted)
        {
            ShowOnboarding(1);
        }
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
        var availableWidth = ActualWidth > 0 ? ActualWidth : Width;
        var availableHeight = ActualHeight > 0 ? ActualHeight : Height;
        var compact = availableWidth < 1000;
        var compactHeight = availableHeight < 560;
        var sidebarWidth = compact ? 72d : 210d;
        SidebarColumn.Width = new GridLength(sidebarWidth);
        TitleSidebarColumn.Width = new GridLength(sidebarWidth);
        BrandNameText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RuntimeCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        RingNav.Content = compact ? null : "Кольцо действий";
        ProfilesNav.Content = compact ? null : "Профили";
        TriggerNav.Content = compact ? null : "Вызов кольца";
        SettingsNav.Content = compact ? null : "Настройки";
        AboutNav.Content = compact ? null : "О программе";

        ProfileHeaderGrid.Margin = compact ? new Thickness(16, 0, 16, 0) : new Thickness(24, 0, 24, 0);
        ProfileSummaryPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ProfileTabsScroller.Margin = compact ? new Thickness(0, 0, 12, 0) : new Thickness(24, 0, 24, 0);
        AddProfileButton.Content = compact ? "+" : "+ Приложение";
        AddProfileButton.MinWidth = compact ? 42 : 0;

        EditorHeadingPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RingStyleCombo.Width = compact ? 132 : 138;
        EditRingColorsButton.Content = compact ? "●" : "Цвета";
        EditRingColorsButton.MinWidth = compact ? 40 : 0;
        ResetRingButton.Content = compact ? "↺" : "По умолчанию";
        ResetRingButton.MinWidth = compact ? 40 : 0;
        PreviewRingButton.Content = compact ? "▶" : "Проверить";
        PreviewRingButton.MinWidth = compact ? 40 : 0;
        SlotAppearanceButton.Content = compact ? "●" : "Цвета";
        SlotIconButton.Content = compact ? "◇" : "Иконка";
        EditSlotButton.Content = compact ? "✎" : "Изменить";
        ClearSlotButton.Content = compact ? "×" : "Очистить";
        EditorCanvasScroller.VerticalScrollBarVisibility = compact
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        RingEditor.Height = compact ? 226 : double.NaN;
        RingEditor.ShowConfigurationLabels = !compact;

        OnboardingDialog.Margin = compactHeight ? new Thickness(10) : new Thickness(22);
        OnboardingDialog.Padding = compactHeight ? new Thickness(16) : new Thickness(26);
        OnboardingDialog.CornerRadius = compactHeight ? new CornerRadius(20) : new CornerRadius(24);
        OnboardingTextPanel.Margin = compactHeight ? new Thickness(2, 0, 16, 0) : new Thickness(4, 0, 28, 0);
        OnboardingTitle.FontSize = compactHeight ? 28 : 34;
        OnboardingTitle.Margin = compactHeight ? new Thickness(0, 8, 0, 0) : new Thickness(0, 12, 0, 0);
        OnboardingBody.FontSize = compactHeight ? 13 : 14;
        OnboardingBody.LineHeight = compactHeight ? 20 : 22;
        OnboardingBody.Margin = compactHeight ? new Thickness(0, 8, 0, 0) : new Thickness(0, 14, 0, 0);
        OnboardingTriggerCard.Margin = compactHeight ? new Thickness(0, 10, 0, 0) : new Thickness(0, 20, 0, 0);
        OnboardingTriggerCard.Padding = compactHeight ? new Thickness(14) : new Thickness(20);
        OnboardingCaptureButton.Margin = compactHeight ? new Thickness(0, 8, 0, 0) : new Thickness(0, 13, 0, 0);
        OnboardingRingHost.Margin = compactHeight ? new Thickness(6, 12, 0, 12) : new Thickness(12, 24, 0, 24);
        OnboardingRing.Width = compactHeight ? 230 : 300;
        OnboardingRing.Height = compactHeight ? 230 : 300;
    }

    private void RefreshAll()
    {
        _refreshing = true;
        try
        {
            RefreshUserProfileSelector();
            EnsureSelectedProfileExists();
            RebuildProfileTabs();
            RebuildUserProfilesList();
            RebuildProfilesList();
            RebuildActionLibrary(ActionSearchBox.Text);
            RefreshEditor();
            RefreshTrigger();
            RefreshSettings();
            RefreshRuntimeStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshEditor()
    {
        var (_, ring, style) = GetSelectedProfile();
        var selectedId = _selectedSlot?.Id;
        _refreshingEditor = true;
        try
        {
            RingEditor.RingScale = 1.0;
            RingEditor.CenterDiameter = 32;
            RingEditor.ApplyStyle(style);
            RingEditor.Present(ring, animate: false);
            _selectedSlot = selectedId is null ? null : FindSlotById(ring, selectedId);
            RingEditor.SetSelectedSlot(_selectedSlot);
        }
        finally
        {
            _refreshingEditor = false;
        }
        var wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            SelectComboByTag(RingStyleCombo, style.Preset.ToString());
        }
        finally
        {
            _refreshing = wasRefreshing;
        }
        EditRingColorsButton.Visibility = style.Preset == RingStylePreset.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshSelectedSlotCard();
    }

    private void RefreshTrigger()
    {
        var trigger = _controller.Configuration.Trigger;
        var isMouse = trigger.Kind == InputBindingKind.MouseButton;
        TriggerGlyph.Text = isMouse ? "\uE962" : "\uE765";
        TriggerDescription.Text = InputBindingMapper.Describe(trigger);
        TriggerModeDescription.Text = trigger.ActivationMode == ActivationMode.Hold
            ? "Удерживать для выбора"
            : "Нажать, затем выбрать";
        OnboardingTriggerText.Text = TriggerDescription.Text;
        HoldModeRadio.IsChecked = trigger.ActivationMode == ActivationMode.Hold;
        ToggleModeRadio.IsChecked = trigger.ActivationMode == ActivationMode.Toggle;
        var warning = InputBindingMapper.GetConflictWarning(trigger);
        TriggerWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        TriggerWarningText.Text = warning ?? string.Empty;
    }

    private void RefreshSettings()
    {
        var general = _controller.Configuration.Preferences.General;
        var appearance = _controller.Configuration.Preferences.Appearance;
        var updates = _controller.Configuration.Preferences.Updates;
        StartupToggle.IsChecked = general.RunAtStartup;
        StartMinimizedToggle.IsChecked = general.StartMinimized;
        CloseToTrayToggle.IsChecked = general.CloseToTray;
        KeyStateNotificationsToggle.IsChecked = general.ShowKeyStateNotifications;
        AutoScaleToggle.IsChecked = appearance.AutoScaleRing;
        TooltipsToggle.IsChecked = appearance.ShowTooltips;
        AnimationsToggle.IsChecked = appearance.EnableAnimations;
        ReduceMotionToggle.IsChecked = appearance.ReduceMotion;
        ReduceMotionToggle.IsEnabled = appearance.EnableAnimations;
        RingSizeSlider.Value = appearance.RingDiameter;
        CenterSizeSlider.Value = appearance.CenterCloseDiameter;
        TooltipDelaySlider.Value = appearance.TooltipDelayMilliseconds;
        TooltipDelaySlider.IsEnabled = appearance.ShowTooltips;
        UpdateRingSizeLabel(appearance.RingDiameter);
        UpdateCenterSizeLabel(appearance.CenterCloseDiameter);
        UpdateTooltipDelayLabel(appearance.TooltipDelayMilliseconds);
        SelectComboByTag(ThemeCombo, appearance.Theme.ToString());
        AutoCheckUpdatesToggle.IsChecked = updates.CheckAutomatically;
        AutoDownloadUpdatesToggle.IsChecked = updates.DownloadAutomatically;
        AutoDownloadUpdatesToggle.IsEnabled = updates.CheckAutomatically;
        LastUpdateCheckText.Text = updates.LastCheckedAtUtc is { } lastChecked
            ? $"Последняя проверка: {lastChecked.ToLocalTime():g}"
            : "Обновления ещё не проверялись";
    }

    private void RefreshUserProfileSelector()
    {
        var activeId = _controller.Configuration.ActiveUserProfileId;
        UserProfileCombo.ItemsSource = null;
        UserProfileCombo.ItemsSource = _controller.Configuration.UserProfiles;
        UserProfileCombo.SelectedValue = activeId;
        ApplicationsSectionTitle.Text = $"ПРИЛОЖЕНИЯ · {_controller.Configuration.GetActiveUserProfile().Name.ToUpperInvariant()}";
    }

    private void RebuildProfileTabs()
    {
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        ProfileTabsPanel.Children.Clear();
        AddProfileTab(
            userProfile.GlobalProfile.Id,
            userProfile.GlobalProfile.Name,
            CreateApplicationIcon(userProfile.GlobalProfile.Name, isGlobal: true),
            _selectedProfileId == userProfile.GlobalProfile.Id);
        foreach (var profile in userProfile.ApplicationProfiles)
        {
            AddProfileTab(
                profile.Id,
                profile.Name,
                CreateApplicationIcon(profile.Name, isGlobal: false, profile),
                profile.Id == _selectedProfileId);
        }
    }

    private void OnProfileTabsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ProfileTabsScroller.ScrollableWidth <= 0 || e.Delta == 0) return;
        ProfileTabsScroller.ScrollToHorizontalOffset(Math.Clamp(
            ProfileTabsScroller.HorizontalOffset - e.Delta / 120d * 108,
            0, ProfileTabsScroller.ScrollableWidth));
        e.Handled = true;
    }

    private void AddProfileTab(string id, string title, FrameworkElement icon, bool selected)
    {
        icon.Width = 24;
        icon.Height = 24;
        icon.VerticalAlignment = VerticalAlignment.Center;
        if (icon is Border iconBorder)
        {
            iconBorder.CornerRadius = new CornerRadius(7);
            if (iconBorder.Child is ActionIconView image)
            {
                image.Width = 18;
                image.Height = 18;
            }
            else if (iconBorder.Child is TextBlock text)
            {
                text.FontSize = 10;
            }
        }
        var button = new Button
        {
            Tag = id,
            ToolTip = title,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(11, 8, 11, 8),
            Background = selected
                ? FindBrush("AccentSoftBrush", Brushes.MediumPurple)
                : Brushes.Transparent,
            BorderBrush = selected
                ? FindBrush("AccentBrush", Brushes.MediumPurple)
                : FindBrush("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            Cursor = Cursors.Hand,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    icon,
                    new TextBlock
                    {
                        Text = title,
                        Margin = new Thickness(7, 0, 0, 0),
                        MaxWidth = 110,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        button.Click += (_, _) =>
        {
            SelectApplicationProfile(id);
        };
        ProfileTabsPanel.Children.Add(button);
    }

    private void SelectApplicationProfile(string id)
    {
        _selectedProfileId = id;
        _selectedSlot = null;
        RebuildProfileTabs();
        RebuildActionLibrary(ActionSearchBox.Text);
        RefreshEditor();
    }

    private void RebuildUserProfilesList()
    {
        UserProfilesListPanel.Children.Clear();
        foreach (var profile in _controller.Configuration.UserProfiles)
        {
            UserProfilesListPanel.Children.Add(BuildUserProfileCard(profile));
        }
    }

    private Border BuildUserProfileCard(UserProfile profile)
    {
        var isActive = string.Equals(
            profile.Id,
            _controller.Configuration.ActiveUserProfileId,
            StringComparison.OrdinalIgnoreCase);
        var select = MakeSmallButton(isActive ? "Выбран" : "Выбрать", async (_, _) =>
        {
            if (!isActive)
            {
                await SwitchUserProfileAsync(profile.Id);
            }
        });
        select.IsEnabled = !isActive;
        var rename = MakeSmallButton("Переименовать", async (_, _) => await RenameUserProfileAsync(profile));
        var duplicate = MakeSmallButton("Дублировать", async (_, _) => await DuplicateUserProfileAsync(profile));
        var remove = MakeSmallButton("Удалить", async (_, _) => await DeleteUserProfileAsync(profile));
        remove.IsEnabled = _controller.Configuration.UserProfiles.Count > 1;

        return new Border
        {
            Style = FindResource("CardBorder") as Style,
            Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = isActive ? FindBrush("AccentBrush", Brushes.MediumPurple) : FindBrush("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(isActive ? 1.5 : 1),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(54) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Children =
                {
                    CreateUserProfileIcon(profile.Name),
                    Place(new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = profile.Name, FontSize = 16, FontWeight = FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = $"{profile.ApplicationProfiles.Count} приложений · отдельное кольцо по умолчанию",
                                Style = FindResource("CaptionText") as Style,
                                Margin = new Thickness(0, 4, 0, 0),
                            },
                        },
                    }, 1),
                    Place(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { select, rename, duplicate, remove },
                    }, 2),
                },
            },
        };
    }

    private void RebuildProfilesList()
    {
        ProfilesListPanel.Children.Clear();
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        ProfilesListPanel.Children.Add(BuildProfileCard(
            userProfile.GlobalProfile.Id,
            userProfile.GlobalProfile.Name,
            "Используется на рабочем столе и когда приложение не совпало ни с одним правилом.",
            isGlobal: true,
            isEnabled: true,
            applicationProfile: null));
        foreach (var profile in userProfile.ApplicationProfiles
                     .OrderByDescending(item => item.Priority))
        {
            var patterns = string.Join(" · ", profile.MatchRules.Select(rule => rule.Pattern));
            ProfilesListPanel.Children.Add(BuildProfileCard(
                profile.Id,
                profile.Name,
                string.IsNullOrWhiteSpace(patterns) ? "Нет правил сопоставления" : patterns,
                isGlobal: false,
                isEnabled: profile.IsEnabled,
                applicationProfile: profile));
        }
    }

    private Border BuildProfileCard(
        string id,
        string name,
        string detail,
        bool isGlobal,
        bool isEnabled,
        ApplicationProfile? applicationProfile)
    {
        var enabledToggle = new CheckBox { Style = FindResource("ToggleSwitch") as Style, IsChecked = isEnabled, IsEnabled = !isGlobal };
        enabledToggle.Click += async (_, _) =>
        {
            var requested = enabledToggle.IsChecked == true;
            await MutateAndSaveAsync(
                configuration =>
                {
                    var profile = configuration.ApplicationProfiles.FirstOrDefault(item => item.Id == id)
                                  ?? throw new InvalidOperationException("The selected profile no longer exists.");
                    profile.IsEnabled = requested;
                },
                updateAutostart: false);
        };

        var edit = MakeSmallButton("Настроить", (_, _) =>
        {
            SelectApplicationProfile(id);
            RingNav.IsChecked = true;
            NavigateTo("Ring");
        });
        var remove = MakeSmallButton("Удалить", async (_, _) =>
        {
            if (isGlobal || MessageBox.Show(this, $"Удалить профиль «{name}»?", "Actions Ring", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            if (await MutateAndSaveAsync(
                    configuration => configuration.ApplicationProfiles.RemoveAll(item => item.Id == id),
                    updateAutostart: false))
            {
                _selectedProfileId = _controller.Configuration.GetActiveUserProfile().GlobalProfile.Id;
                _selectedSlot = null;
                RefreshAll();
            }
        });
        remove.Visibility = isGlobal ? Visibility.Collapsed : Visibility.Visible;

        return new Border
        {
            Style = FindResource("CardBorder") as Style,
            Margin = new Thickness(0, 0, 0, 10),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(54) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Children =
                {
                    CreateApplicationIcon(name, isGlobal, applicationProfile),
                    Place(new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold },
                            new TextBlock { Text = detail, Style = FindResource("CaptionText") as Style, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
                        },
                    }, 1),
                    Place(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { enabledToggle, edit, remove },
                    }, 2),
                },
            },
        };
    }

    private FrameworkElement CreateApplicationIcon(
        string name,
        bool isGlobal,
        ApplicationProfile? profile = null)
    {
        var executable = profile?.MatchRules.FirstOrDefault(rule =>
            rule.Kind == ApplicationMatchKind.ExecutablePath)?.Pattern;
        var content = CreateActionIcon(isGlobal ? "lucide:globe" : null,
            isGlobal ? null : new ActionDefinition
            {
                Name = name,
                Kind = ActionKind.LaunchApplication,
                Icon = profile?.IconPath,
                LaunchApplication = new LaunchApplicationAction
                {
                    ExecutablePath = profile?.LaunchTarget ?? executable ?? string.Empty,
                },
            }, 30);

        return new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = FindBrush("AccentSoftBrush", Brushes.Lavender),
            Child = content,
        };
    }

    private FrameworkElement CreateUserProfileIcon(string name) => new Border
    {
        Width = 42,
        Height = 42,
        CornerRadius = new CornerRadius(12),
        Background = FindBrush("AccentSoftBrush", Brushes.Lavender),
        Child = new TextBlock
        {
            Text = Initials(name),
            Foreground = FindBrush("AccentBrush", Brushes.MediumPurple),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private Button MakeSmallButton(string label, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = label,
            Style = FindResource("SecondaryButton") as Style,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += handler;
        return button;
    }

    private void RebuildActionLibrary(string? search)
    {
        ActionGroupsPanel.Children.Clear();
        var query = search?.Trim() ?? string.Empty;
        ClearActionSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var group in ActionCatalog.GetGroups(GetSelectedActionCatalogContext()))
        {
            var items = group.Items.Where(item => query.Length == 0
                                                  || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                                  || item.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                                  || group.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
            if (items.Length == 0)
            {
                continue;
            }

            var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
            foreach (var item in items)
            {
                var button = CreateActionButton(item);
                stack.Children.Add(button);
            }
            var expander = new Expander
            {
                Header = group.IsContextual
                    ? $"ДЛЯ {group.Title.ToUpperInvariant()}"
                    : group.Title.ToUpperInvariant(),
                Content = stack,
                IsExpanded = query.Length > 0 || _actionGroupExpansion.GetValueOrDefault(group.Title, group.IsContextual),
                Foreground = group.IsContextual
                    ? FindBrush("AccentBrush", Brushes.MediumPurple)
                    : FindBrush("TextPrimaryBrush", Brushes.Black),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 7),
            };
            expander.Expanded += (_, _) =>
            {
                if (ActionSearchBox.Text.Trim().Length == 0)
                {
                    _actionGroupExpansion[group.Title] = true;
                }
            };
            expander.Collapsed += (_, _) =>
            {
                if (ActionSearchBox.Text.Trim().Length == 0)
                {
                    _actionGroupExpansion[group.Title] = false;
                }
            };
            ActionGroupsPanel.Children.Add(expander);
        }
        if (ActionGroupsPanel.Children.Count == 0)
        {
            ActionGroupsPanel.Children.Add(new TextBlock
            {
                Text = "Ничего не найдено",
                Style = FindResource("CaptionText") as Style,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 0),
            });
        }
    }

    private Button CreateActionButton(ActionCatalogItem item)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = FindBrush("AccentSoftBrush", Brushes.Lavender),
            Child = CreateActionIcon(item.Icon, item.CreateSlot().Action, 19),
        });
        content.Children.Add(Place(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 13 },
                new TextBlock { Text = item.Description, Style = FindResource("CaptionText") as Style, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
            },
        }, 1));

        var button = new Button
        {
            Tag = item,
            Content = content,
            Style = FindResource("SecondaryButton") as Style,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
        };
        button.Click += OnActionItemClick;
        button.PreviewMouseLeftButtonDown += (_, args) => _dragStart = args.GetPosition(button);
        button.PreviewMouseMove += (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var point = args.GetPosition(button);
            if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            var data = new DataObject(ActionCatalog.DragFormat, item);
            DragDrop.DoDragDrop(button, data, DragDropEffects.Copy);
        };
        return button;
    }

    private void OnEditorSelectionChanged(object? sender, EventArgs e)
    {
        if (_refreshingEditor)
        {
            return;
        }
        _selectedSlot = RingEditor.SelectedSlot;
        RefreshSelectedSlotCard();
    }

    private ActionIconView CreateActionIcon(string? icon, ActionDefinition? action, double size)
    {
        var view = new ActionIconView
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        view.SetResourceReference(Control.BackgroundProperty, "AccentSoftBrush");
        view.SetIcon(icon, action);
        return view;
    }

    private async void OnEditorSlotDropRequested(object? sender, RingSlotDropEventArgs e)
    {
        if (e.Payload is RingSlotDrag move)
        {
            await SwapEditorSlotsAsync(move, e.Target);
        }
        else if (e.Payload is ActionCatalogItem item)
        {
            await AssignCatalogItemAsync(e.Target, item);
        }
    }

    internal async Task<bool> SwapEditorSlotsAsync(RingSlotDrag move, RingSlotDefinition target)
    {
        var (_, ring, _) = GetSelectedProfile();
        if (!ReferenceEquals(move.Root, ring) || !RingSlotEditing.CanSwap(ring, move.Source, target)) return false;
        var profileId = _selectedProfileId;
        var sourceId = move.Source.Id;
        var selectionBefore = _selectedSlot?.Id;
        if (!await MutateAndSaveAsync(_ =>
            {
                if (!RingSlotEditing.TrySwap(ring, move.Source, target))
                    throw new InvalidOperationException("The ring positions changed before the move could be saved.");
            }, updateAutostart: false))
        {
            if (_selectedProfileId == profileId)
            {
                _selectedSlot = selectionBefore is null ? null : FindSlotById(_controller.Configuration, profileId, selectionBefore);
                RefreshEditor();
            }
            return false;
        }
        if (_selectedProfileId == profileId)
        {
            _selectedSlot = FindSlotById(_controller.Configuration, profileId, sourceId);
            RefreshEditor();
        }
        return true;
    }

    private async void OnActionItemClick(object sender, RoutedEventArgs e)
    {
        if (_selectedSlot is null)
        {
            ShowStatus("Сначала выберите пузырь в кольце", isError: true);
            return;
        }
        if ((sender as Button)?.Tag is ActionCatalogItem item)
        {
            await AssignCatalogItemAsync(_selectedSlot, item);
        }
    }

    private async Task AssignCatalogItemAsync(RingSlotDefinition target, ActionCatalogItem item)
    {
        var profileId = _selectedProfileId;
        var targetId = target.Id;
        var replacement = item.CreateSlot();
        if (target.Submenu is not null && replacement.Submenu is not null)
        {
            RingEditor.SetSelectedSlot(target);
            ShowStatus("Подменю уже добавлено. Нажмите «Изменить», чтобы настроить его.");
            return;
        }
        if (replacement.Submenu is not null)
        {
            var (_, rootRing, _) = GetSelectedProfile();
            var containingDepth = FindContainingRingDepth(rootRing, target);
            if (containingDepth >= ConfigurationNormalizer.MaximumSubmenuDepth)
            {
                ShowStatus(
                    $"Допустимо не более {ConfigurationNormalizer.MaximumSubmenuDepth} уровней подменю",
                    isError: true);
                return;
            }
        }
        if (item.RequiresConfiguration && replacement.Action is not null)
        {
            var editor = new ActionEditorWindow(replacement.Action, _controller.CaptureShortcutChordAsync) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result is null)
            {
                return;
            }
            replacement = RingSlotDefinition.ForAction(editor.Result);
        }

        replacement = RingSlotEditing.ComposeAssignment(target, replacement)!;

        if (await MutateAndSaveAsync(
                configuration => CopySlot(
                    replacement,
                    FindSlotById(configuration, profileId, targetId)
                    ?? throw new InvalidOperationException("The selected ring slot no longer exists.")),
                updateAutostart: false))
        {
            _selectedSlot = FindSlotById(_controller.Configuration, profileId, targetId);
            RefreshEditor();
        }
    }

    private void RefreshSelectedSlotCard()
    {
        if (_selectedSlot is null)
        {
            SelectedSlotIcon.SetIcon("lucide:plus");
            SelectedSlotTitle.Text = "Выберите пузырь";
            SelectedSlotDescription.Text = "Затем назначьте действие из библиотеки";
            EditSlotButton.IsEnabled = false;
            ClearSlotButton.IsEnabled = false;
            SlotAppearanceButton.IsEnabled = false;
            SlotIconButton.IsEnabled = false;
            SlotAppearanceButton.ToolTip = "Выберите пузырь";
            return;
        }
        SelectedSlotIcon.SetIcon(_selectedSlot.Icon ?? (_selectedSlot.Submenu is not null && _selectedSlot.Action?.Kind is null or ActionKind.None ? "folder" : null), _selectedSlot.Action);
        SelectedSlotTitle.Text = _selectedSlot.Label;
        SelectedSlotDescription.Text = _selectedSlot.Submenu is not null
            ? _selectedSlot.Action is { Kind: not ActionKind.None } primary
                ? $"По клику: {primary.Name} · Подменю: {_selectedSlot.Submenu.SlotCount}"
                : $"Подменю: {_selectedSlot.Submenu.SlotCount} · По клику не назначено"
            : _selectedSlot.Action?.Description ?? DescribeAction(_selectedSlot.Action);
        EditSlotButton.IsEnabled = _selectedSlot.Submenu is not null || _selectedSlot.Action?.Kind is not (null or ActionKind.None);
        ClearSlotButton.IsEnabled = _selectedSlot.Submenu is not null || _selectedSlot.Action?.Kind != ActionKind.None;
        SlotAppearanceButton.IsEnabled = true;
        SlotAppearanceButton.ToolTip = "Индивидуальные цвета пузыря";
        SlotIconButton.IsEnabled = true;
    }

    private async void OnEditSelectedSlotIcon(object sender, RoutedEventArgs e)
    {
        if (_selectedSlot is null)
        {
            return;
        }
        var profileId = _selectedProfileId;
        var targetId = _selectedSlot.Id;
        var picker = new IconPickerWindow(_selectedSlot.Icon) { Owner = this };
        if (picker.ShowDialog() != true)
        {
            return;
        }
        if (await MutateAndSaveAsync(configuration =>
                {
                    var target = FindSlotById(configuration, profileId, targetId)
                                 ?? throw new InvalidOperationException("The selected ring slot no longer exists.");
                    target.Icon = picker.SelectedIcon;
                    if (picker.SelectedIcon is null && target.Action is not null)
                    {
                        target.Action.Icon = null;
                    }
                }, updateAutostart: false))
        {
            _selectedSlot = FindSlotById(_controller.Configuration, profileId, targetId);
            RefreshEditor();
        }
    }

    private async void OnEditSelectedSlot(object sender, RoutedEventArgs e)
    {
        if (_selectedSlot?.Submenu is not null)
        {
            var profileId = _selectedProfileId;
            var targetId = _selectedSlot.Id;
            var editedSlot = Clone(_selectedSlot);
            var editor = new FolderEditorWindow(editedSlot, _controller.CaptureShortcutChordAsync, GetSelectedActionCatalogContext()) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                if (await MutateAndSaveAsync(
                        configuration => CopySlot(
                            editedSlot,
                            FindSlotById(configuration, profileId, targetId)
                            ?? throw new InvalidOperationException("The selected ring slot no longer exists.")),
                        updateAutostart: false))
                {
                    _selectedSlot = FindSlotById(_controller.Configuration, profileId, targetId);
                    RefreshEditor();
                }
            }
            return;
        }
        if (_selectedSlot?.Action is null)
        {
            return;
        }
        var actionForEditing = Clone(_selectedSlot.Action);
        actionForEditing.Icon = _selectedSlot.Icon ?? actionForEditing.Icon;
        var actionEditor = new ActionEditorWindow(actionForEditing, _controller.CaptureShortcutChordAsync) { Owner = this };
        if (actionEditor.ShowDialog() == true && actionEditor.Result is not null)
        {
            var profileId = _selectedProfileId;
            var targetId = _selectedSlot.Id;
            var result = actionEditor.Result;
            if (await MutateAndSaveAsync(
                    configuration =>
                    {
                        var target = FindSlotById(configuration, profileId, targetId)
                                     ?? throw new InvalidOperationException("The selected ring slot no longer exists.");
                        target.Action = result;
                        target.Label = result.Name;
                        target.Icon = result.Icon;
                    },
                    updateAutostart: false))
            {
                _selectedSlot = FindSlotById(_controller.Configuration, profileId, targetId);
                RefreshEditor();
            }
        }
    }

    private async void OnClearSelectedSlot(object sender, RoutedEventArgs e)
    {
        if (_selectedSlot is null)
        {
            return;
        }
        var profileId = _selectedProfileId;
        var targetId = _selectedSlot.Id;
        if (await MutateAndSaveAsync(
                configuration =>
                {
                    var target = FindSlotById(configuration, profileId, targetId)
                                 ?? throw new InvalidOperationException("The selected ring slot no longer exists.");
                    target.Label = "Добавить действие";
                    target.Icon = null;
                    target.Submenu = null;
                    target.Action = ActionDefinition.None();
                },
                updateAutostart: false))
        {
            _selectedSlot = FindSlotById(_controller.Configuration, profileId, targetId);
            RefreshEditor();
        }
    }

    private void OnActionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_refreshing)
        {
            RebuildActionLibrary(ActionSearchBox.Text);
        }
    }

    private void OnClearActionSearch(object sender, RoutedEventArgs e)
    {
        ActionSearchBox.Clear();
        ActionSearchBox.Focus();
    }

    private async void OnRingStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || (RingStyleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() is not { } value
                        || !Enum.TryParse<RingStylePreset>(value, out var preset))
        {
            return;
        }
        var profileId = _selectedProfileId;
        if (preset == RingStylePreset.Custom)
        {
            var (_, ring, _) = GetSelectedProfile();
            var editor = new RingThemeEditorWindow(ring.Appearance) { Owner = this };
            if (editor.ShowDialog() != true)
            {
                _refreshing = true;
                try
                {
                    RefreshEditor();
                }
                finally
                {
                    _refreshing = false;
                }
                return;
            }

            var appearance = editor.EditedAppearance;
            await MutateAndSaveAsync(
                configuration =>
                {
                    GetProfileStyle(configuration, profileId).Preset = RingStylePreset.Custom;
                    GetProfileRing(configuration, profileId).Appearance = appearance;
                },
                updateAutostart: false);
            return;
        }

        await MutateAndSaveAsync(
            configuration => GetProfileStyle(configuration, profileId).Preset = preset,
            updateAutostart: false);
    }

    private async void OnEditRingColors(object sender, RoutedEventArgs e)
    {
        var profileId = _selectedProfileId;
        var (_, ring, _) = GetSelectedProfile();
        var editor = new RingThemeEditorWindow(ring.Appearance) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var appearance = editor.EditedAppearance;
        await MutateAndSaveAsync(
            configuration =>
            {
                GetProfileStyle(configuration, profileId).Preset = RingStylePreset.Custom;
                GetProfileRing(configuration, profileId).Appearance = appearance;
            },
            updateAutostart: false);
    }

    private async void OnEditSelectedSlotAppearance(object sender, RoutedEventArgs e)
    {
        if (_selectedSlot is null)
        {
            return;
        }

        var profileId = _selectedProfileId;
        var targetId = _selectedSlot.Id;
        var (_, ring, style) = GetSelectedProfile();
        var wasCustom = style.Preset == RingStylePreset.Custom;
        var inherited = wasCustom ? ring.Appearance.Clone() : CaptureVisibleRingPalette();
        var editor = new SlotAppearanceEditorWindow(_selectedSlot, inherited)
        {
            Owner = this,
        };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var appearance = editor.EditedAppearanceOverride;
        if (await MutateAndSaveAsync(
                configuration =>
                {
                    var target = FindSlotById(configuration, profileId, targetId)
                                 ?? throw new InvalidOperationException("The selected ring slot no longer exists.");
                    target.AppearanceOverride = appearance;
                    if (!wasCustom)
                    {
                        GetProfileRing(configuration, profileId).Appearance = inherited;
                        GetProfileStyle(configuration, profileId).Preset = RingStylePreset.Custom;
                    }
                },
                updateAutostart: false))
        {
            _selectedSlot = FindSlotById(_controller.Configuration, profileId, targetId);
            RefreshEditor();
        }
    }

    private RingAppearanceDefinition CaptureVisibleRingPalette() => new()
    {
        BubbleColor = RingColor("RingBubbleBrush", RingAppearanceDefinition.DefaultBubbleColor),
        BubbleHoverColor = RingColor("RingBubbleHoverBrush", RingAppearanceDefinition.DefaultBubbleHoverColor),
        IconColor = RingColor("RingIconBrush", RingAppearanceDefinition.DefaultIconColor),
        IconHoverColor = RingColor("RingIconHoverBrush", RingAppearanceDefinition.DefaultIconHoverColor),
    };

    private string RingColor(string resource, string fallback) =>
        RingEditor.TryFindResource(resource) is SolidColorBrush brush ? brush.Color.ToString() : fallback;

    private async void OnResetRing(object sender, RoutedEventArgs e)
    {
        var (name, _, _) = GetSelectedProfile();
        if (MessageBox.Show(
                this,
                $"Вернуть кольцо «{name}» к исходным действиям?",
                "Actions Ring",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var profileId = _selectedProfileId;
        if (await MutateAndSaveAsync(
                configuration =>
                {
                    RingDefaults.RestoreSlots(GetProfileRing(configuration, profileId));
                },
                updateAutostart: false))
        {
            _selectedSlot = null;
            RefreshEditor();
            ShowStatus("Кольцо восстановлено");
        }
    }

    private async void OnUserProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || UserProfileCombo.SelectedValue is not string userProfileId)
        {
            return;
        }
        await SwitchUserProfileAsync(userProfileId);
    }

    private async Task SwitchUserProfileAsync(string userProfileId)
    {
        var selected = _controller.Configuration.UserProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, userProfileId, StringComparison.OrdinalIgnoreCase));
        if (selected is null
            || string.Equals(
                _controller.Configuration.ActiveUserProfileId,
                selected.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (await MutateAndSaveAsync(
                configuration => configuration.ActiveUserProfileId = selected.Id,
                updateAutostart: false))
        {
            _selectedProfileId = selected.GlobalProfile.Id;
            _selectedSlot = null;
            RefreshAll();
            ShowStatus($"Выбран профиль «{selected.Name}»");
        }
    }

    private async void OnAddUserProfile(object sender, RoutedEventArgs e)
    {
        if (_controller.Configuration.UserProfiles.Count >= ConfigurationNormalizer.MaximumUserProfiles)
        {
            ShowStatus($"Можно создать не более {ConfigurationNormalizer.MaximumUserProfiles} профилей", isError: true);
            return;
        }

        var dialog = new ProfileNameWindow("Новый профиль", "Новый профиль") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var userProfile = ConfigurationDefaults.CreateDefaultUserProfile();
        RegenerateUserProfileIds(userProfile);
        userProfile.Name = dialog.ProfileName;
        if (await MutateAndSaveAsync(
                configuration =>
                {
                    configuration.UserProfiles.Add(userProfile);
                    configuration.ActiveUserProfileId = userProfile.Id;
                },
                updateAutostart: false))
        {
            _selectedProfileId = userProfile.GlobalProfile.Id;
            _selectedSlot = null;
            RefreshAll();
        }
    }

    private async Task RenameUserProfileAsync(UserProfile profile)
    {
        var dialog = new ProfileNameWindow("Переименовать профиль", profile.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await MutateAndSaveAsync(
            configuration =>
            {
                var target = configuration.UserProfiles.First(item => item.Id == profile.Id);
                target.Name = dialog.ProfileName;
            },
            updateAutostart: false);
    }

    private async Task DuplicateUserProfileAsync(UserProfile profile)
    {
        if (_controller.Configuration.UserProfiles.Count >= ConfigurationNormalizer.MaximumUserProfiles)
        {
            ShowStatus($"Можно создать не более {ConfigurationNormalizer.MaximumUserProfiles} профилей", isError: true);
            return;
        }
        var dialog = new ProfileNameWindow("Дублировать профиль", profile.Name + " — копия") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var duplicate = Clone(profile);
        RegenerateUserProfileIds(duplicate);
        duplicate.Name = dialog.ProfileName;
        if (await MutateAndSaveAsync(
                configuration => configuration.UserProfiles.Add(duplicate),
                updateAutostart: false))
        {
            RefreshAll();
        }
    }

    private async Task DeleteUserProfileAsync(UserProfile profile)
    {
        if (_controller.Configuration.UserProfiles.Count <= 1)
        {
            ShowStatus("Должен остаться хотя бы один профиль", isError: true);
            return;
        }
        if (MessageBox.Show(
                this,
                $"Удалить профиль «{profile.Name}» со всеми его кольцами?",
                "Actions Ring",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var replacement = _controller.Configuration.UserProfiles.First(item => item.Id != profile.Id);
        if (await MutateAndSaveAsync(
                configuration =>
                {
                    configuration.UserProfiles.RemoveAll(item => item.Id == profile.Id);
                    if (configuration.ActiveUserProfileId == profile.Id)
                    {
                        configuration.ActiveUserProfileId = replacement.Id;
                    }
                },
                updateAutostart: false))
        {
            _selectedProfileId = _controller.Configuration.GetActiveUserProfile().GlobalProfile.Id;
            _selectedSlot = null;
            RefreshAll();
        }
    }

    private async void OnAddProfile(object sender, RoutedEventArgs e)
    {
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        if (userProfile.ApplicationProfiles.Count >= ConfigurationNormalizer.MaximumProfiles)
        {
            ShowStatus($"Можно создать не более {ConfigurationNormalizer.MaximumProfiles} профилей", isError: true);
            return;
        }

        var dialog = new InstalledApplicationPickerWindow(_applicationDiscovery, _visuals) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedApplication is not { } application)
        {
            return;
        }
        await AddApplicationProfileAsync(application);
    }

    private async void OnAddActiveApplication(object sender, RoutedEventArgs e)
    {
        Hide();
        await Task.Delay(450);
        var active = new ForegroundWindowResolver().GetActiveWindow();
        ShowFromTray();
        if (active is null
            || active.ProcessId == checked((uint)Environment.ProcessId)
            || string.IsNullOrWhiteSpace(active.ProcessName))
        {
            ShowStatus("Не удалось определить активное приложение", isError: true);
            return;
        }

        var executable = active.ExecutablePath;
        var application = new InstalledApplicationInfo(
            active.ProductName ?? active.ProcessName,
            executable ?? active.ProcessName + ".exe",
            active.ProcessName,
            executable,
            IconPath: executable);
        await AddApplicationProfileAsync(application);
    }

    private async Task AddApplicationProfileAsync(InstalledApplicationInfo application)
    {
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        if (userProfile.ApplicationProfiles.Count >= ConfigurationNormalizer.MaximumProfiles)
        {
            ShowStatus($"Можно добавить не более {ConfigurationNormalizer.MaximumProfiles} приложений", isError: true);
            return;
        }

        var iconPath = await _visuals.CacheApplicationIconAsync(application);
        var rules = new List<ApplicationMatchRule>();
        if (!string.IsNullOrWhiteSpace(application.ExecutablePath))
        {
            rules.Add(new ApplicationMatchRule
            {
                Kind = ApplicationMatchKind.ExecutablePath,
                Mode = TextMatchMode.Equals,
                Pattern = application.ExecutablePath,
            });
        }
        if (!string.IsNullOrWhiteSpace(application.ProcessName))
        {
            rules.Add(new ApplicationMatchRule
            {
                Kind = ApplicationMatchKind.ProcessName,
                Mode = TextMatchMode.Equals,
                Pattern = application.ProcessName,
            });
        }
        if (rules.Count == 0)
        {
            ShowStatus("Для приложения не удалось определить правило", isError: true);
            return;
        }

        var profile = new ApplicationProfile
        {
            Name = application.Name,
            Priority = (userProfile.ApplicationProfiles.Count + 1) * 10,
            MatchRules = rules,
            MatchAllRules = false,
            AppUserModelId = application.AppUserModelId,
            LaunchTarget = application.LaunchTarget,
            IconPath = iconPath,
            RootRing = Clone(userProfile.GlobalProfile.RootRing),
            Style = GuessProfileStyle(application.ExecutablePath ?? application.LaunchTarget),
        };
        RegenerateApplicationProfileIds(profile);
        if (await MutateAndSaveAsync(
                configuration => configuration.GetActiveUserProfile().ApplicationProfiles.Add(profile),
                updateAutostart: false))
        {
            _selectedProfileId = profile.Id;
            _selectedSlot = null;
            RingNav.IsChecked = true;
            NavigateTo("Ring");
            RefreshAll();
        }
    }

    private async void OnCaptureTrigger(object sender, RoutedEventArgs e)
    {
        if (_configurationMutationInProgress)
        {
            ShowStatus("Дождитесь сохранения предыдущего изменения", isError: true);
            RefreshTrigger();
            return;
        }

        _configurationMutationInProgress = true;
        CaptureTriggerButton.IsEnabled = false;
        OnboardingCaptureButton.IsEnabled = false;
        CaptureTriggerButton.Content = "Нажмите ввод…";
        try
        {
            var mode = HoldModeRadio.IsChecked == true ? ActivationMode.Hold : ActivationMode.Toggle;
            await _controller.CaptureTriggerAsync(mode);
            CommitPendingAppearanceTransaction();
            RefreshTrigger();
            ShowStatus("Новый ввод сохранён");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Запись отменена");
        }
        catch (InvalidDataException exception)
        {
            ShowStatus(exception.Message, isError: true);
        }
        catch (Exception exception)
        {
            AppLog.Error("Trigger capture failed", exception);
            ShowStatus("Не удалось записать ввод", isError: true);
        }
        finally
        {
            _configurationMutationInProgress = false;
            CaptureTriggerButton.IsEnabled = true;
            OnboardingCaptureButton.IsEnabled = true;
            CaptureTriggerButton.Content = "Записать новый ввод";
        }
    }

    private async void OnActivationModeChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || HoldModeRadio is null || ToggleModeRadio is null)
        {
            return;
        }
        var requestedMode = HoldModeRadio.IsChecked == true ? ActivationMode.Hold : ActivationMode.Toggle;
        await MutateAndSaveAsync(
            configuration =>
            {
                var trigger = configuration.Trigger;
                trigger.ActivationMode = trigger.Button is CoreMouseButton.WheelUp
                    or CoreMouseButton.WheelDown
                    or CoreMouseButton.WheelLeft
                    or CoreMouseButton.WheelRight
                    ? ActivationMode.Toggle
                    : requestedMode;
            },
            updateAutostart: false);
    }

    private async void OnPreviewRing(object sender, RoutedEventArgs e)
    {
        var (_, ring, style) = GetSelectedProfile();
        await _controller.PreviewRingAsync(ring, style);
    }

    private async void OnSettingsToggle(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }
        var runAtStartup = StartupToggle.IsChecked == true;
        var startMinimized = StartMinimizedToggle.IsChecked == true;
        var closeToTray = CloseToTrayToggle.IsChecked == true;
        var showKeyStateNotifications = KeyStateNotificationsToggle.IsChecked == true;
        var autoScale = AutoScaleToggle.IsChecked == true;
        var showTooltips = TooltipsToggle.IsChecked == true;
        var enableAnimations = AnimationsToggle.IsChecked == true;
        var reduceMotion = ReduceMotionToggle.IsChecked == true;
        await MutateAndSaveAsync(
            configuration =>
            {
                var general = configuration.Preferences.General;
                var appearance = configuration.Preferences.Appearance;
                general.RunAtStartup = runAtStartup;
                general.StartMinimized = startMinimized;
                general.CloseToTray = closeToTray;
                general.ShowKeyStateNotifications = showKeyStateNotifications;
                appearance.AutoScaleRing = autoScale;
                appearance.ShowTooltips = showTooltips;
                appearance.EnableAnimations = enableAnimations;
                appearance.ReduceMotion = reduceMotion;
            },
            updateAutostart: true);
    }

    private async void OnUpdateSettingsToggle(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        var checkAutomatically = AutoCheckUpdatesToggle.IsChecked == true;
        var downloadAutomatically = AutoDownloadUpdatesToggle.IsChecked == true;
        if (ReferenceEquals(sender, AutoDownloadUpdatesToggle) && downloadAutomatically)
        {
            checkAutomatically = true;
        }
        if (!checkAutomatically)
        {
            downloadAutomatically = false;
        }

        await MutateAndSaveAsync(
            configuration =>
            {
                configuration.Preferences.Updates.CheckAutomatically = checkAutomatically;
                configuration.Preferences.Updates.DownloadAutomatically = downloadAutomatically;
            },
            updateAutostart: false);
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) =>
        await ExecuteUpdateCheckAsync(manual: true);

    private async Task ExecuteUpdateCheckAsync(bool manual)
    {
        bool entered;
        try
        {
            entered = await _updateGate.WaitAsync(0, _updateLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!entered)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Проверяем…";
        UpdateStatusText.Text = "Подключаемся к официальному репозиторию…";
        SetVersionStatus("Проверка…", "TextSecondaryBrush", "SurfaceRaisedBrush");
        try
        {
            UpdateCheckResult check;
            StagedUpdatePackage? stagedPackage = null;
            if (manual)
            {
                check = await _updateService.CheckForUpdatesAsync(
                    _controller.Configuration.Preferences.Updates.SkippedVersion,
                    includeSkipped: true,
                    cancellationToken: _updateLifetime.Token);
            }
            else
            {
                var source = _controller.Configuration.Preferences.Updates;
                var snapshot = new UpdatePreferences
                {
                    CheckAutomatically = source.CheckAutomatically,
                    DownloadAutomatically = source.DownloadAutomatically,
                    SkippedVersion = source.SkippedVersion,
                    LastCheckedAtUtc = source.LastCheckedAtUtc,
                };
                var automatic = await _updateService.RunAutomaticCheckAsync(
                    snapshot,
                    progress: null,
                    cancellationToken: _updateLifetime.Token);
                check = automatic.Check;
                stagedPackage = automatic.Stage?.Package;
            }

            if (check.Status != UpdateCheckStatus.AutomaticCheckDisabled)
            {
                await MutateAndSaveAsync(
                    configuration =>
                    {
                        var updates = configuration.Preferences.Updates;
                        updates.LastCheckedAtUtc = check.CheckedAtUtc;
                        if (SemanticVersion.TryParse(updates.SkippedVersion, out var skipped)
                            && skipped.CompareTo(_updateService.CurrentVersion) <= 0)
                        {
                            updates.SkippedVersion = null;
                        }
                    },
                    updateAutostart: false);
            }

            await PresentUpdateCheckResultAsync(check, stagedPackage, manual);
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("Update check UI failed", exception);
            UpdateStatusText.Text = "Не удалось проверить обновления. Попробуйте ещё раз.";
            SetVersionStatus("Ошибка проверки", "DangerBrush", "SurfaceRaisedBrush");
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            CheckUpdatesButton.Content = "Проверить обновления";
            _updateGate.Release();
        }
    }

    private async Task PresentUpdateCheckResultAsync(
        UpdateCheckResult check,
        StagedUpdatePackage? stagedPackage,
        bool manual)
    {
        UpdateStatusText.Text = check.UserMessage;
        switch (check.Status)
        {
            case UpdateCheckStatus.UpToDate:
                SetVersionStatus("Актуальная версия", "SuccessBrush", "SurfaceRaisedBrush");
                if (AboutPage.Visibility == Visibility.Visible)
                {
                    ShowStatus("Установлена актуальная версия");
                }
                return;
            case UpdateCheckStatus.Skipped:
                SetVersionStatus("Версия пропущена", "TextSecondaryBrush", "SurfaceRaisedBrush");
                return;
            case UpdateCheckStatus.Failed:
                SetVersionStatus("Ошибка проверки", "DangerBrush", "SurfaceRaisedBrush");
                if (manual)
                {
                    ShowStatus(check.UserMessage, isError: true);
                }
                return;
            case UpdateCheckStatus.AutomaticCheckDisabled:
                SetVersionStatus("Автопроверка выключена", "TextSecondaryBrush", "SurfaceRaisedBrush");
                return;
            case UpdateCheckStatus.UpdateAvailable when check.LatestRelease is not null:
                SetVersionStatus($"Доступна {check.LatestRelease.Version}", "AccentBrush", "AccentSoftBrush");
                break;
            default:
                return;
        }

        var dialog = new UpdateAvailableWindow(_updateService, check.LatestRelease, stagedPackage);
        _activeUpdateDialog = dialog;
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            dialog.Owner = this;
        }
        else
        {
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            _activeUpdateDialog = null;
        }

        if (dialog.Decision == UpdateDecision.Skip)
        {
            if (!await MutateAndSaveAsync(
                configuration => configuration.Preferences.Updates.SkippedVersion =
                    check.LatestRelease.Version.ToString(),
                updateAutostart: false))
            {
                UpdateStatusText.Text = $"Версия {check.LatestRelease.Version} по-прежнему доступна.";
                SetVersionStatus($"Доступна {check.LatestRelease.Version}", "AccentBrush", "AccentSoftBrush");
                return;
            }
            UpdateStatusText.Text = $"Версия {check.LatestRelease.Version} пропущена.";
            SetVersionStatus("Версия пропущена", "TextSecondaryBrush", "SurfaceRaisedBrush");
            return;
        }

        if (dialog.Decision == UpdateDecision.Install && dialog.Package is { } package)
        {
            UpdateStatusText.Text = "Обновление готово. Actions Ring перезапустится после установки.";
            UpdateInstallRequested?.Invoke(this, new UpdateInstallRequestedEventArgs(package));
        }
    }

    private void SetVersionStatus(string text, string foregroundResource, string backgroundResource)
    {
        VersionStatusText.Text = text;
        VersionStatusText.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
        VersionStatusBadge.SetResourceReference(Border.BackgroundProperty, backgroundResource);
    }

    internal static bool IsAutomaticUpdateCheckDue(DateTimeOffset? lastCheckedAtUtc, DateTimeOffset nowUtc)
    {
        if (lastCheckedAtUtc is null || lastCheckedAtUtc > nowUtc.AddMinutes(5))
        {
            return true;
        }
        return nowUtc - lastCheckedAtUtc.Value >= TimeSpan.FromHours(24);
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() is not { } value
                        || !Enum.TryParse<ThemePreference>(value, out var preference))
        {
            return;
        }
        await MutateAndSaveAsync(
            configuration => configuration.Preferences.Appearance.Theme = preference,
            updateAutostart: false);
    }

    private void OnRingSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || RingSizeLabel is null)
        {
            return;
        }
        if (_configurationMutationInProgress)
        {
            RefreshAll();
            return;
        }
        var value = (int)Math.Round(e.NewValue);
        BeginPendingAppearanceTransaction();
        _controller.Configuration.Preferences.Appearance.RingDiameter = value;
        UpdateRingSizeLabel(value);
        RingEditor.RingScale = 1.0;
        RingEditor.Present(GetSelectedProfile().Ring, animate: false);
        _appearanceDirty = true;
        _saveAppearanceTimer.Stop();
        _saveAppearanceTimer.Start();
    }

    private void UpdateRingSizeLabel(int value) =>
        RingSizeLabel.Text = $"{Math.Round(value / 212d * 100)}% · {value} DIP" +
                             (_controller.Configuration.Preferences.Appearance.AutoScaleRing ? " · авто" : string.Empty);

    private void OnCenterSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || CenterSizeLabel is null)
        {
            return;
        }
        if (_configurationMutationInProgress)
        {
            RefreshAll();
            return;
        }
        var value = (int)Math.Round(e.NewValue);
        BeginPendingAppearanceTransaction();
        _controller.Configuration.Preferences.Appearance.CenterCloseDiameter = value;
        RingEditor.CenterDiameter = 32;
        RingEditor.Present(GetSelectedProfile().Ring, animate: false);
        UpdateCenterSizeLabel(value);
        _appearanceDirty = true;
        _saveAppearanceTimer.Stop();
        _saveAppearanceTimer.Start();
    }

    private void OnTooltipDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || TooltipDelayLabel is null)
        {
            return;
        }
        if (_configurationMutationInProgress)
        {
            RefreshAll();
            return;
        }
        var value = (int)Math.Round(e.NewValue);
        BeginPendingAppearanceTransaction();
        _controller.Configuration.Preferences.Appearance.TooltipDelayMilliseconds = value;
        UpdateTooltipDelayLabel(value);
        _appearanceDirty = true;
        _saveAppearanceTimer.Stop();
        _saveAppearanceTimer.Start();
    }

    private void UpdateCenterSizeLabel(int value) => CenterSizeLabel.Text = $"{value} DIP";

    private void UpdateTooltipDelayLabel(int value) =>
        TooltipDelayLabel.Text = value == 0 ? "Сразу" : $"{value} мс";

    private async void OnExportConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт настроек Actions Ring",
            Filter = "Actions Ring JSON (*.json)|*.json",
            FileName = $"ActionsRing-{DateTime.Now:yyyy-MM-dd}.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var snapshot = ConfigurationNormalizer.Normalize(
                ConfigurationJson.Clone(_controller.Configuration)).Configuration;
            var validation = ConfigurationValidator.Validate(snapshot);
            if (!validation.IsValid)
            {
                throw new InvalidDataException("Настройки содержат недопустимые значения.");
            }

            var json = ConfigurationJson.Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(json) > ConfigurationDocumentParser.MaximumFileSizeBytes)
            {
                throw new InvalidDataException("Файл настроек слишком большой для экспорта.");
            }

            await WriteExportAtomicallyAsync(dialog.FileName, json);
            ShowStatus("Настройки экспортированы");
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            AppLog.Error("Configuration export failed", exception);
            MessageBox.Show(
                this,
                "Не удалось сохранить файл настроек в выбранную папку.",
                "Экспорт не выполнен",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnImportConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт настроек Actions Ring",
            Filter = "Actions Ring JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            await using var stream = new FileStream(
                dialog.FileName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var parsed = await ConfigurationDocumentParser.ParseAsync(stream);
            var normalized = parsed.Configuration;
            if (MessageBox.Show(this, "Заменить текущие профили и настройки импортированными?", "Actions Ring", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            var requestedStartup = normalized.Preferences.General.RunAtStartup;
            if (!await MutateAndSaveAsync(
                    configuration => CopyConfiguration(normalized, configuration),
                    updateAutostart: true))
            {
                return;
            }
            _selectedProfileId = _controller.Configuration.GetActiveUserProfile().GlobalProfile.Id;
            _selectedSlot = null;
            RefreshAll();
            var startupChanged = requestedStartup != _controller.Configuration.Preferences.General.RunAtStartup;
            ShowStatus(
                startupChanged
                    ? "Настройки импортированы; автозапуск оставлен в состоянии Windows"
                    : "Настройки импортированы",
                isError: startupChanged);
        }
        catch (UnsupportedConfigurationVersionException)
        {
            MessageBox.Show(
                this,
                "Версия этого файла настроек не поддерживается установленной версией Actions Ring.",
                "Импорт не выполнен",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            AppLog.Error("Configuration import failed", exception);
            MessageBox.Show(this, "Файл не похож на корректную конфигурацию Actions Ring.", "Импорт не выполнен", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.CommandParameter is string page)
        {
            NavigateTo(page);
        }
    }

    private void NavigateTo(string page)
    {
        EditorPage.Visibility = page == "Ring" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        TriggerPage.Visibility = page == "Trigger" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        PageCaption.Text = page switch
        {
            "Profiles" => "Профили приложений",
            "Trigger" => "Вызов кольца",
            "Settings" => "Настройки",
            "About" => "О программе",
            _ => "Кольцо действий",
        };
    }

    private void ShowOnboarding(int step)
    {
        _onboardingStep = Math.Clamp(step, 1, 3);
        OnboardingLayer.Visibility = Visibility.Visible;
        OnboardingEyebrow.Text = $"ШАГ {_onboardingStep} ИЗ 3";
        StepDot1.Fill = _onboardingStep >= 1 ? FindBrush("AccentBrush", Brushes.MediumPurple) : FindBrush("BorderBrush", Brushes.Gray);
        StepDot2.Fill = _onboardingStep >= 2 ? FindBrush("AccentBrush", Brushes.MediumPurple) : FindBrush("BorderBrush", Brushes.Gray);
        StepDot3.Fill = _onboardingStep >= 3 ? FindBrush("AccentBrush", Brushes.MediumPurple) : FindBrush("BorderBrush", Brushes.Gray);
        OnboardingBackButton.Visibility = _onboardingStep > 1 ? Visibility.Visible : Visibility.Hidden;
        OnboardingTriggerCard.Visibility = _onboardingStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        switch (_onboardingStep)
        {
            case 1:
                OnboardingTitle.Text = "Действия всегда под курсором";
                OnboardingBody.Text = "Вызовите кольцо, наведитесь на пузырь и отпустите кнопку. Профиль автоматически меняется вместе с активным приложением.";
                OnboardingNextButton.Content = "Далее";
                break;
            case 2:
                OnboardingTitle.Text = "Назначьте удобный ввод";
                OnboardingBody.Text = "По умолчанию используется дальняя боковая кнопка мыши — XButton2. Можно записать любую кнопку, клавишу или сочетание.";
                OnboardingNextButton.Content = "Далее";
                break;
            case 3:
                OnboardingTitle.Text = "Готово для каждого приложения";
                OnboardingBody.Text = "Создайте отдельные кольца для браузера, Photoshop и любых других программ. Actions Ring останется в трее и подберёт профиль автоматически.";
                OnboardingNextButton.Content = "Начать";
                break;
        }
        OnboardingRing.InteractionMode = RingInteractionMode.Execute;
        OnboardingRing.RingScale = 0.88;
        OnboardingRing.ApplyStyle(_controller.Configuration.GlobalProfile.Style);
        OnboardingRing.Present(_controller.Configuration.GlobalProfile.RootRing, animate: true);
    }

    private async void OnOnboardingNext(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep < 3)
        {
            ShowOnboarding(_onboardingStep + 1);
            return;
        }
        await CompleteOnboardingAsync();
    }

    private void OnOnboardingBack(object sender, RoutedEventArgs e) => ShowOnboarding(_onboardingStep - 1);

    private async void OnSkipOnboarding(object sender, RoutedEventArgs e) => await CompleteOnboardingAsync();

    private async Task CompleteOnboardingAsync()
    {
        if (await MutateAndSaveAsync(
                configuration =>
                {
                    var onboarding = configuration.Onboarding;
                    onboarding.WelcomeCompleted = true;
                    onboarding.TriggerSetupCompleted = true;
                    onboarding.IsCompleted = true;
                    onboarding.LastCompletedStep = 3;
                    onboarding.CompletedAtUtc = DateTimeOffset.UtcNow;
                    onboarding.LastSeenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
                },
                updateAutostart: false))
        {
            OnboardingLayer.Visibility = Visibility.Collapsed;
            ShowStatus("Actions Ring готов к работе");
        }
    }

    private async Task<bool> MutateAndSaveAsync(
        Action<ActionsRingConfiguration> mutation,
        bool updateAutostart)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (_configurationMutationInProgress)
        {
            ShowStatus("Дождитесь сохранения предыдущего изменения", isError: true);
            RefreshAll();
            return false;
        }

        _configurationMutationInProgress = true;
        var selectedProfileBefore = _selectedProfileId;
        var hadPendingAppearance = _appearanceDirty;
        _saveAppearanceTimer.Stop();
        var transaction = ConfigurationMutationTransaction.Capture(_controller.Configuration);
        try
        {
            mutation(_controller.Configuration);
            if (await transaction.TryCommitAsync(() => SaveSafelyAsync(updateAutostart)))
            {
                CommitPendingAppearanceTransaction();
                return true;
            }

            RestoreAfterConfigurationRollback(selectedProfileBefore);
            if (hadPendingAppearance)
            {
                _saveAppearanceTimer.Start();
            }
            return false;
        }
        catch (Exception exception)
        {
            if (transaction.IsActive)
            {
                transaction.Rollback();
            }
            AppLog.Error("Configuration mutation failed", exception);
            RestoreAfterConfigurationRollback(selectedProfileBefore);
            if (hadPendingAppearance)
            {
                _saveAppearanceTimer.Start();
            }
            ShowStatus("Не удалось применить изменение", isError: true);
            return false;
        }
        finally
        {
            _configurationMutationInProgress = false;
        }
    }

    private async Task<bool> SaveSafelyAsync(bool updateAutostart)
    {
        try
        {
            await _controller.SaveAndApplyAsync(updateAutostart);
            return true;
        }
        catch (AutostartApplyException exception)
        {
            AppLog.Error("Could not apply autostart preference", exception);
            ShowStatus(
                exception.ConfigurationPersisted
                    ? "Настройки сохранены; автозапуск оставлен в состоянии Windows"
                    : "Не удалось полностью сохранить настройку автозапуска",
                isError: true);
            // The main configuration was durably written before applying the
            // Windows startup entry. Keep that transaction committed even when
            // persisting the subsequently reconciled Windows state also fails.
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not save configuration", exception);
            ShowStatus("Не удалось сохранить настройки", isError: true);
            return false;
        }
    }

    private async Task FlushPendingAppearanceSaveAsync()
    {
        if (!_appearanceDirty || _pendingAppearanceTransaction is null)
        {
            return;
        }

        if (_configurationMutationInProgress)
        {
            _saveAppearanceTimer.Start();
            return;
        }

        _configurationMutationInProgress = true;
        var transaction = _pendingAppearanceTransaction;
        var selectedProfileBefore = _selectedProfileId;
        try
        {
            if (await transaction.TryCommitAsync(() => SaveSafelyAsync(updateAutostart: false)))
            {
                _appearanceDirty = false;
                _pendingAppearanceTransaction = null;
                return;
            }

            _appearanceDirty = false;
            _pendingAppearanceTransaction = null;
            RestoreAfterConfigurationRollback(selectedProfileBefore);
        }
        catch (Exception exception)
        {
            if (transaction.IsActive)
            {
                transaction.Rollback();
            }
            _appearanceDirty = false;
            _pendingAppearanceTransaction = null;
            AppLog.Error("Appearance configuration mutation failed", exception);
            RestoreAfterConfigurationRollback(selectedProfileBefore);
            ShowStatus("Не удалось применить оформление", isError: true);
        }
        finally
        {
            _configurationMutationInProgress = false;
        }
    }

    private void BeginPendingAppearanceTransaction()
    {
        _pendingAppearanceTransaction ??=
            ConfigurationMutationTransaction.Capture(_controller.Configuration);
    }

    private void CommitPendingAppearanceTransaction()
    {
        _saveAppearanceTimer.Stop();
        _appearanceDirty = false;
        if (_pendingAppearanceTransaction is not null)
        {
            _pendingAppearanceTransaction.Commit();
            _pendingAppearanceTransaction = null;
        }
    }

    private void RestoreAfterConfigurationRollback(string selectedProfileBefore)
    {
        _selectedProfileId = selectedProfileBefore;
        _selectedSlot = null;
        try
        {
            _controller.ReapplyRuntimeConfiguration();
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not restore runtime configuration after rollback", exception);
        }
        RefreshAll();
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusToastText.Text = message;
        StatusToast.Background = isError ? FindBrush("DangerBrush", Brushes.IndianRed) : FindBrush("OverlayHintBrush", Brushes.Black);
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.45))));
        StatusToast.BeginAnimation(OpacityProperty, animation);
    }

    private void RefreshRuntimeStatus()
    {
        RuntimeStatus.Text = _controller.IsPaused ? "Кольцо приостановлено" : "Кольцо активно";
        RuntimeDot.Fill = _controller.IsPaused
            ? FindBrush("TextMutedBrush", Brushes.Gray)
            : FindBrush("SuccessBrush", Brushes.MediumSeaGreen);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnWindowStateChanged(object? sender, EventArgs e) =>
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\u2750" : "\u25A1";
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || Application.Current.Dispatcher.HasShutdownStarted)
        {
            return;
        }
        if (_controller.Configuration.Preferences.General.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            ShowStatus("Actions Ring продолжает работать в трее");
            return;
        }
        _allowClose = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_controller.SettingsPath)!;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Log opened from settings.");
        Process.Start(new ProcessStartInfo(AppLog.CurrentPath) { UseShellExecute = true });
    }

    private void OnReportBug(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(BugReportService.CreateIssueUri().AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Could not open the bug report form", exception);
            ShowStatus("Не удалось открыть браузер. Проверьте браузер по умолчанию.", isError: true);
        }
    }

    private (string Name, RingDefinition Ring, RingStyleDefinition Style) GetSelectedProfile()
    {
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        var profile = userProfile.ApplicationProfiles.FirstOrDefault(item => item.Id == _selectedProfileId);
        return profile is null
            ? (userProfile.GlobalProfile.Name, userProfile.GlobalProfile.RootRing, userProfile.GlobalProfile.Style)
            : (profile.Name, profile.RootRing, profile.Style);
    }

    private ActionCatalogContext? GetSelectedActionCatalogContext()
    {
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        var profile = userProfile.ApplicationProfiles.FirstOrDefault(item => item.Id == _selectedProfileId);
        if (profile is null)
        {
            return null;
        }

        var processName = profile.MatchRules.FirstOrDefault(rule =>
            rule.Kind == ApplicationMatchKind.ProcessName)?.Pattern;
        var executablePath = profile.MatchRules.FirstOrDefault(rule =>
            rule.Kind == ApplicationMatchKind.ExecutablePath)?.Pattern;
        if (string.IsNullOrWhiteSpace(executablePath)
            && profile.LaunchTarget is { } launchTarget
            && !launchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            executablePath = launchTarget;
        }
        return new ActionCatalogContext(processName, executablePath, profile.Name);
    }

    private void EnsureSelectedProfileExists()
    {
        var userProfile = _controller.Configuration.GetActiveUserProfile();
        if (_selectedProfileId != userProfile.GlobalProfile.Id
            && userProfile.ApplicationProfiles.All(item => item.Id != _selectedProfileId))
        {
            _selectedProfileId = userProfile.GlobalProfile.Id;
        }
    }

    private static string DescribeAction(ActionDefinition? action) => action?.Kind switch
    {
        ActionKind.None or null => "Пустой пузырь",
        ActionKind.KeyboardShortcut => "Сочетание клавиш",
        ActionKind.LaunchApplication => action.LaunchApplication?.ExecutablePath ?? "Приложение",
        ActionKind.OpenUri => action.OpenUri?.Uri ?? "Ссылка",
        ActionKind.TypeText => "Ввод текста",
        ActionKind.Sequence => $"{action.Sequence?.Steps.Count ?? 0} шагов",
        ActionKind.AdjustParameter => "Регулировка колесом",
        _ => "Системное действие",
    };

    private RingStyleDefinition GuessProfileStyle(string executable) =>
        executable.Contains("photoshop", StringComparison.OrdinalIgnoreCase)
            ? new RingStyleDefinition { Preset = RingStylePreset.Ocean }
            : executable.Contains("premiere", StringComparison.OrdinalIgnoreCase)
                ? new RingStyleDefinition { Preset = RingStylePreset.Purple }
                : new RingStyleDefinition { Preset = RingStylePreset.Inherit };

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, ConfigurationJson.Options);
        return JsonSerializer.Deserialize<T>(json, ConfigurationJson.Options)
               ?? throw new InvalidDataException("Could not clone configuration value.");
    }

    private static void CopyConfiguration(ActionsRingConfiguration source, ActionsRingConfiguration target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.Trigger = source.Trigger;
        target.UserProfiles = source.UserProfiles;
        target.ActiveUserProfileId = source.ActiveUserProfileId;
        target.Preferences = source.Preferences;
        target.Onboarding = source.Onboarding;
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0
            ? "•"
            : string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    private static void RegenerateUserProfileIds(UserProfile profile)
    {
        profile.Id = ConfigurationIds.New("user");
        profile.GlobalProfile.Id = ConfigurationIds.New("profile");
        RegenerateRingIds(profile.GlobalProfile.RootRing);
        foreach (var applicationProfile in profile.ApplicationProfiles)
        {
            RegenerateApplicationProfileIds(applicationProfile);
        }
    }

    private static void RegenerateApplicationProfileIds(ApplicationProfile profile)
    {
        profile.Id = ConfigurationIds.New("profile");
        RegenerateRingIds(profile.RootRing);
    }

    private static void RegenerateRingIds(RingDefinition ring)
    {
        ring.Id = ConfigurationIds.New("ring");
        foreach (var slot in ring.Slots)
        {
            slot.Id = ConfigurationIds.New("slot");
            if (slot.Submenu is not null)
            {
                RegenerateRingIds(slot.Submenu);
            }
            if (slot.Action is not null)
            {
                RegenerateActionIds(slot.Action);
            }
        }
    }

    private static void RegenerateActionIds(ActionDefinition action)
    {
        action.Id = ConfigurationIds.New("action");
        if (action.Sequence is null)
        {
            return;
        }
        foreach (var step in action.Sequence.Steps)
        {
            step.Id = ConfigurationIds.New("step");
            RegenerateActionIds(step.Action);
        }
    }

    private static RingStyleDefinition GetProfileStyle(
        ActionsRingConfiguration configuration,
        string profileId)
    {
        var userProfile = configuration.GetActiveUserProfile();
        var profile = userProfile.ApplicationProfiles.FirstOrDefault(item => item.Id == profileId);
        return profile?.Style ?? userProfile.GlobalProfile.Style;
    }

    private static RingDefinition GetProfileRing(
        ActionsRingConfiguration configuration,
        string profileId)
    {
        var userProfile = configuration.GetActiveUserProfile();
        if (string.Equals(profileId, userProfile.GlobalProfile.Id, StringComparison.Ordinal))
        {
            return userProfile.GlobalProfile.RootRing;
        }

        return userProfile.ApplicationProfiles
            .First(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal))
            .RootRing;
    }

    private static RingSlotDefinition? FindSlotById(
        ActionsRingConfiguration configuration,
        string profileId,
        string slotId)
    {
        var userProfile = configuration.GetActiveUserProfile();
        var ring = string.Equals(profileId, userProfile.GlobalProfile.Id, StringComparison.Ordinal)
            ? userProfile.GlobalProfile.RootRing
            : userProfile.ApplicationProfiles
                .FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal))
                ?.RootRing;
        return ring is null ? null : FindSlotById(ring, slotId);
    }

    private static RingSlotDefinition? FindSlotById(RingDefinition ring, string slotId)
    {
        foreach (var slot in ring.Slots)
        {
            if (string.Equals(slot.Id, slotId, StringComparison.Ordinal))
            {
                return slot;
            }

            if (slot.Submenu is not null && FindSlotById(slot.Submenu, slotId) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void CopySlot(RingSlotDefinition source, RingSlotDefinition target)
    {
        target.Label = source.Label;
        target.Icon = source.Icon;
        target.Action = source.Action;
        target.Submenu = source.Submenu;
    }

    private static int FindContainingRingDepth(
        RingDefinition ring,
        RingSlotDefinition target,
        int depth = 0)
    {
        foreach (var slot in ring.Slots)
        {
            if (ReferenceEquals(slot, target))
            {
                return depth;
            }
            if (slot.Submenu is not null)
            {
                var nested = FindContainingRingDepth(slot.Submenu, target, depth + 1);
                if (nested >= 0)
                {
                    return nested;
                }
            }
        }
        return -1;
    }

    private static async Task WriteExportAtomicallyAsync(string destinationPath, string contents)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("У выбранного файла нет папки.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The export result is already decided; a stale temp file is harmless.
            }
        }
    }

    private static T Place<T>(T element, int column) where T : UIElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private static void SelectComboByTag(ComboBox comboBox, string tag)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
