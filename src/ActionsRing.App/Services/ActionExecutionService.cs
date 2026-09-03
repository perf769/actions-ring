using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using ActionsRing.Core.Domain;
using ActionsRing.Platform.Windows;
using WindowsActions = ActionsRing.Platform.Windows.Actions;
using PlatformInput = ActionsRing.Platform.Windows.Input;

namespace ActionsRing.App.Services;

public sealed class ActionExecutionService(IWindowsActionExecutor executor)
{
    private const int MaximumSequenceDepth = 6;

    public Task ExecuteAsync(
        ActionDefinition action,
        nint targetWindow,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(action, targetWindow, 0, cancellationToken);

    public Task AdjustAsync(
        ActionDefinition action,
        int direction,
        nint targetWindow,
        CancellationToken cancellationToken = default)
    {
        if (action.Kind != ActionKind.AdjustParameter || action.AdjustParameter is null || direction == 0)
        {
            return Task.CompletedTask;
        }
        var clone = new ActionDefinition
        {
            Kind = ActionKind.AdjustParameter,
            AdjustParameter = new AdjustParameterAction
            {
                Parameter = action.AdjustParameter.Parameter,
                Mode = AdjustmentMode.Relative,
                Value = Math.Abs(action.AdjustParameter.Value) * Math.Sign(direction),
                CustomParameterId = action.AdjustParameter.CustomParameterId,
            },
        };
        return ExecuteCoreAsync(clone, targetWindow, 0, cancellationToken);
    }

    private async Task ExecuteCoreAsync(
        ActionDefinition action,
        nint targetWindow,
        int depth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (depth > MaximumSequenceDepth)
        {
            throw new InvalidOperationException("Action sequence nesting is too deep.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        switch (action.Kind)
        {
            case ActionKind.None:
                return;
            case ActionKind.KeyboardShortcut:
                await ExecuteShortcutAsync(action.KeyboardShortcut, targetWindow, cancellationToken);
                return;
            case ActionKind.LaunchApplication:
                var launch = action.LaunchApplication
                    ?? throw new InvalidDataException("Launch action has no settings.");
                var isAppsFolderTarget = launch.ExecutablePath.StartsWith(
                    @"shell:AppsFolder\",
                    StringComparison.OrdinalIgnoreCase);
                await executor.ExecuteAsync(
                    new WindowsActions.LaunchTargetAction(
                        isAppsFolderTarget ? "explorer.exe" : launch.ExecutablePath,
                        isAppsFolderTarget ? $"\"{launch.ExecutablePath}\"" : launch.Arguments,
                        isAppsFolderTarget ? null : launch.WorkingDirectory,
                        UseShellExecute: true,
                        RunAsAdministrator: !isAppsFolderTarget && launch.RunAsAdministrator),
                    cancellationToken);
                return;
            case ActionKind.OpenUri:
                await executor.ExecuteAsync(
                    new WindowsActions.LaunchTargetAction(
                        action.OpenUri?.Uri ?? throw new InvalidDataException("URI action is empty.")),
                    cancellationToken);
                return;
            case ActionKind.TypeText:
                await ExecuteTextAsync(action.TypeText, targetWindow, cancellationToken);
                return;
            case ActionKind.BuiltIn:
                await ExecuteBuiltInAsync(
                    action.BuiltIn?.Command ?? throw new InvalidDataException("Built-in action is empty."),
                    targetWindow,
                    cancellationToken);
                return;
            case ActionKind.MouseInput:
                await ExecuteMouseAsync(action.MouseInput, targetWindow, cancellationToken);
                return;
            case ActionKind.Sequence:
                foreach (var step in action.Sequence?.Steps ?? [])
                {
                    await ExecuteCoreAsync(step.Action, targetWindow, depth + 1, cancellationToken);
                    if (step.DelayAfterMilliseconds > 0)
                    {
                        await Task.Delay(step.DelayAfterMilliseconds, cancellationToken);
                    }
                }
                return;
            case ActionKind.AdjustParameter:
                await ExecuteAdjustmentAsync(action.AdjustParameter, targetWindow, cancellationToken);
                return;
            default:
                throw new NotSupportedException($"Action kind {action.Kind} is not supported.");
        }
    }

    private async Task ExecuteShortcutAsync(
        KeyboardShortcutAction? shortcut,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        if (shortcut?.Chords is not { Count: > 0 })
        {
            throw new InvalidDataException("Keyboard shortcut has no chord.");
        }
        foreach (var chord in shortcut.Chords)
        {
            var keys = ModifierVirtualKeys(chord.Modifiers).Append(InputBindingMapper.KeyNameToVirtualKey(chord.Key)).ToArray();
            await executor.ExecuteAsync(
                new WindowsActions.KeyboardShortcutAction(keys, targetWindow),
                cancellationToken);
            if (shortcut.Chords.Count > 1)
            {
                await Task.Delay(35, cancellationToken);
            }
        }
    }

    private async Task ExecuteTextAsync(
        TypeTextAction? text,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        if (text is null)
        {
            throw new InvalidDataException("Text action has no settings.");
        }
        if (text.CharacterDelayMilliseconds <= 0)
        {
            await executor.ExecuteAsync(
                new WindowsActions.TextInputAction(text.Text, text.CharacterDelayMilliseconds, targetWindow),
                cancellationToken);
            return;
        }
        await executor.ExecuteAsync(
            new WindowsActions.TextInputAction(text.Text, text.CharacterDelayMilliseconds, targetWindow),
            cancellationToken);
    }

    private async Task ExecuteBuiltInAsync(
        BuiltInCommand command,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        if (TryCreateDynamicText(command, out var dynamicText))
        {
            await executor.ExecuteAsync(
                new WindowsActions.TextInputAction(dynamicText, TargetWindow: targetWindow),
                cancellationToken);
            return;
        }

        if (IsClipboardCommand(command))
        {
            await ExecuteClipboardCommandAsync(command, targetWindow, cancellationToken);
            return;
        }

        WindowsActions.WindowsAction windowsAction = command switch
        {
            BuiltInCommand.Back => Shortcut(targetWindow, 0x12, 0x25),
            BuiltInCommand.Forward => Shortcut(targetWindow, 0x12, 0x27),
            BuiltInCommand.Copy => Shortcut(targetWindow, 0x11, 'C'),
            BuiltInCommand.Paste => Shortcut(targetWindow, 0x11, 'V'),
            BuiltInCommand.Cut => Shortcut(targetWindow, 0x11, 'X'),
            BuiltInCommand.Undo => Shortcut(targetWindow, 0x11, 'Z'),
            BuiltInCommand.Redo => Shortcut(targetWindow, 0x11, 'Y'),
            BuiltInCommand.SelectAll => Shortcut(targetWindow, 0x11, 'A'),
            BuiltInCommand.Find => Shortcut(targetWindow, 0x11, 'F'),
            BuiltInCommand.ShowDesktop => Shortcut(targetWindow, 0x5B, 'D'),
            BuiltInCommand.TaskView => Shortcut(targetWindow, 0x5B, 0x09),
            BuiltInCommand.SwitchWindow => Shortcut(targetWindow, 0x12, 0x09),
            BuiltInCommand.CloseWindow => new WindowsActions.WindowManagementAction(WindowsActions.WindowManagementCommand.Close, targetWindow),
            BuiltInCommand.MinimizeWindow => new WindowsActions.WindowManagementAction(WindowsActions.WindowManagementCommand.Minimize, targetWindow),
            BuiltInCommand.MaximizeOrRestoreWindow => new WindowsActions.WindowManagementAction(WindowsActions.WindowManagementCommand.ToggleMaximizeRestore, targetWindow),
            BuiltInCommand.VolumeUp => new WindowsActions.MediaAction(WindowsActions.MediaCommand.VolumeUp),
            BuiltInCommand.VolumeDown => new WindowsActions.MediaAction(WindowsActions.MediaCommand.VolumeDown),
            BuiltInCommand.VolumeMute => new WindowsActions.MediaAction(WindowsActions.MediaCommand.VolumeMute),
            BuiltInCommand.MediaPlayPause => new WindowsActions.MediaAction(WindowsActions.MediaCommand.PlayPause),
            BuiltInCommand.MediaNext => new WindowsActions.MediaAction(WindowsActions.MediaCommand.NextTrack),
            BuiltInCommand.MediaPrevious => new WindowsActions.MediaAction(WindowsActions.MediaCommand.PreviousTrack),
            BuiltInCommand.MediaStop => new WindowsActions.MediaAction(WindowsActions.MediaCommand.Stop),
            BuiltInCommand.TakeScreenshot => Shortcut(targetWindow, 0x5B, 0x10, 'S'),
            BuiltInCommand.LockWorkstation => Shortcut(targetWindow, 0x5B, 'L'),
            BuiltInCommand.OpenFileExplorer => Shortcut(targetWindow, 0x5B, 'E'),
            BuiltInCommand.OpenSettings => new WindowsActions.LaunchTargetAction("ms-settings:"),
            BuiltInCommand.OpenSearch => Shortcut(targetWindow, 0x5B, 'S'),
            BuiltInCommand.ToggleCapsLock => Shortcut(targetWindow, 0x14),
            BuiltInCommand.ToggleNumLock => Shortcut(targetWindow, 0x90),
            BuiltInCommand.ToggleScrollLock => Shortcut(targetWindow, 0x91),
            _ => throw new NotSupportedException($"Built-in command {command} is not supported."),
        };
        await executor.ExecuteAsync(windowsAction, cancellationToken);
    }

    private async Task ExecuteClipboardCommandAsync(
        BuiltInCommand command,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        if (command == BuiltInCommand.ClearClipboard)
        {
            await RetryClipboardAsync(
                () =>
                {
                    System.Windows.Clipboard.Clear();
                    return true;
                },
                cancellationToken);
            return;
        }

        var source = await RetryClipboardAsync(
            () => System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText)
                : string.Empty,
            cancellationToken);
        if (source.Length == 0)
        {
            return;
        }
        var transformed = TransformClipboardText(command, source);
        await RetryClipboardAsync(
            () =>
            {
                System.Windows.Clipboard.SetText(transformed, System.Windows.TextDataFormat.UnicodeText);
                return true;
            },
            cancellationToken);
        await executor.ExecuteAsync(Shortcut(targetWindow, 0x11, 'V'), cancellationToken);
    }

    private static async Task<T> RetryClipboardAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null && !dispatcher.CheckAccess())
                {
                    return await dispatcher.InvokeAsync(operation).Task.WaitAsync(cancellationToken);
                }
                return operation();
            }
            catch (ExternalException) when (attempt < 4)
            {
                await Task.Delay(40 * (attempt + 1), cancellationToken);
            }
        }
    }

    internal static string TransformClipboardText(BuiltInCommand command, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return command switch
        {
            BuiltInCommand.PasteClipboardPlainText => source,
            BuiltInCommand.ClipboardLowercase => source.ToLower(CultureInfo.CurrentCulture),
            BuiltInCommand.ClipboardSentenceCase => ToSentenceCase(source),
            BuiltInCommand.ClipboardTitleCase => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                source.ToLower(CultureInfo.CurrentCulture)),
            BuiltInCommand.ClipboardUppercase => source.ToUpper(CultureInfo.CurrentCulture),
            BuiltInCommand.ClipboardToggleCase => new string(source.Select(character =>
                char.IsUpper(character)
                    ? char.ToLower(character, CultureInfo.CurrentCulture)
                    : char.IsLower(character)
                        ? char.ToUpper(character, CultureInfo.CurrentCulture)
                        : character).ToArray()),
            BuiltInCommand.ClipboardUrlEncode => WebUtility.UrlEncode(source),
            BuiltInCommand.ClipboardUrlDecode => WebUtility.UrlDecode(source),
            BuiltInCommand.ClipboardHtmlEncode => WebUtility.HtmlEncode(source),
            BuiltInCommand.ClipboardHtmlDecode => WebUtility.HtmlDecode(source),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    private static string ToSentenceCase(string source)
    {
        var result = new StringBuilder(source.Length);
        var capitalize = true;
        foreach (var character in source)
        {
            var output = capitalize
                ? char.ToUpper(character, CultureInfo.CurrentCulture)
                : char.ToLower(character, CultureInfo.CurrentCulture);
            result.Append(output);
            if (char.IsLetter(character))
            {
                capitalize = false;
            }
            else if (character is '.' or '!' or '?')
            {
                capitalize = true;
            }
        }
        return result.ToString();
    }

    private static bool IsClipboardCommand(BuiltInCommand command) => command is
        BuiltInCommand.ClearClipboard
        or BuiltInCommand.PasteClipboardPlainText
        or BuiltInCommand.ClipboardLowercase
        or BuiltInCommand.ClipboardSentenceCase
        or BuiltInCommand.ClipboardTitleCase
        or BuiltInCommand.ClipboardUppercase
        or BuiltInCommand.ClipboardToggleCase
        or BuiltInCommand.ClipboardUrlEncode
        or BuiltInCommand.ClipboardUrlDecode
        or BuiltInCommand.ClipboardHtmlEncode
        or BuiltInCommand.ClipboardHtmlDecode;

    private static bool TryCreateDynamicText(BuiltInCommand command, out string text)
    {
        var now = DateTime.Now;
        text = command switch
        {
            BuiltInCommand.InsertDate => now.ToString("d", CultureInfo.CurrentCulture),
            BuiltInCommand.InsertTime => now.ToString("t", CultureInfo.CurrentCulture),
            BuiltInCommand.InsertDateTime => now.ToString("g", CultureInfo.CurrentCulture),
            BuiltInCommand.InsertDayOfYear => now.DayOfYear.ToString(CultureInfo.CurrentCulture),
            BuiltInCommand.InsertWeekNumber => ISOWeek.GetWeekOfYear(now).ToString(CultureInfo.CurrentCulture),
            BuiltInCommand.InsertMoonPhase => MoonPhase(DateTime.UtcNow),
            _ => string.Empty,
        };
        return text.Length > 0;
    }

    private static string MoonPhase(DateTime utcNow)
    {
        var referenceNewMoon = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
        const double lunarCycleDays = 29.53058867;
        var cycles = (utcNow - referenceNewMoon).TotalDays / lunarCycleDays;
        var fraction = cycles - Math.Floor(cycles);
        var phase = (int)Math.Floor((fraction * 8d) + 0.5d) % 8;
        return phase switch
        {
            0 => "Новолуние",
            1 => "Растущий серп",
            2 => "Первая четверть",
            3 => "Растущая Луна",
            4 => "Полнолуние",
            5 => "Убывающая Луна",
            6 => "Последняя четверть",
            _ => "Убывающий серп",
        };
    }

    private async Task ExecuteMouseAsync(
        MouseInputAction? mouse,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        if (mouse is null)
        {
            throw new InvalidDataException("Mouse action has no settings.");
        }
        if (mouse.Button is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight)
        {
            var positive = mouse.Button is MouseButton.WheelUp or MouseButton.WheelRight;
            var horizontal = mouse.Button is MouseButton.WheelLeft or MouseButton.WheelRight;
            var delta = Math.Abs(mouse.WheelDelta) * (positive ? 1 : -1);
            await executor.ExecuteAsync(
                new WindowsActions.MouseWheelAction(delta, horizontal, targetWindow),
                cancellationToken);
            return;
        }
        var button = mouse.Button switch
        {
            MouseButton.Left => PlatformInput.MouseButton.Left,
            MouseButton.Right => PlatformInput.MouseButton.Right,
            MouseButton.Middle => PlatformInput.MouseButton.Middle,
            MouseButton.XButton1 => PlatformInput.MouseButton.XButton1,
            MouseButton.XButton2 => PlatformInput.MouseButton.XButton2,
            _ => throw new ArgumentOutOfRangeException(nameof(mouse)),
        };
        for (var index = 0; index < Math.Clamp(mouse.ClickCount, 1, 3); index++)
        {
            await executor.ExecuteAsync(
                new WindowsActions.MouseButtonAction(button, TargetWindow: targetWindow),
                cancellationToken);
        }
    }

    private async Task ExecuteAdjustmentAsync(
        AdjustParameterAction? adjustment,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        if (adjustment is null)
        {
            throw new InvalidDataException("Adjustment action has no settings.");
        }

        if (!double.IsFinite(adjustment.Value))
        {
            throw new InvalidDataException("Adjustment value must be a finite number.");
        }

        if (targetWindow == nint.Zero && adjustment.Parameter is
                AdjustableParameter.VerticalScroll or AdjustableParameter.HorizontalScroll)
        {
            // Preview rings have no captured application window. Falling back to
            // SendInput here would feed the wheel event back into the overlay.
            return;
        }

        var value = checked((int)Math.Round(adjustment.Value));
        if (adjustment.Parameter == AdjustableParameter.Zoom)
        {
            if (adjustment.Value == 0)
            {
                return;
            }

            var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(adjustment.Value)), 1, 20);
            var key = adjustment.Value > 0 ? 0xBB : 0xBD;
            for (var index = 0; index < steps; index++)
            {
                await executor.ExecuteAsync(Shortcut(targetWindow, 0x11, key), cancellationToken);
            }
            return;
        }

        WindowsActions.WindowsAction action = adjustment.Parameter switch
        {
            AdjustableParameter.SystemVolume => new WindowsActions.SystemVolumeAction(
                adjustment.Value,
                adjustment.Mode == AdjustmentMode.Absolute
                    ? WindowsActions.ValueAdjustmentMode.Absolute
                    : WindowsActions.ValueAdjustmentMode.Delta),
            AdjustableParameter.ScreenBrightness => new WindowsActions.BrightnessAction(
                value,
                adjustment.Mode == AdjustmentMode.Absolute
                    ? WindowsActions.ValueAdjustmentMode.Absolute
                    : WindowsActions.ValueAdjustmentMode.Delta),
            AdjustableParameter.Zoom => throw new InvalidOperationException("Zoom is handled before dispatch."),
            AdjustableParameter.VerticalScroll => new WindowsActions.MouseWheelAction(
                value * 120,
                TargetWindow: targetWindow),
            AdjustableParameter.HorizontalScroll => new WindowsActions.MouseWheelAction(
                value * 120,
                Horizontal: true,
                TargetWindow: targetWindow),
            AdjustableParameter.Custom => throw new NotSupportedException(
                $"No handler is installed for parameter '{adjustment.CustomParameterId}'."),
            _ => throw new ArgumentOutOfRangeException(nameof(adjustment)),
        };
        await executor.ExecuteAsync(action, cancellationToken);
    }

    private static WindowsActions.KeyboardShortcutAction Shortcut(params int[] virtualKeys) => new(virtualKeys);

    private static WindowsActions.KeyboardShortcutAction Shortcut(nint targetWindow, params int[] virtualKeys) =>
        new(virtualKeys, targetWindow);

    private static IEnumerable<int> ModifierVirtualKeys(KeyboardModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyboardModifiers.Control)) yield return 0x11;
        if (modifiers.HasFlag(KeyboardModifiers.Alt)) yield return 0x12;
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) yield return 0x10;
        if (modifiers.HasFlag(KeyboardModifiers.Windows)) yield return 0x5B;
    }
}
