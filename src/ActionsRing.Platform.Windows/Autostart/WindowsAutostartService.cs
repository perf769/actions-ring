using Microsoft.Win32;

namespace ActionsRing.Platform.Windows;

public sealed record AutostartStatus(
    bool IsEnabled,
    string? Command,
    string? ExecutablePath,
    bool MatchesExpectedExecutable,
    bool IsRegistered,
    bool IsDisabledByWindows);

/// <summary>
/// Manages the current user's standard Run entry. This requires no elevation and
/// is surfaced by Windows Startup Apps / Task Manager.
/// </summary>
public sealed class WindowsAutostartService : IWindowsAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public AutostartStatus GetStatus(string valueName, string? expectedExecutablePath = null)
    {
        ValidateValueName(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var executablePath = TryReadExecutablePath(command);
        var isRegistered = command is not null;
        var isDisabledByWindows = isRegistered && IsDisabledInStartupApps(valueName);

        var matches = expectedExecutablePath is null
            ? command is not null
            : PathsEqual(executablePath, expectedExecutablePath);

        return new AutostartStatus(
            isRegistered && !isDisabledByWindows,
            command,
            executablePath,
            matches,
            isRegistered,
            isDisabledByWindows);
    }

    public void Enable(string valueName, string executablePath, string? arguments = null)
    {
        ValidateValueName(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The autostart executable does not exist.", fullPath);
        }

        var command = Quote(fullPath);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            command += " " + arguments.Trim();
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user Run registry key.");
        key.SetValue(valueName, command, RegistryValueKind.String);

        // Task Manager keeps the user's disabled state separately from the Run
        // entry. Removing that marker is the supported user intent of switching
        // autostart back on inside this application.
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void Disable(string valueName)
    {
        ValidateValueName(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static bool IsDisabledInStartupApps(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: false);
        var data = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
        return data is { Length: > 0 } && data[0] is 0x03 or 0x07;
    }

    private static void ValidateValueName(string valueName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string? TryReadExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var value = command.TrimStart();
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        var separator = value.IndexOfAny([' ', '\t']);
        return separator < 0 ? value : value[..separator];
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
