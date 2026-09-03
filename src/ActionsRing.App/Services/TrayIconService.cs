using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ActionsRing.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private bool _disposed;

    public TrayIconService()
    {
        _pauseItem = new ToolStripMenuItem("Приостановить кольцо");
        _startupItem = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = false };
        var showItem = new ToolStripMenuItem("Открыть настройки");
        var previewItem = new ToolStripMenuItem("Показать кольцо");
        var exitItem = new ToolStripMenuItem("Выйти");

        showItem.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        previewItem.Click += (_, _) => ShowRingRequested?.Invoke(this, EventArgs.Empty);
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);
        _startupItem.Click += (_, _) => StartupToggled?.Invoke(this, EventArgs.Empty);
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(previewItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "Actions Ring",
            Icon = CreateIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ShowRingRequested;
    public event EventHandler? PauseToggled;
    public event EventHandler? StartupToggled;
    public event EventHandler? ExitRequested;

    public bool IsPaused
    {
        get => _pauseItem.Checked;
        set
        {
            _pauseItem.Checked = value;
            _pauseItem.Text = value ? "Возобновить кольцо" : "Приостановить кольцо";
            _notifyIcon.Text = value ? "Actions Ring — приостановлено" : "Actions Ring";
        }
    }

    public bool IsStartupEnabled
    {
        get => _startupItem.Checked;
        set => _startupItem.Checked = value;
    }

    public void ShowWelcomeBalloon()
    {
        _notifyIcon.BalloonTipTitle = "Actions Ring работает";
        _notifyIcon.BalloonTipText = "Кольцо доступно из трея и по назначенному вводу.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2800);
    }

    public void ShowSettingsRecoveryBalloon(bool restoredBackup)
    {
        _notifyIcon.BalloonTipTitle = "Настройки восстановлены";
        _notifyIcon.BalloonTipText = restoredBackup
            ? "Загружена предыдущая сохранённая версия настроек."
            : "Повреждённый файл заменён безопасными настройками.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void ShowAutostartErrorBalloon()
    {
        _notifyIcon.BalloonTipTitle = "Автозапуск не изменён";
        _notifyIcon.BalloonTipText = "Windows не удалось применить настройку автозапуска.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(4500);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _disposed = true;
    }

    private static Icon CreateIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } executable)
            {
                using var associated = Icon.ExtractAssociatedIcon(executable);
                if (associated is not null)
                {
                    return (Icon)associated.Clone();
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            // The procedural fallback below keeps the tray usable from unusual hosts.
        }

        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var core = new SolidBrush(Color.FromArgb(255, 18, 21, 23));
        using var accent = new SolidBrush(Color.FromArgb(255, 130, 78, 249));
        using var bubble = new SolidBrush(Color.White);
        graphics.FillEllipse(core, 1, 1, 30, 30);
        graphics.FillEllipse(accent, 11, 11, 10, 10);
        for (var index = 0; index < 8; index++)
        {
            var angle = -Math.PI / 2d + index * Math.PI / 4d;
            var x = 16f + (float)Math.Cos(angle) * 10.5f;
            var y = 16f + (float)Math.Sin(angle) * 10.5f;
            graphics.FillEllipse(bubble, x - 2.4f, y - 2.4f, 4.8f, 4.8f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
