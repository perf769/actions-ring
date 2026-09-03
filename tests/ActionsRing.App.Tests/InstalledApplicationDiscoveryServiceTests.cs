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
