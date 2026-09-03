using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Services;

namespace ActionsRing.App.Windows;

public enum UpdateDecision
{
    Later,
    Skip,
    Install,
}

public partial class UpdateAvailableWindow : Window
{
    private static readonly Regex MarkdownLinkPattern = new(
        @"\[([^\]]+)\]\([^\)]+\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly IUpdateService _updateService;
    private readonly UpdateReleaseInfo _release;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _packageValidatedForInstall;

    public UpdateAvailableWindow(
        IUpdateService updateService,
        UpdateReleaseInfo release,
        StagedUpdatePackage? stagedPackage = null)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        Package = stagedPackage;
        InitializeComponent();

        HeadingText.Text = $"Доступна версия {_release.Version}";
        VersionTransitionText.Text = $"{_updateService.CurrentVersion}  →  {_release.Version}";
        ReleaseDateText.Text = _release.PublishedAtUtc is { } publishedAt
            ? $"Опубликовано {publishedAt.ToLocalTime():d MMMM yyyy}"
            : "Новый официальный релиз Actions Ring";
        ReleaseNotesText.Text = FormatReleaseNotes(_release.ReleaseNotes);
        InstallButton.Content = Package is null ? "Скачать и установить" : "Установить";
        SourceInitialized += (_, _) =>
        {
            FitToMonitorWorkArea();
            ApplyResponsiveLayout();
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        DownloadPanel.IsVisibleChanged += (_, _) => ApplyResponsiveLayout();
        Closing += OnClosing;
        Closed += (_, _) => _lifetime.Cancel();
    }

    public UpdateDecision Decision { get; private set; } = UpdateDecision.Later;

    public StagedUpdatePackage? Package { get; private set; }

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
        var effectiveHeight = ActualHeight > 0 ? ActualHeight : Height;
        var compact = effectiveHeight <= 420;

        RootLayout.Margin = compact
            ? new Thickness(20, 8, 20, 8)
            : new Thickness(28, 24, 28, 22);
        HeaderIconSurface.Width = compact ? 42 : 58;
        HeaderIconSurface.Height = compact ? 42 : 58;
        HeaderIconSurface.CornerRadius = new CornerRadius(compact ? 13 : 18);
        HeaderIcon.FontSize = compact ? 19 : 25;
        HeaderCopy.Margin = compact
            ? new Thickness(12, 0, 0, 0)
            : new Thickness(16, 0, 0, 0);
        HeadingText.FontSize = compact ? 20 : 24;
        VersionTransitionText.FontSize = compact ? 12 : 13;
        VersionTransitionText.Margin = compact
            ? new Thickness(0, 2, 0, 0)
            : new Thickness(0, 5, 0, 0);

        var downloadVisible = DownloadPanel.Visibility == Visibility.Visible;
        ReleaseBanner.Visibility = compact && downloadVisible
            ? Visibility.Collapsed
            : Visibility.Visible;
        ReleaseBanner.Margin = compact
            ? new Thickness(0, 6, 0, 0)
            : new Thickness(0, 20, 0, 0);
        ReleaseBanner.Padding = compact
            ? new Thickness(10, 4, 10, 4)
            : new Thickness(14, 10, 14, 10);
        ReleaseDateText.FontSize = compact ? 12 : 13;

        ReleaseNotesCard.Margin = compact
            ? new Thickness(0, 6, 0, 0)
            : new Thickness(0, 16, 0, 0);
        ReleaseNotesCard.Padding = compact
            ? new Thickness(12, 8, 12, 8)
            : new Thickness(20);
        ReleaseNotesCard.MinHeight = compact ? 68 : 0;
        ReleaseNotesCard.MaxHeight = compact
            ? downloadVisible ? 120 : 140
            : double.PositiveInfinity;
        ReleaseNotesHeading.FontSize = compact ? 14 : 16;
        ReleaseNotesScroller.Margin = compact
            ? new Thickness(0, 5, 0, 0)
            : new Thickness(0, 13, 0, 0);
        ReleaseNotesText.FontSize = compact ? 12 : 13;
        ReleaseNotesText.LineHeight = compact ? 17 : 21;

        InstallCaption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        InstallDetailsPanel.Margin = compact
            ? new Thickness(0)
            : new Thickness(0, 14, 0, 0);
        DownloadPanel.Margin = compact
            ? new Thickness(0, 4, 0, 0)
            : new Thickness(0, 12, 0, 0);
        DownloadStatusText.FontSize = compact ? 12 : 13;
        DownloadPercentText.FontSize = compact ? 12 : 13;
        DownloadProgress.Margin = compact
            ? new Thickness(0, 5, 0, 0)
            : new Thickness(0, 9, 0, 0);

        ActionsPanel.Margin = compact
            ? new Thickness(0, 6, 0, 0)
            : new Thickness(0, 20, 0, 0);
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!_packageValidatedForInstall)
        {
            _busy = true;
            SetButtonsEnabled(false);
            InstallButton.IsEnabled = false;
            DownloadPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text = "Загрузка и проверка пакета…";
            DownloadStatusText.Foreground = FindBrush("TextPrimaryBrush", Brushes.White);
            DownloadProgress.IsIndeterminate = true;
            DownloadPercentText.Text = string.Empty;

            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (value.Percent is not { } percent)
                {
                    DownloadProgress.IsIndeterminate = true;
                    DownloadPercentText.Text = FormatBytes(value.BytesReceived);
                    return;
                }

                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = percent;
                DownloadPercentText.Text = $"{percent}%";
            });

            try
            {
                var result = await _updateService.DownloadAndStageAsync(
                    _release,
                    progress,
                    _lifetime.Token);
                if (_closeRequested)
                {
                    CompleteRequestedClose();
                    return;
                }
                if (result.Status is not (UpdateStageStatus.Staged or UpdateStageStatus.AlreadyStaged)
                    || result.Package is null)
                {
                    ShowDownloadFailure(result.UserMessage);
                    return;
                }

                Package = result.Package;
                _packageValidatedForInstall = true;
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = 100;
                DownloadPercentText.Text = "100%";
                DownloadStatusText.Text = "Пакет проверен и готов к установке";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                CompleteRequestedClose();
                return;
            }
            catch (Exception exception)
            {
                AppLog.Error("Update download dialog failed", exception);
                if (_closeRequested)
                {
                    CompleteRequestedClose();
                    return;
                }
                ShowDownloadFailure("Не удалось подготовить обновление. Проверьте подключение и повторите попытку.");
                return;
            }
            finally
            {
                _busy = false;
            }
        }

        Decision = UpdateDecision.Install;
        DialogResult = true;
    }

    private void ShowDownloadFailure(string message)
    {
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        DownloadPercentText.Text = string.Empty;
        DownloadStatusText.Text = string.IsNullOrWhiteSpace(message)
            ? "Не удалось подготовить обновление."
            : message;
        DownloadStatusText.Foreground = FindBrush("DangerBrush", Brushes.IndianRed);
        SetButtonsEnabled(true);
        InstallButton.Content = "Повторить";
        InstallButton.IsEnabled = true;
        _busy = false;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        Decision = UpdateDecision.Skip;
        DialogResult = false;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        Decision = UpdateDecision.Later;
        DialogResult = false;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_busy || _allowClose)
        {
            return;
        }

        e.Cancel = true;
        _closeRequested = true;
        _lifetime.Cancel();
        DownloadStatusText.Text = "Отменяем загрузку…";
        InstallButton.IsEnabled = false;
    }

    private void CompleteRequestedClose()
    {
        _busy = false;
        if (!_closeRequested)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        SkipButton.IsEnabled = enabled;
        LaterButton.IsEnabled = enabled;
    }

    private static string FormatReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "Описание изменений для этой версии не опубликовано.";
        }

        var builder = new StringBuilder();
        foreach (var sourceLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0)
            {
                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.AppendLine();
                }
                continue;
            }

            if (line.StartsWith('#'))
            {
                line = line.TrimStart('#', ' ').ToUpperInvariant();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal)
                     || line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "• " + line[2..].Trim();
            }

            try
            {
                line = MarkdownLinkPattern.Replace(line, "$1");
            }
            catch (RegexMatchTimeoutException)
            {
                // Keep the plain release-note line if an unusually complex link cannot be normalized quickly.
            }
            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal);
            builder.AppendLine(line);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0
            ? "Описание изменений для этой версии не опубликовано."
            : result;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} Б";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} КБ";
        }
        return $"{bytes / (1024d * 1024d):0.#} МБ";
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;
}
