using System.Security.Cryptography;
using System.Text;
using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class ApplicationVisualServiceTests
{
    [TestMethod]
    public void FaviconDiscoveryHonorsRelIconsAndSkipsUnsafeReferences()
    {
        var candidates = ApplicationVisualService.FindFaviconCandidates("<link rel='stylesheet' href='/style.css'><link rel='apple-touch-icon' href='/touch.png'><link rel='icon' sizes='32x32' href='/icon.svg'><link rel='icon' href='http://127.0.0.1/private'>", new Uri("https://example.com"));
        CollectionAssert.AreEqual(new[] { "https://example.com/icon.svg", "https://example.com/touch.png" }, candidates.Select(uri => uri.AbsoluteUri).ToArray());
        Assert.IsFalse(ApplicationVisualService.IsPublicHttpUri(new Uri("http://192.168.1.2/favicon.ico")));
        Assert.IsFalse(ApplicationVisualService.IsPublicHttpUri(new Uri("https://user:password@example.com/favicon.ico")));
    }

    [TestMethod]
    public async Task ChatGptFaviconIsAvailableOfflineAndContrastAware()
    {
        var service = new ApplicationVisualService();
        Assert.AreEqual("brand:openai", await service.GetFaviconAsync("https://chatgpt.com"));
        Assert.IsNotNull(service.TryLoadIcon("brand:openai"));
    }

    [TestMethod]
    public void TryLoadIcon_CorruptBitmapReturnsNull()
    {
        using var workspace = new TemporaryWorkspace();
        var corrupt = Path.Combine(workspace.DirectoryPath, "corrupt.png");
        File.WriteAllText(corrupt, "not an image");

        var result = new ApplicationVisualService(workspace.DirectoryPath).TryLoadIcon(corrupt);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task CacheApplicationIconAsync_ReplacesCorruptCacheEntry()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The test host path is unavailable.");
        Assert.IsTrue(File.Exists(executable));
        var application = new InstalledApplicationInfo(
            "Test host",
            executable,
            Path.GetFileNameWithoutExtension(executable),
            executable,
            IconPath: Path.Combine(workspace.DirectoryPath, "missing-icon.png"));
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable)));
        var expected = Path.Combine(workspace.DirectoryPath, "apps", keyHash + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        await File.WriteAllTextAsync(expected, "incomplete cache entry");
        var service = new ApplicationVisualService(workspace.DirectoryPath);

        var result = await service.CacheApplicationIconAsync(application);

        Assert.AreEqual(expected, result);
        Assert.IsTrue(new FileInfo(expected).Length > "incomplete cache entry".Length);
        Assert.IsNotNull(service.TryLoadIcon(expected));
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ActionsRing.App.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
