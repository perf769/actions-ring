using System.Windows;
using System.Windows.Threading;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Platform.Windows.SingleInstance;

namespace ActionsRing.App;

public partial class App : Application
{
    private const string ApplicationId = "ActionsRing.Desktop.v1";
    private SingleInstanceCoordinator? _singleInstance;
    private JsonConfigurationStore? _store;
    private ApplicationController? _controller;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private bool _pendingShowRing;
    private bool _pendingShowSettings;
    private int _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            _singleInstance = SingleInstanceCoordinator.Acquire(ApplicationId);
            if (!_singleInstance.IsPrimary)
            {
                try
                {
                    await _singleInstance.NotifyPrimaryAsync(InstanceActivationRequest.FromCurrentProcess());
                }
                catch (Exception exception)
                {
                    AppLog.Error("Could not activate the existing instance", exception);
                }
                Shutdown();
                return;
            }

            _singleInstance.ActivationReceived += (_, request) =>
                Dispatcher.BeginInvoke(() => HandleActivationRequest(request.Request));
            _singleInstance.PlatformError += (_, args) => AppLog.Error(args.Operation, args.Exception);
            _singleInstance.StartListening();

            _store = new JsonConfigurationStore();
            var load = await _store.LoadAsync();
            if (load.Status == ConfigurationLoadStatus.UnsupportedVersion)
            {
                MessageBox.Show(
                    "Файл настроек создан более новой версией Actions Ring. Обновите приложение; файл не был изменён.",
                    "Нужна более новая версия",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                await ExitAsync();
                return;
            }

            if (load.Status is ConfigurationLoadStatus.RecoveredCorrupt or ConfigurationLoadStatus.RecoveredFromBackup)
            {
                AppLog.Info($"Configuration recovery status: {load.Status}; source: {load.RecoveredFilePath}");
            }

            var theme = new ThemeService();
            theme.Apply(load.Configuration.Preferences.Appearance);
            _controller = new ApplicationController(load.Configuration, _store, theme);
            _mainWindow = new MainWindow(_controller, theme);
            MainWindow = _mainWindow;

            _tray = new TrayIconService
            {
                IsStartupEnabled = load.Configuration.Preferences.General.RunAtStartup,
            };
            WireApplicationEvents();
            await _controller.StartAsync();
            FlushPendingActivationRequests();

            var background = e.Args.Any(argument =>
                string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
            var showRing = e.Args.Any(argument =>
                string.Equals(argument, "--show-ring", StringComparison.OrdinalIgnoreCase));
            var mustShowSetup = !load.Configuration.Onboarding.IsCompleted;
            if (!background || !load.Configuration.Preferences.General.StartMinimized || mustShowSetup)
            {
                _mainWindow.Show();
            }
            if (showRing)
            {
                await _controller.ShowRingAsync();
            }
            if (load.Status == ConfigurationLoadStatus.CreatedDefault)
            {
                _tray.ShowWelcomeBalloon();
            }
            else if (load.Status is ConfigurationLoadStatus.RecoveredCorrupt or ConfigurationLoadStatus.RecoveredFromBackup)
            {
                _tray.ShowSettingsRecoveryBalloon(load.Status == ConfigurationLoadStatus.RecoveredFromBackup);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Application startup failed", exception);
            MessageBox.Show(
                $"Actions Ring не удалось запустить. Перезапустите приложение. Если ошибка повторится, журнал находится здесь:\n\n{AppLog.CurrentPath}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private void WireApplicationEvents()
    {
        if (_controller is null || _mainWindow is null || _tray is null)
        {
            return;
        }

        _mainWindow.ExitRequested += (_, _) => _ = ExitAsync();
        _tray.ShowSettingsRequested += (_, _) => Dispatcher.BeginInvoke(_mainWindow.ShowFromTray);
        _tray.ShowRingRequested += (_, _) => Dispatcher.BeginInvoke(() => _ = _controller.ShowRingAsync());
        _tray.PauseToggled += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _controller.SetPaused(!_controller.IsPaused);
            _tray.IsPaused = _controller.IsPaused;
        });
        _tray.StartupToggled += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            var previous = _controller.Configuration.Preferences.General.RunAtStartup;
            try
            {
                var enabled = !previous;
                _controller.Configuration.Preferences.General.RunAtStartup = enabled;
                await _controller.SaveAndApplyAsync(updateAutostart: true);
                _tray.IsStartupEnabled = _controller.Configuration.Preferences.General.RunAtStartup;
            }
            catch (Exception exception)
            {
                AppLog.Error("Tray autostart toggle failed", exception);
                if (exception is not AutostartApplyException)
                {
                    _controller.Configuration.Preferences.General.RunAtStartup = previous;
                }
                _tray.IsStartupEnabled = _controller.Configuration.Preferences.General.RunAtStartup;
                _tray.ShowAutostartErrorBalloon();
            }
        });
        _tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(() => _ = ExitAsync());
        _controller.ConfigurationChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_tray is not null && _controller is not null)
            {
                _tray.IsStartupEnabled = _controller.Configuration.Preferences.General.RunAtStartup;
            }
        });
    }

    private void HandleActivationRequest(InstanceActivationRequest request)
    {
        var showRing = request.Arguments.Any(argument =>
            string.Equals(argument, "--show-ring", StringComparison.OrdinalIgnoreCase));
        if (_controller is null || _mainWindow is null)
        {
            _pendingShowRing |= showRing;
            _pendingShowSettings |= !showRing;
            return;
        }

        if (showRing)
        {
            _ = _controller.ShowRingAsync();
        }
        else
        {
            _mainWindow.ShowFromTray();
        }
    }

    private void FlushPendingActivationRequests()
    {
        if (_controller is null || _mainWindow is null)
        {
            return;
        }

        if (_pendingShowSettings)
        {
            _pendingShowSettings = false;
            _mainWindow.ShowFromTray();
        }
        if (_pendingShowRing)
        {
            _pendingShowRing = false;
            _ = _controller.ShowRingAsync();
        }
    }

    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
        {
            return;
        }

        try
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.PrepareForShutdownAsync();
            }
            _tray?.Dispose();
            _tray = null;
            if (_controller is not null)
            {
                await _controller.DisposeAsync();
                _controller = null;
            }
            _store?.Dispose();
            _store = null;
            if (_singleInstance is not null)
            {
                await _singleInstance.DisposeAsync();
                _singleInstance = null;
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Application shutdown failed", exception);
        }
        finally
        {
            Shutdown();
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            AppLog.Error("Unhandled background exception", exception);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.Error("Unhandled interface exception", args.Exception);
        MessageBox.Show(
            $"Произошла непредвиденная ошибка. Перезапустите Actions Ring. Подробности сохранены в журнале:\n\n{AppLog.CurrentPath}",
            "Actions Ring",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        args.Handled = true;
    }
}
