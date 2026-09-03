using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ActionsRing.App.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class TrayContextMenuTests
{
    [STATestMethod]
    public void MenuHasStyledCommandsInExpectedOrder()
    {
        var menu = new TrayContextMenu();
        var commands = menu.Items.OfType<MenuItem>().ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "Открыть настройки",
                "Показать кольцо",
                "Приостановить кольцо",
                "Запускать вместе с Windows",
                "Выйти",
            },
            commands.Select(item => item.Header?.ToString()).ToArray());
        Assert.AreEqual(2, menu.Items.OfType<Separator>().Count());
        Assert.IsNotNull(menu.Template);
        Assert.IsTrue(commands.All(item => item.Style is not null));
        Assert.AreEqual(PlacementMode.MousePoint, menu.Placement);
        Assert.IsFalse(menu.StaysOpen);
    }

    [STATestMethod]
    public void StateChangesUpdatePauseStartupAndStatusVisuals()
    {
        var menu = new TrayContextMenu();
        var commands = menu.Items.OfType<MenuItem>().ToArray();
        var pause = commands[2];
        var startup = commands[3];

        menu.IsPaused = true;
        menu.IsStartupEnabled = true;

        Assert.IsTrue(menu.IsPaused);
        Assert.IsTrue(pause.IsChecked);
        Assert.AreEqual("Возобновить кольцо", pause.Header);
        Assert.AreEqual("Кольцо приостановлено", menu.StatusText);
        Assert.IsTrue(menu.IsStartupEnabled);
        Assert.IsTrue(startup.IsChecked);

        menu.IsPaused = false;
        menu.IsStartupEnabled = false;

        Assert.IsFalse(pause.IsChecked);
        Assert.AreEqual("Приостановить кольцо", pause.Header);
        Assert.AreEqual("Кольцо активно", menu.StatusText);
        Assert.IsFalse(startup.IsChecked);
    }

    [STATestMethod]
    public void EveryCommandRaisesItsMatchingRequest()
    {
        var menu = new TrayContextMenu();
        var commands = menu.Items.OfType<MenuItem>().ToArray();
        var requests = new List<string>();
        menu.ShowSettingsRequested += (_, _) => requests.Add("settings");
        menu.ShowRingRequested += (_, _) => requests.Add("ring");
        menu.PauseToggled += (_, _) => requests.Add("pause");
        menu.StartupToggled += (_, _) => requests.Add("startup");
        menu.ExitRequested += (_, _) => requests.Add("exit");

        foreach (var command in commands)
        {
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        CollectionAssert.AreEqual(
            new[] { "settings", "ring", "pause", "startup", "exit" },
            requests);
    }
}
