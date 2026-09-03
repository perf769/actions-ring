using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using ActionsRing.Platform.Windows;
using ActionsRing.Platform.Windows.Actions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlatformKeyboardShortcutAction = ActionsRing.Platform.Windows.Actions.KeyboardShortcutAction;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ActionExecutionServiceTests
{
    [TestMethod]
    public async Task UserInputActions_KeepCapturedTargetWindow()
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);
        var target = new nint(0x4321);

        await service.ExecuteAsync(ActionDefinition.Shortcut("Копировать", "C", KeyboardModifiers.Control), target);
        await service.ExecuteAsync(new ActionDefinition
        {
            Kind = ActionKind.TypeText,
            TypeText = new TypeTextAction { Text = "текст" },
        }, target);
        await service.ExecuteAsync(new ActionDefinition
        {
            Kind = ActionKind.MouseInput,
            MouseInput = new MouseInputAction { Button = MouseButton.Left },
        }, target);

        Assert.AreEqual(target, executor.Actions.OfType<PlatformKeyboardShortcutAction>().Single().TargetWindow);
        Assert.AreEqual(target, executor.Actions.OfType<TextInputAction>().Single().TargetWindow);
        Assert.AreEqual(target, executor.Actions.OfType<MouseButtonAction>().Single().TargetWindow);
    }

    [DataTestMethod]
    [DataRow(0d, 0, 0)]
    [DataRow(-0.1d, 1, 0xBD)]
    [DataRow(2.2d, 3, 0xBB)]
    public async Task ZoomAdjustment_UsesSignedDiscreteSteps(double value, int expectedCount, int expectedKey)
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);
        var target = new nint(0x1234);

        await service.ExecuteAsync(CreateAdjustment(AdjustableParameter.Zoom, value), target);

        var shortcuts = executor.Actions.OfType<PlatformKeyboardShortcutAction>().ToArray();
        Assert.AreEqual(expectedCount, shortcuts.Length);
        foreach (var shortcut in shortcuts)
        {
            Assert.AreEqual(target, shortcut.TargetWindow);
            Assert.AreEqual(expectedKey, shortcut.VirtualKeys.Last());
        }
    }

    [TestMethod]
    public async Task DynamicDate_IsGeneratedAtExecutionAndTargetsCapturedWindow()
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);
        var target = new nint(0x7777);
        var action = ActionDefinition.BuiltInCommand("Дата", BuiltInCommand.InsertDate);

        await service.ExecuteAsync(action, target);

        var text = executor.Actions.OfType<TextInputAction>().Single();
        Assert.AreEqual(target, text.TargetWindow);
        Assert.IsFalse(string.IsNullOrWhiteSpace(text.Text));
    }

    [TestMethod]
    public async Task MediaStop_MapsToWindowsMediaStop()
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);

        await service.ExecuteAsync(
            ActionDefinition.BuiltInCommand("Стоп", BuiltInCommand.MediaStop),
            new nint(0x7777));

        Assert.AreEqual(MediaCommand.Stop, executor.Actions.OfType<MediaAction>().Single().Command);
    }

    [TestMethod]
    public async Task PackagedApplication_UsesAppsFolderThroughExplorer()
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);
        const string appsFolderTarget = @"shell:AppsFolder\Vendor.ChatApp_abc!Main";

        await service.ExecuteAsync(new ActionDefinition
        {
            Kind = ActionKind.LaunchApplication,
            LaunchApplication = new LaunchApplicationAction
            {
                ExecutablePath = appsFolderTarget,
                RunAsAdministrator = true,
            },
        }, nint.Zero);

        var launch = executor.Actions.OfType<LaunchTargetAction>().Single();
        Assert.AreEqual("explorer.exe", launch.Target);
        Assert.AreEqual($"\"{appsFolderTarget}\"", launch.Arguments);
        Assert.IsFalse(launch.RunAsAdministrator);
    }

    [DataTestMethod]
    [DataRow(BuiltInCommand.ToggleCapsLock, 0x14)]
    [DataRow(BuiltInCommand.ToggleNumLock, 0x90)]
    [DataRow(BuiltInCommand.ToggleScrollLock, 0x91)]
    public async Task LockKeyActions_MapToTheirWindowsVirtualKey(BuiltInCommand command, int expectedKey)
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);
        var target = new nint(0x7777);

        await service.ExecuteAsync(ActionDefinition.BuiltInCommand("Переключить", command), target);

        var shortcut = executor.Actions.OfType<PlatformKeyboardShortcutAction>().Single();
        Assert.AreEqual(target, shortcut.TargetWindow);
        CollectionAssert.AreEqual(new[] { expectedKey }, shortcut.VirtualKeys.ToArray());
    }

    [DataTestMethod]
    [DataRow(AdjustableParameter.VerticalScroll, false)]
    [DataRow(AdjustableParameter.HorizontalScroll, true)]
    public async Task ScrollAdjustment_RoutesWheelToCapturedWindow(
        AdjustableParameter parameter,
        bool expectedHorizontal)
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);
        var target = new nint(0x1234);

        await service.ExecuteAsync(CreateAdjustment(parameter, 2), target);

        var wheel = executor.Actions.OfType<MouseWheelAction>().Single();
        Assert.AreEqual(240, wheel.Delta);
        Assert.AreEqual(expectedHorizontal, wheel.Horizontal);
        Assert.AreEqual(target, wheel.TargetWindow);
    }

    [DataTestMethod]
    [DataRow(AdjustableParameter.VerticalScroll)]
    [DataRow(AdjustableParameter.HorizontalScroll)]
    public async Task ScrollAdjustment_WithoutCapturedWindow_IsSafeNoOp(AdjustableParameter parameter)
    {
        var executor = new RecordingExecutor();
        var service = new ActionExecutionService(executor);

        await service.ExecuteAsync(CreateAdjustment(parameter, 1), nint.Zero);

        Assert.AreEqual(0, executor.Actions.Count);
    }

    private static ActionDefinition CreateAdjustment(AdjustableParameter parameter, double value) =>
        new()
        {
            Kind = ActionKind.AdjustParameter,
            AdjustParameter = new AdjustParameterAction
            {
                Parameter = parameter,
                Mode = AdjustmentMode.Relative,
                Value = value,
            },
        };

    private sealed class RecordingExecutor : IWindowsActionExecutor
    {
        public List<WindowsAction> Actions { get; } = [];

        public Task ExecuteAsync(WindowsAction action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }
}
