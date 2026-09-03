using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ActionsRing.App.Services;

public sealed record InstalledApplicationInfo(
    string Name,
    string LaunchTarget,
    string ProcessName,
    string? ExecutablePath = null,
    string? AppUserModelId = null,
    string? IconPath = null,
    bool IsPackaged = false)
{
    public string SearchText => string.Join(
        ' ',
        new[] { Name, ProcessName, ExecutablePath, AppUserModelId }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class InstalledApplicationDiscoveryService
{
    private static readonly string[] AppPathsRegistryLocations =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
    ];

    public Task<IReadOnlyList<InstalledApplicationInfo>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<InstalledApplicationInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Discover(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Actions Ring application discovery",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static IReadOnlyList<InstalledApplicationInfo> MergeAndSort(
        IEnumerable<InstalledApplicationInfo> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var unique = new Dictionary<string, InstalledApplicationInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications)
        {
            if (string.IsNullOrWhiteSpace(application.Name)
                || string.IsNullOrWhiteSpace(application.LaunchTarget))
            {
                continue;
            }

            var key = application.AppUserModelId ?? application.LaunchTarget;
            if (!unique.TryGetValue(key, out var current)
                || Score(application) > Score(current))
            {
                unique[key] = application;
            }
        }

        return unique.Values
            .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(application => application.LaunchTarget, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        static int Score(InstalledApplicationInfo application) =>
            (application.IconPath is null ? 0 : 4)
            + (application.ExecutablePath is null ? 0 : 2)
            + (application.IsPackaged ? 1 : 0);
    }

    private static IReadOnlyList<InstalledApplicationInfo> Discover(CancellationToken cancellationToken)
    {
        var results = new List<InstalledApplicationInfo>();
        DiscoverAppPaths(results, cancellationToken);
        DiscoverStartMenuShortcuts(results, cancellationToken);
        DiscoverAppsFolder(results, cancellationToken);
        return MergeAndSort(results);
    }

    private static void DiscoverAppPaths(
        ICollection<InstalledApplicationInfo> results,
        CancellationToken cancellationToken)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var location in AppPathsRegistryLocations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var appPaths = baseKey.OpenSubKey(location);
                        if (appPaths is null)
                        {
                            continue;
                        }

                        foreach (var keyName in appPaths.GetSubKeyNames())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            using var entry = appPaths.OpenSubKey(keyName);
                            var executable = entry?.GetValue(null) as string;
                            if (!TryNormalizeExecutable(executable, out var normalized))
                            {
                                continue;
                            }

                            results.Add(CreateClassic(normalized));
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Error("Could not read an application registry location", exception);
                    }
                }
            }
        }
    }

    private static void DiscoverStartMenuShortcuts(
        ICollection<InstalledApplicationInfo> results,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };

        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            foreach (var root in roots.Where(Directory.Exists))
            {
                IReadOnlyList<string> shortcuts;
                try
                {
                    shortcuts = Directory.EnumerateFiles(
                            root,
                            "*.lnk",
                            new EnumerationOptions
                            {
                                RecurseSubdirectories = true,
                                IgnoreInaccessible = true,
                                MatchCasing = MatchCasing.CaseInsensitive,
                                ReturnSpecialDirectories = false,
                            })
                        .ToArray();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Error("Could not enumerate Start menu shortcuts", exception);
                    continue;
                }

                foreach (var shortcutPath in shortcuts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    object? shortcut = null;
                    try
                    {
                        shortcut = shellType.InvokeMember(
                            "CreateShortcut",
                            System.Reflection.BindingFlags.InvokeMethod,
                            binder: null,
                            shell,
                            [shortcutPath]);
                        var target = shortcut?.GetType().InvokeMember(
                            "TargetPath",
                            System.Reflection.BindingFlags.GetProperty,
                            binder: null,
                            shortcut,
                            null) as string;
                        if (TryNormalizeExecutable(target, out var executable))
                        {
                            var classic = CreateClassic(executable);
                            results.Add(classic with { Name = Path.GetFileNameWithoutExtension(shortcutPath) });
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Error("Could not read a Start menu shortcut", exception);
                    }
                    finally
                    {
                        ReleaseComObject(shortcut);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Error("Could not inspect Start menu applications", exception);
        }
        finally
        {
            ReleaseComObject(shell);
        }
    }

    private static void DiscoverAppsFolder(
        ICollection<InstalledApplicationInfo> results,
        CancellationToken cancellationToken)
    {
        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return;
            }
            shell = Activator.CreateInstance(shellType);
            folder = shellType.InvokeMember(
                "NameSpace",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                shell,
                ["shell:AppsFolder"]);
            items = folder?.GetType().InvokeMember(
                "Items",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                folder,
                null);
            if (items is not System.Collections.IEnumerable enumerable)
            {
                return;
            }

            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var name = GetComString(item, "Name");
                    var aumid = GetExtendedProperty(item, "System.AppUserModel.ID")
                                ?? GetComString(item, "Path");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(aumid))
                    {
                        continue;
                    }

                    aumid = aumid.Trim();

                    var installPath = GetExtendedProperty(item, "System.AppUserModel.PackageInstallPath");
                    var manifest = TryReadPackageManifest(installPath, aumid);
                    results.Add(new InstalledApplicationInfo(
                        name.Trim(),
                        $@"shell:AppsFolder\{aumid}",
                        manifest.ProcessName ?? GuessProcessName(name, aumid),
                        manifest.ExecutablePath,
                        aumid,
                        manifest.IconPath,
                        IsPackaged: true));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Error("Could not read an AppsFolder entry", exception);
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Error("Could not enumerate AppsFolder", exception);
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static InstalledApplicationInfo CreateClassic(string executable)
    {
        var processName = Path.GetFileNameWithoutExtension(executable);
        var name = processName;
        try
        {
            var productName = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductName;
            if (!string.IsNullOrWhiteSpace(productName))
            {
                name = productName.Trim();
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Could not read application product information", exception);
        }

        return new InstalledApplicationInfo(
            name,
            executable,
            processName,
            executable,
            IconPath: executable);
    }

    private static (string? ExecutablePath, string? ProcessName, string? IconPath) TryReadPackageManifest(
        string? installPath,
        string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return default;
        }

        var manifestPath = Path.Combine(installPath, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return default;
        }

        var document = XDocument.Load(manifestPath, LoadOptions.None);
        var appId = appUserModelId[(appUserModelId.LastIndexOf('!') + 1)..];
        var application = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Application"
                                       && string.Equals(
                                           element.Attribute("Id")?.Value,
                                           appId,
                                           StringComparison.OrdinalIgnoreCase));
        application ??= document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Application");
        var executableRelative = application?.Attribute("Executable")?.Value;
        string? executable = null;
        if (!string.IsNullOrWhiteSpace(executableRelative))
        {
            var candidate = Path.GetFullPath(
                Path.Combine(installPath, executableRelative.Replace('/', '\\')));
            if (File.Exists(candidate))
            {
                executable = candidate;
            }
        }

        var visual = application?.Elements().FirstOrDefault(element => element.Name.LocalName == "VisualElements");
        var logoRelative = visual?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Square44x44Logo")?.Value;
        var icon = ResolvePackageAsset(installPath, logoRelative);
        return (
            executable,
            executable is null ? null : Path.GetFileNameWithoutExtension(executable),
            icon);
    }

    private static string? ResolvePackageAsset(string installPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(installPath, relativePath.Replace('/', '\\')));
        if (File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, stem + ".*" + extension)
            .OrderByDescending(candidate => candidate.Contains("targetsize-64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => candidate.Contains("scale-200", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static bool TryNormalizeExecutable(string? value, out string executable)
    {
        executable = (value ?? string.Empty).Trim().Trim('"');
        if (executable.Length == 0)
        {
            return false;
        }

        executable = Environment.ExpandEnvironmentVariables(executable);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable))
        {
            return false;
        }

        executable = Path.GetFullPath(executable);
        var name = Path.GetFileName(executable);
        return !name.Contains("unins", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("setup", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("update", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessProcessName(string name, string aumid)
    {
        var appId = aumid[(aumid.LastIndexOf('!') + 1)..];
        return string.Equals(appId, "App", StringComparison.OrdinalIgnoreCase)
            ? name.Replace(" ", string.Empty, StringComparison.Ordinal)
            : appId;
    }

    private static string? GetComString(object? instance, string property) =>
        instance?.GetType().InvokeMember(
            property,
            System.Reflection.BindingFlags.GetProperty,
            binder: null,
            instance,
            null)?.ToString();

    private static string? GetExtendedProperty(object? item, string property)
    {
        var value = item?.GetType().InvokeMember(
            "ExtendedProperty",
            System.Reflection.BindingFlags.InvokeMethod,
            binder: null,
            item,
            [property])?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
