using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class InstalledApplicationDiscoveryServiceTests
{
    [TestMethod]
    public void MergeAndSort_DeduplicatesCaseInsensitivelyAndKeepsRicherEntry()
    {
        var applications = new[]
        {
            new InstalledApplicationInfo("Browser", @"C:\Apps\browser.exe", "browser"),
            new InstalledApplicationInfo("Browser", @"c:\apps\BROWSER.exe", "browser", @"c:\apps\BROWSER.exe", IconPath: @"c:\apps\BROWSER.exe"),
            new InstalledApplicationInfo("ChatGPT", @"shell:AppsFolder\Vendor.ChatApp_abc!App", "ChatGPT", AppUserModelId: "Vendor.ChatApp_abc!App", IsPackaged: true),
        };

        var result = InstalledApplicationDiscoveryService.MergeAndSort(applications);

        Assert.AreEqual(2, result.Count);
        Assert.IsNotNull(result.Single(item => item.Name == "Browser").IconPath);
        Assert.IsTrue(result.Single(item => item.Name == "ChatGPT").IsPackaged);
    }

    [TestMethod]
    public void MergeAndSort_MergesDesktopAliasesByExecutableNotAumid()
    {
        var applications = new[]
        {
            new InstalledApplicationInfo("4K Video Downloader+", @"C:\Apps\video.exe", "video", @"C:\Apps\video.exe"),
            new InstalledApplicationInfo("4K Video Downloader+", @"shell:AppsFolder\Vendor.Video", "video", @"c:\apps\VIDEO.exe", "Vendor.Video", @"C:\Apps\video.exe"),
            new InstalledApplicationInfo("Video", @"shell:AppsFolder\{KnownFolder}\Apps\video.exe", "video", @"C:\Apps\.\video.exe", @"{KnownFolder}\Apps\video.exe"),
        };

        var result = InstalledApplicationDiscoveryService.MergeAndSort(applications);

        Assert.AreEqual(1, result.Count);
        Assert.IsFalse(result[0].IsPackaged);
        Assert.IsNotNull(result[0].IconPath);
    }

    [TestMethod]
    public void MergeAndSort_DoesNotMergeSameNameDifferentInstallsOrPackages()
    {
        var applications = new[]
        {
            new InstalledApplicationInfo("Editor", @"C:\Apps\Stable\editor.exe", "editor", @"C:\Apps\Stable\editor.exe"),
            new InstalledApplicationInfo("Editor", @"C:\Apps\Beta\editor.exe", "editor", @"C:\Apps\Beta\editor.exe"),
            new InstalledApplicationInfo("Editor", @"shell:AppsFolder\Vendor.Editor_pub!First", "host", @"C:\Apps\host.exe", "Vendor.Editor_pub!First", IsPackaged: true),
            new InstalledApplicationInfo("Editor", @"shell:AppsFolder\Vendor.Editor_pub!Second", "host", @"C:\Apps\host.exe", "Vendor.Editor_pub!Second", IsPackaged: true),
        };

        Assert.AreEqual(4, InstalledApplicationDiscoveryService.MergeAndSort(applications).Count);
    }

    [TestMethod]
    public void MergeAndSort_PreservesShortcutLaunchArgumentsAndCaseSensitiveVariants()
    {
        var applications = new[]
        {
            new InstalledApplicationInfo("Browser", @"C:\Apps\browser.exe", "browser", @"C:\Apps\browser.exe"),
            new InstalledApplicationInfo("Web app", @"C:\Menu\web.lnk", "browser", @"C:\Apps\browser.exe", LaunchArguments: "--app=https://example.com/Work"),
            new InstalledApplicationInfo("Web app", @"shell:AppsFolder\Browser.WebApp", "browser", @"c:\Apps\browser.exe", "Browser.WebApp", LaunchArguments: "--app=https://example.com/Work"),
            new InstalledApplicationInfo("Other web app", @"C:\Menu\other.lnk", "browser", @"C:\Apps\browser.exe", LaunchArguments: "--app=https://example.com/work"),
        };

        var result = InstalledApplicationDiscoveryService.MergeAndSort(applications);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(@"C:\Menu\web.lnk", result.Single(item => item.Name == "Web app").LaunchTarget);
    }

    [TestMethod]
    public void AppsFolderEntry_DesktopWithExplicitAumidUsesActualExecutable()
    {
        var executable = Environment.ProcessPath!;
        var entry = InstalledApplicationDiscoveryService.CreateAppsFolderEntry(
            "Desktop app", "Vendor.Desktop!App", null, null, null, executable);

        Assert.IsNotNull(entry);
        Assert.IsFalse(entry.IsPackaged);
        Assert.AreEqual(executable, entry.ExecutablePath);
        Assert.AreEqual(executable, entry.LaunchTarget);
        Assert.AreEqual(Path.GetFileNameWithoutExtension(executable), entry.ProcessName);
    }

    [TestMethod]
    public void AppsFolderEntry_DesktopWithArgumentsKeepsShellLaunch()
    {
        var entry = InstalledApplicationDiscoveryService.CreateAppsFolderEntry(
            "Web app", "Browser.ProfileApp", null, null, null, Environment.ProcessPath!, "--app=https://example.com");

        Assert.IsNotNull(entry);
        Assert.IsFalse(entry.IsPackaged);
        Assert.AreEqual(@"shell:AppsFolder\Browser.ProfileApp", entry.LaunchTarget);
        Assert.AreEqual("--app=https://example.com", entry.LaunchArguments);
    }

    [TestMethod]
    public void AppsFolderEntry_TruePackageRemainsSelectableWithoutManifest()
    {
        var entry = InstalledApplicationDiscoveryService.CreateAppsFolderEntry(
            "ChatGPT", "OpenAI.Codex_publisher!App", "OpenAI.Codex_publisher",
            "OpenAI.Codex_1.0.0.0_x64__publisher", null, null);

        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.IsPackaged);
        Assert.AreEqual(@"shell:AppsFolder\OpenAI.Codex_publisher!App", entry.LaunchTarget);
        Assert.AreEqual("ChatGPT", entry.ProcessName);
    }

    [TestMethod]
    public void AppsFolderEntry_IgnoresDocumentsFoldersAndMissingExecutables()
    {
        foreach (var target in new[] { @"C:\Manual.pdf", @"C:\Examples", @"C:\MissingApp\application.exe" })
        {
            Assert.IsNull(InstalledApplicationDiscoveryService.CreateAppsFolderEntry(
                "Not installed", "Vendor.Shortcut", null, null, null, target));
        }
    }

    [TestMethod]
    public void AppsFolderEntry_NamespaceOnlyDesktopIsNotMarkedPackaged()
    {
        var entry = InstalledApplicationDiscoveryService.CreateAppsFolderEntry(
            "Desktop registration", "Vendor.Desktop", null, null, null, null);

        Assert.IsNotNull(entry);
        Assert.IsFalse(entry.IsPackaged);
        Assert.AreEqual(@"shell:AppsFolder\Vendor.Desktop", entry.LaunchTarget);
    }

    [TestMethod]
    public async Task InstalledDesktopApps_WhenPresent_DoNotHaveFakePackagedDuplicates()
    {
        var applications = await new InstalledApplicationDiscoveryService().DiscoverAsync();
        foreach (var name in new[] { "3uTools(32bit)", "4K Video Downloader+" })
        {
            var matches = applications.Where(application => application.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.IsTrue(matches.All(application => !application.IsPackaged), name);
            Assert.AreEqual(
                matches.Length,
                matches.Select(application => application.ExecutablePath?.ToUpperInvariant() + "|" + application.LaunchArguments)
                    .Distinct(StringComparer.Ordinal).Count(),
                name);
        }
    }

    [TestMethod]
    public async Task InstalledChatGpt_WhenPresent_HasLoadableAndCacheableIcon()
    {
        var applications = await new InstalledApplicationDiscoveryService().DiscoverAsync();
        var chatGpt = applications.FirstOrDefault(application =>
            application.IsPackaged
            && application.Name.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase));
        if (chatGpt is null)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "ActionsRing.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var visuals = new ApplicationVisualService(directory);
            Assert.IsFalse(string.IsNullOrWhiteSpace(chatGpt.ProcessName));
            Assert.AreEqual(
                $@"shell:AppsFolder\{chatGpt.AppUserModelId}",
                chatGpt.LaunchTarget,
                ignoreCase: true);
            if (chatGpt.ExecutablePath is not null)
            {
                Assert.IsTrue(File.Exists(chatGpt.ExecutablePath));
            }
            Assert.IsNotNull(visuals.TryLoadIcon($@"shell:AppsFolder\{chatGpt.AppUserModelId}"));
            var cached = await visuals.CacheApplicationIconAsync(chatGpt);

            Assert.IsNotNull(cached);
            Assert.IsTrue(File.Exists(cached));
            Assert.IsNotNull(visuals.TryLoadIcon(cached));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
